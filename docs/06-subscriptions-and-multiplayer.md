# Yo Mama — Backend for Subscriptions & Multiplayer

> Additions to the Go API (`02-api-spec.md`) and Postgres schema
> (`03-data-model.md`) that the Unity client (`05-unity-client.md`) needs for
> the paywall and Party rooms. Everything here is **additive to `/v1`** — no
> existing endpoint changes shape.

---

## 1. Principles

1. **The server decides who is Pro.** RevenueCat tells us via webhook; we store
   it in `entitlements`. The client's local purchase state is a hint for UI only.
2. **The server runs the game.** Party rooms are server-authoritative: the
   server picks jokes, runs the timer, counts votes, scores. Clients only send
   intents (`vote`, `start`).
3. **Webhooks are idempotent.** Every RevenueCat event id is stored once;
   replays are no-ops.

---

## 2. Subscriptions

### 2.1 Migration `0003_entitlements.up.sql`

```sql
-- Current access per user. One row per (user, entitlement).
CREATE TABLE entitlements (
    user_id      UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    entitlement  TEXT NOT NULL,                 -- 'pro'
    product_id   TEXT,                          -- 'yomama_pro_yearly', ...
    store        TEXT NOT NULL                  -- where it came from
                 CHECK (store IN ('app_store','play_store','promo')),
    expires_at   TIMESTAMPTZ,                   -- null = lifetime/promo
    will_renew   BOOLEAN NOT NULL DEFAULT true,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, entitlement)
);

-- Raw webhook log: audit trail + idempotency.
CREATE TABLE billing_events (
    id           TEXT PRIMARY KEY,              -- RevenueCat event.id
    user_id      UUID REFERENCES users(id) ON DELETE SET NULL,
    type         TEXT NOT NULL,                 -- INITIAL_PURCHASE, RENEWAL, EXPIRATION, ...
    payload      JSONB NOT NULL,
    received_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Daily free-tier swipe counter (cheap; one row per user per UTC day).
CREATE TABLE daily_usage (
    user_id      UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    day          DATE NOT NULL,
    swipes       INT  NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, day)
);
```

`0003_entitlements.down.sql`:

```sql
DROP TABLE IF EXISTS daily_usage;
DROP TABLE IF EXISTS billing_events;
DROP TABLE IF EXISTS entitlements;
```

### 2.2 sqlc queries (`api/db/queries/entitlements.sql`)

```sql
-- name: IsPro :one
SELECT EXISTS (
  SELECT 1 FROM entitlements
  WHERE user_id = $1 AND entitlement = 'pro'
    AND (expires_at IS NULL OR expires_at > now())
) AS pro;

-- name: GetEntitlement :one
SELECT entitlement, product_id, store, expires_at, will_renew
FROM entitlements WHERE user_id = $1 AND entitlement = $2;

-- name: UpsertEntitlement :exec
INSERT INTO entitlements (user_id, entitlement, product_id, store, expires_at, will_renew, updated_at)
VALUES ($1, $2, $3, $4, $5, $6, now())
ON CONFLICT (user_id, entitlement) DO UPDATE
SET product_id = EXCLUDED.product_id, store = EXCLUDED.store,
    expires_at = EXCLUDED.expires_at, will_renew = EXCLUDED.will_renew,
    updated_at = now();

-- name: InsertBillingEvent :one
-- Returns a row only when the event is new (idempotency guard).
INSERT INTO billing_events (id, user_id, type, payload)
VALUES ($1, $2, $3, $4)
ON CONFLICT (id) DO NOTHING
RETURNING id;

-- name: IncrementDailySwipes :one
INSERT INTO daily_usage (user_id, day, swipes)
VALUES ($1, (now() AT TIME ZONE 'utc')::date, 1)
ON CONFLICT (user_id, day) DO UPDATE SET swipes = daily_usage.swipes + 1
RETURNING swipes;

-- name: GetDailySwipes :one
SELECT coalesce((SELECT swipes FROM daily_usage
                 WHERE user_id = $1 AND day = (now() AT TIME ZONE 'utc')::date), 0)::int AS swipes;
```

### 2.3 Endpoints

#### `GET /v1/me/entitlements`

What the client is allowed to do *right now*. Called at boot, after a
purchase/restore, and when the app returns to the foreground.

**Response `200`**
```json
{
  "pro": true,
  "product_id": "yomama_pro_yearly",
  "expires_at": "2027-09-27T00:00:00Z",
  "will_renew": true,
  "limits": {
    "daily_swipes": null,
    "swipes_today": 112,
    "can_host_rooms": true,
    "max_room_players": 12
  }
}
```

For a free user: `"pro": false`, `daily_swipes: 30`, `can_host_rooms: false`,
`max_room_players: 8` (the cap for rooms they *join*; room size is set by the
host's tier).

#### Swipe cap on `POST /v1/swipes`

For non-Pro users, only **newly inserted** swipes count (idempotent retries
don't burn quota). When `swipes_today >= 30`:

```
402 Payment Required
{ "error": { "code": "daily_limit_reached", "message": "Free swipes used up for today." } }
```

The Unity deck shows the paywall card on `402`. `GET /v1/jokes/feed` is **not**
capped — the client just can't record more swipes, which keeps the feed simple.

#### `POST /v1/webhooks/revenuecat`

- **Auth:** RevenueCat sends a static `Authorization` header you set in its
  dashboard. Compare it in constant time against `REVENUECAT_WEBHOOK_AUTH`.
  No JWT.
- **Body:** `{ "event": { "id", "type", "app_user_id", "product_id",
  "entitlement_ids", "expiration_at_ms", "store", ... } }`.
- **Handling:**
  1. `InsertBillingEvent` — if it returns no row, it's a replay → `200`, stop.
  2. **Don't trust the event body for state.** Fetch the subscriber from the
     RevenueCat REST API (`GET /v1/subscribers/{app_user_id}`, secret key
     `REVENUECAT_SECRET_KEY`) and upsert the `pro` entitlement from that
     response. This makes out-of-order events harmless.
  3. Ignore `app_user_id`s that aren't valid UUIDs of our users (RevenueCat
     anonymous ids like `$RCAnonymousID:…`) — log and `200`.
  4. Push an `entitlements_changed` message to any open WebSocket for that
     user (optional nicety; the client also polls).
- Always return `200` quickly once stored; RevenueCat retries non-2xx.

### 2.4 Go sketch

```go
func (h *Handler) RevenueCatWebhook(w http.ResponseWriter, r *http.Request) {
    if subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), h.cfg.RCWebhookAuth) != 1 {
        writeErr(w, 401, "invalid_token", "bad webhook auth"); return
    }
    var body struct{ Event rcEvent `json:"event"` }
    if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
        writeErr(w, 400, "invalid_body", err.Error()); return
    }
    userID, err := uuid.Parse(body.Event.AppUserID)
    if err != nil { w.WriteHeader(200); return } // not one of ours

    if _, err := h.q.InsertBillingEvent(r.Context(), /* id, userID, type, raw */); errors.Is(err, pgx.ErrNoRows) {
        w.WriteHeader(200); return // replay
    }
    sub, err := h.rc.GetSubscriber(r.Context(), userID.String()) // source of truth
    if err != nil { writeErr(w, 500, "internal", "revenuecat fetch"); return } // RC will retry
    if err := h.q.UpsertEntitlement(r.Context(), sub.ProParams(userID)); err != nil {
        writeErr(w, 500, "internal", "upsert"); return
    }
    h.hub.NotifyUser(userID, "entitlements_changed")
    w.WriteHeader(200)
}
```

> Note: if the RevenueCat fetch fails after the event row was stored, the
> retry would be treated as a replay. Either insert the event row **after** the
> upsert succeeds, or wrap both in one transaction — the sketch above keeps the
> order readable; do it transactionally in the real handler.

### 2.5 Config

| Env var | Purpose |
|---|---|
| `REVENUECAT_WEBHOOK_AUTH` | Shared secret RevenueCat sends in `Authorization` |
| `REVENUECAT_SECRET_KEY` | REST API key for `GET /v1/subscribers/{id}` |
| `FREE_DAILY_SWIPES` | Default `30` |

---

## 3. Rooms — REST

Rooms are **ephemeral and in-memory** in the Go process (a `map[code]*Room`
behind a mutex, one goroutine per room). Nothing about a live room needs to
survive a deploy. See §5 for scaling past one instance.

#### `POST /v1/rooms` — create (Pro only)

```json
// 201
{ "code": "KX7QP", "ws_url": "wss://api.yomama.app/v1/rooms/KX7QP/ws", "max_players": 12 }
```
- `403 pro_required` if the caller isn't Pro (`IsPro` false).
- `429 too_many_rooms` if they already host an open room.
- Code: 5 chars from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789` (no 0/O/1/I), retried on collision.

#### `POST /v1/rooms/{code}/join`

```json
// 200
{ "code": "KX7QP", "ws_url": "wss://api.yomama.app/v1/rooms/KX7QP/ws" }
```
- `404 room_not_found`, `409 room_full`, `409 room_in_progress` (joining
  mid-game is allowed only for players reconnecting).

#### `GET /v1/rooms/{code}/ws` — WebSocket upgrade

- Auth: `Authorization: Bearer <jwt>` header (NativeWebSocket supports custom
  headers on iOS/Android). WebGL can't set headers → also accept
  `?token=` there and **never log query strings**.
- Library: `github.com/coder/websocket` (context-aware, maintained).

---

## 4. Rooms — WebSocket protocol

JSON text frames, every message has a `type`. Server → client messages carry
`seq` (monotonic per room) so a client can drop stale ones.

### 4.1 Client → server

| `type` | Fields | Who | Notes |
|---|---|---|---|
| `hello` | `name` (≤ 16 chars) | anyone | First message after connect. Display name. |
| `start` | `rounds` (5–15, default 10) | host | Lobby → playing. Needs ≥ 2 players. |
| `vote` | `round`, `direction` (`left`/`right`) | player | One per round; later votes ignored. |
| `next` | — | host | Skip the reveal wait. |
| `kick` | `player_id` | host | |
| `ping` | — | anyone | Keepalive every 20 s (server replies `pong`). |

### 4.2 Server → client

| `type` | Fields |
|---|---|
| `room_state` | Full snapshot: `code, phase, host_id, players[{id,name,score,connected}], round, total_rounds, joke?, deadline?` — sent on connect/reconnect and on any membership change |
| `round_start` | `round, joke{id,body}, deadline` (RFC 3339, server clock) |
| `vote_progress` | `round, voted, total` (never reveals *who* voted which way) |
| `round_result` | `round, loves, nopes, majority, scores[{id,score,delta}]` |
| `game_over` | `podium[{id,name,score}]` |
| `entitlements_changed` | — (client should re-fetch `/v1/me/entitlements`) |
| `error` | `code, message` (e.g. `not_host`, `bad_round`) |
| `pong` | — |

### 4.3 Room lifecycle (server)

```
lobby ──start──► round_open (10 s or all voted) ──► reveal (4 s or host `next`)
                     ▲                                   │
                     └─────────── round < total ─────────┘
                                                         │ round == total
                                                         ▼
                                                     game_over ──(60 s)──► closed
```

- **Jokes:** at `start` the server picks `rounds` approved jokes at random
  (weighted to well-liked ones), preferring jokes nobody in the room has swiped.
- **Scoring:** +100 for voting with the majority, +50 bonus for being one of
  the first half to vote. Ties (equal loves/nopes) → everyone who voted scores.
- **Disconnects:** a player keeps their seat for 60 s (`connected:false`). If
  the **host** leaves for > 60 s, host passes to the longest-connected player —
  the room keeps its original size cap even though the new host may be free.
- **Timeouts:** empty rooms close after 2 min; lobbies idle > 15 min close.
- **Moderation:** rooms only ever show `approved` jokes; names go through the
  same blocklist as joke submissions.

### 4.4 Go shape

```go
type Room struct {
    Code       string
    HostID     uuid.UUID
    MaxPlayers int
    in         chan roomCmd     // all mutations go through here → no locks on state
    players    map[uuid.UUID]*Player
    phase      Phase
    round      int
    jokes      []store.Joke
    votes      map[uuid.UUID]string
    seq        int64
}

// One goroutine per room owns all state; connections just forward commands.
func (r *Room) run(ctx context.Context) { for { select { case cmd := <-r.in: r.handle(cmd); case <-r.timer.C: r.tick() ; case <-ctx.Done(): return } } }
```

---

## 5. Scaling notes (later, not now)

- **One API instance is fine for a long time** — a room is a few KB and a
  goroutine. Thousands of concurrent rooms fit on a small VM.
- Going multi-instance: route `/v1/rooms/{code}/*` to the owning instance
  (store `code → instance` in Redis, or use Fly.io's `fly-replay` header), rather
  than syncing room state across instances.
- Persist finished games (`games`, `game_players`) only if you want history or
  leaderboards.

---

## 6. OpenAPI additions (merge into `api/openapi.yaml`)

```yaml
paths:
  /v1/me/entitlements:
    get:
      operationId: getEntitlements
      responses:
        "200":
          description: Current access
          content:
            application/json:
              schema: { $ref: '#/components/schemas/Entitlements' }
  /v1/rooms:
    post:
      operationId: createRoom
      responses:
        "201":
          description: Room created
          content:
            application/json:
              schema: { $ref: '#/components/schemas/RoomTicket' }
        "403": { $ref: '#/components/responses/Error' }
  /v1/rooms/{code}/join:
    post:
      operationId: joinRoom
      parameters:
        - { name: code, in: path, required: true, schema: { type: string, pattern: '^[A-Z2-9]{5}$' } }
      responses:
        "200":
          description: Joinable
          content:
            application/json:
              schema: { $ref: '#/components/schemas/RoomTicket' }
        "404": { $ref: '#/components/responses/Error' }
        "409": { $ref: '#/components/responses/Error' }
  /v1/webhooks/revenuecat:
    post:
      operationId: revenueCatWebhook
      security: []   # static shared-secret header instead of JWT
      responses:
        "200": { description: Accepted }

components:
  schemas:
    Entitlements:
      type: object
      required: [pro, limits]
      properties:
        pro:        { type: boolean }
        product_id: { type: [string, "null"] }
        expires_at: { type: [string, "null"], format: date-time }
        will_renew: { type: boolean }
        limits:
          type: object
          required: [daily_swipes, swipes_today, can_host_rooms, max_room_players]
          properties:
            daily_swipes:     { type: [integer, "null"] }
            swipes_today:     { type: integer }
            can_host_rooms:   { type: boolean }
            max_room_players: { type: integer }
    RoomTicket:
      type: object
      required: [code, ws_url]
      properties:
        code:        { type: string }
        ws_url:      { type: string }
        max_players: { type: integer }
```

New error codes: `402 daily_limit_reached`, `403 pro_required`,
`404 room_not_found`, `409 room_full`, `409 room_in_progress`,
`429 too_many_rooms`.
