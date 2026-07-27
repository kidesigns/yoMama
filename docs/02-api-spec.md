# Yo Mama — API Spec (v1)

> The contract between the React Native app and the Go service. This is the
> source of truth: the Go handlers implement it, and the RN app's typed client
> is generated from the OpenAPI block at the bottom.

- **Base URL:** `https://api.yomama.app` (local: `http://localhost:8080`)
- **Prefix:** all endpoints under `/v1`
- **Format:** JSON in, JSON out. UTF-8. `Content-Type: application/json`.
- **Auth:** `Authorization: Bearer <jwt>` on every endpoint except `POST /v1/auth/anon`.
- **IDs:** UUID v4 strings.
- **Timestamps:** RFC 3339 / ISO 8601 UTC (`2026-07-25T18:30:00Z`).

---

## 1. Conventions

### Auth

Anonymous-first (see `01-architecture.md` §6). The very first call an app makes
is `POST /v1/auth/anon` with a device-generated id; every later call carries the
returned JWT.

```
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

### Pagination — cursor, not offset

The feed and lists are streams. Endpoints return an opaque `next_cursor`; pass it
back as `?cursor=`. `null` means "end of list."

```
GET /v1/jokes/feed?limit=20
  → { "jokes": [...], "next_cursor": "b2Zmc2V0OjIw" }
GET /v1/jokes/feed?limit=20&cursor=b2Zmc2V0OjIw
  → { "jokes": [...], "next_cursor": null }
```

- `limit` default **20**, max **50**.
- Treat the cursor as opaque — don't parse it client-side.

### Errors

Every non-2xx response uses one shape:

```json
{
  "error": {
    "code": "joke_not_found",
    "message": "No joke exists with that id."
  }
}
```

| HTTP | `code` examples | When |
|---|---|---|
| 400 | `invalid_body`, `invalid_direction` | Malformed request |
| 401 | `missing_token`, `invalid_token` | Auth failed |
| 404 | `joke_not_found` | Resource missing |
| 409 | `already_swiped` | Duplicate swipe (usually treated as success — see §3.2) |
| 422 | `joke_too_long`, `joke_empty` | Validation failed |
| 429 | `rate_limited` | Too many requests (see below) |
| 500 | `internal` | Server fault |

### Rate limits

Applied per user (JWT). On limit: `429` + `Retry-After` header (seconds).

| Endpoint | Limit |
|---|---|
| `POST /v1/swipes` | 60 / min |
| `POST /v1/jokes` | 10 / hour |
| everything else | 120 / min |

### Idempotency

`POST /v1/swipes` is naturally idempotent per `(user, joke)` — re-sending the
same swipe returns `200`, not an error. See §3.2.

---

## 2. Auth

### `POST /v1/auth/anon`

Create (or re-attach to) an anonymous user for a device and get a token.
**No auth header required.**

**Request**
```json
{ "device_id": "550e8400-e29b-41d4-a716-446655440000" }
```

**Response `200`**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "user": {
    "id": "1f2d3c4b-0000-4a11-9c22-abcdef123456",
    "email": null,
    "created_at": "2026-07-25T18:30:00Z"
  }
}
```

Calling again with the same `device_id` returns the **same user** and a fresh
token (tokens are short-lived; see §5).

---

## 3. Jokes & swipes

### 3.1 `GET /v1/jokes/feed`

The next batch of **approved** jokes this user hasn't swiped yet, best first.

**Query params**

| Param | Type | Default | Notes |
|---|---|---|---|
| `limit` | int | 20 | 1–50 |
| `cursor` | string | — | From a previous response |

**Response `200`**
```json
{
  "jokes": [
    {
      "id": "9a8b7c6d-1111-4e22-8f33-000000000001",
      "body": "Yo mama so slow, she took a screenshot of the loading spinner.",
      "like_count": 421,
      "created_at": "2026-06-01T12:00:00Z"
    }
  ],
  "next_cursor": "b2Zmc2V0OjIw"
}
```

> **Client tip:** prefetch the next page when the user is ~5 cards from the end
> of the current batch so the deck never blocks on the network.

### 3.2 `POST /v1/swipes`

Record a swipe. `right` = like (also saves to favorites), `left` = pass.

**Request**
```json
{ "joke_id": "9a8b7c6d-1111-4e22-8f33-000000000001", "direction": "right" }
```

**Response `200`**
```json
{
  "joke_id": "9a8b7c6d-1111-4e22-8f33-000000000001",
  "direction": "right",
  "favorited": true
}
```

- `direction` must be `"left"` or `"right"` → else `400 invalid_direction`.
- Swiping the **same joke twice** returns `200` with the original result (no
  double-counting). The counters in the DB only move on the first swipe.
- A `right` swipe creates a favorite; `favorited` reflects the resulting state.

### 3.3 `POST /v1/jokes`

Submit a new joke. Enters the moderation queue as `pending`; it will **not**
appear in anyone's feed until approved.

**Request**
```json
{ "body": "Yo mama so old, her social security number is 1." }
```

**Response `201`**
```json
{
  "id": "44445555-6666-4777-8888-999900001111",
  "status": "pending",
  "created_at": "2026-07-25T18:31:00Z"
}
```

**Validation**

| Rule | Failure |
|---|---|
| non-empty after trim | `422 joke_empty` |
| ≤ 280 chars | `422 joke_too_long` |
| passes blocklist | `422 joke_rejected` |

---

## 4. Favorites

### 4.1 `GET /v1/favorites`

Jokes the user swiped right on, newest first. Cursor-paginated.

**Response `200`**
```json
{
  "jokes": [
    {
      "id": "9a8b7c6d-1111-4e22-8f33-000000000001",
      "body": "Yo mama so slow, she took a screenshot of the loading spinner.",
      "like_count": 421,
      "favorited_at": "2026-07-25T18:30:05Z"
    }
  ],
  "next_cursor": null
}
```

### 4.2 `DELETE /v1/favorites/{jokeId}`

Un-save a joke. Idempotent — removing something already gone still returns `204`.

**Response `204`** (no body)

> Note: this removes the favorite but leaves the `swipes` record intact, so the
> joke stays "seen" and won't reappear in the feed.

---

## 5. Notes for implementers

- **Token lifetime:** access tokens ~30 days (anon users rarely re-auth). If you
  add email accounts later, switch to short access + refresh tokens.
- **Clock:** server is the authority on timestamps; ignore client-sent times.
- **Feed determinism:** the same user calling `/feed` twice without swiping may
  see overlap — that's fine; swipes de-duplicate it. The cursor is best-effort.
- **CORS:** the RN app doesn't need CORS, but a future web/admin client will —
  keep it configurable.
- **Versioning:** breaking changes go under `/v2`; additive fields are safe in
  `/v1`.

---

## 6. OpenAPI 3.1 (source of truth for the generated TS client)

Save this as `api/openapi.yaml`. Generate the mobile client with e.g.
`openapi-typescript` (types) + `openapi-fetch` (runtime), or `orval`.

```yaml
openapi: 3.1.0
info:
  title: Yo Mama API
  version: "1.0.0"
servers:
  - url: https://api.yomama.app
  - url: http://localhost:8080
security:
  - bearerAuth: []
paths:
  /v1/auth/anon:
    post:
      operationId: authAnon
      security: []            # no token required
      summary: Create/attach an anonymous user for a device
      requestBody:
        required: true
        content:
          application/json:
            schema: { $ref: '#/components/schemas/AuthAnonRequest' }
      responses:
        "200":
          description: Token + user
          content:
            application/json:
              schema: { $ref: '#/components/schemas/AuthAnonResponse' }
        "400": { $ref: '#/components/responses/Error' }

  /v1/jokes/feed:
    get:
      operationId: getFeed
      summary: Next batch of unseen approved jokes
      parameters:
        - { name: limit,  in: query, schema: { type: integer, minimum: 1, maximum: 50, default: 20 } }
        - { name: cursor, in: query, schema: { type: string } }
      responses:
        "200":
          description: A page of jokes
          content:
            application/json:
              schema: { $ref: '#/components/schemas/JokePage' }
        "401": { $ref: '#/components/responses/Error' }

  /v1/jokes:
    post:
      operationId: submitJoke
      summary: Submit a joke to the moderation queue
      requestBody:
        required: true
        content:
          application/json:
            schema: { $ref: '#/components/schemas/SubmitJokeRequest' }
      responses:
        "201":
          description: Created (pending)
          content:
            application/json:
              schema: { $ref: '#/components/schemas/SubmitJokeResponse' }
        "422": { $ref: '#/components/responses/Error' }

  /v1/swipes:
    post:
      operationId: createSwipe
      summary: Record a swipe (idempotent per user+joke)
      requestBody:
        required: true
        content:
          application/json:
            schema: { $ref: '#/components/schemas/SwipeRequest' }
      responses:
        "200":
          description: Swipe recorded
          content:
            application/json:
              schema: { $ref: '#/components/schemas/SwipeResponse' }
        "400": { $ref: '#/components/responses/Error' }

  /v1/favorites:
    get:
      operationId: getFavorites
      summary: Jokes the user swiped right on
      parameters:
        - { name: limit,  in: query, schema: { type: integer, minimum: 1, maximum: 50, default: 20 } }
        - { name: cursor, in: query, schema: { type: string } }
      responses:
        "200":
          description: A page of favorited jokes
          content:
            application/json:
              schema: { $ref: '#/components/schemas/FavoritePage' }

  /v1/favorites/{jokeId}:
    delete:
      operationId: deleteFavorite
      summary: Un-save a joke
      parameters:
        - { name: jokeId, in: path, required: true, schema: { type: string, format: uuid } }
      responses:
        "204": { description: Removed }

components:
  securitySchemes:
    bearerAuth:
      type: http
      scheme: bearer
      bearerFormat: JWT
  responses:
    Error:
      description: Error
      content:
        application/json:
          schema: { $ref: '#/components/schemas/Error' }
  schemas:
    Error:
      type: object
      required: [error]
      properties:
        error:
          type: object
          required: [code, message]
          properties:
            code:    { type: string }
            message: { type: string }
    AuthAnonRequest:
      type: object
      required: [device_id]
      properties:
        device_id: { type: string, format: uuid }
    User:
      type: object
      required: [id, created_at]
      properties:
        id:         { type: string, format: uuid }
        email:      { type: [string, "null"] }
        created_at: { type: string, format: date-time }
    AuthAnonResponse:
      type: object
      required: [token, user]
      properties:
        token: { type: string }
        user:  { $ref: '#/components/schemas/User' }
    Joke:
      type: object
      required: [id, body, like_count, created_at]
      properties:
        id:         { type: string, format: uuid }
        body:       { type: string }
        like_count: { type: integer }
        created_at: { type: string, format: date-time }
    JokePage:
      type: object
      required: [jokes, next_cursor]
      properties:
        jokes:
          type: array
          items: { $ref: '#/components/schemas/Joke' }
        next_cursor: { type: [string, "null"] }
    FavoriteJoke:
      type: object
      required: [id, body, like_count, favorited_at]
      properties:
        id:           { type: string, format: uuid }
        body:         { type: string }
        like_count:   { type: integer }
        favorited_at: { type: string, format: date-time }
    FavoritePage:
      type: object
      required: [jokes, next_cursor]
      properties:
        jokes:
          type: array
          items: { $ref: '#/components/schemas/FavoriteJoke' }
        next_cursor: { type: [string, "null"] }
    SwipeRequest:
      type: object
      required: [joke_id, direction]
      properties:
        joke_id:   { type: string, format: uuid }
        direction: { type: string, enum: [left, right] }
    SwipeResponse:
      type: object
      required: [joke_id, direction, favorited]
      properties:
        joke_id:   { type: string, format: uuid }
        direction: { type: string, enum: [left, right] }
        favorited: { type: boolean }
    SubmitJokeRequest:
      type: object
      required: [body]
      properties:
        body: { type: string, maxLength: 280 }
    SubmitJokeResponse:
      type: object
      required: [id, status, created_at]
      properties:
        id:         { type: string, format: uuid }
        status:     { type: string, enum: [pending, approved, rejected] }
        created_at: { type: string, format: date-time }
```
