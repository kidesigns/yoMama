# Yo Mama — Architecture

> A Tinder-style swipe app for "yo mama" jokes. Swipe right to save/love a joke,
> swipe left to skip. Simple to start, room to grow (accounts, submissions,
> ranking, sharing).

---

## 1. High-level shape

```
┌─────────────────────────┐        HTTPS/JSON         ┌──────────────────────┐
│   React Native (Expo)   │  ───────────────────────► │      Go API          │
│   - Swipe deck          │  ◄─────────────────────── │  (net/http + chi)    │
│   - Saved / Liked list  │                           │                      │
│   - Submit a joke       │                           └───────────┬──────────┘
└─────────────────────────┘                                       │
                                                                  │ SQL
                                                       ┌──────────▼──────────┐
                                                       │    PostgreSQL       │
                                                       │  jokes, users,      │
                                                       │  swipes, favorites  │
                                                       └─────────────────────┘
```

Three moving parts: a **React Native app**, a **stateless Go API**, and a
**PostgreSQL database**. Everything else (auth provider, object storage, CDN)
is optional and can be bolted on later.

---

## 2. Mobile app — React Native

**Recommendation: React Native with Expo (managed workflow).**

For a swipe-card app you get the fastest path to something on your phone.
Expo handles the native build toolchain, over-the-air updates, and gives you
`expo-haptics`, `expo-secure-store`, etc. out of the box. You can "eject" to a
bare workflow later if you ever need a custom native module — you almost
certainly won't for this app.

### Core libraries

| Concern | Library | Why |
|---|---|---|
| Navigation | `expo-router` (or React Navigation) | File-based routing, deep links |
| Swipe gestures | `react-native-gesture-handler` + `react-native-reanimated` | 60fps gestures on the UI thread |
| Card deck | `rn-swiper-list` or a hand-rolled deck on Reanimated | Deck stack + fling animation |
| Server state | TanStack Query (`@tanstack/react-query`) | Caching, prefetch, retries |
| Local/persistent state | Zustand + `expo-secure-store` | Tiny store; token in secure storage |
| Haptics | `expo-haptics` | Tick on swipe, thunk on like |

> **On the swipe interaction:** the "feel" is the whole product. Build the deck
> on **Reanimated + Gesture Handler** so the drag, rotation, and fling run on the
> UI thread (no JS-bridge jank). A pre-built list library is fine to prototype
> with, but expect to customize it.

### App structure

```
app/
  _layout.tsx          # root, providers (Query, theme)
  index.tsx            # the swipe deck (home)
  saved.tsx            # jokes you swiped right on
  submit.tsx           # submit-a-joke form
components/
  JokeCard.tsx         # single card
  Deck.tsx             # gesture + animation logic
lib/
  api.ts               # typed fetch client
  queries.ts           # TanStack Query hooks
  store.ts             # Zustand store
```

### Prefetching (important for a swipe app)

Load jokes in **batches of ~15–20** and prefetch the next batch when the user is
~5 cards from the end. The deck should never block on the network mid-swipe.

---

## 3. Backend API — Go vs the alternatives

**You asked specifically about Go. Short answer: Go is a good fit and I'd
happily use it here — with one honest trade-off you should know about.**

### Why Go works well for this

- **Simple, fast, boring in the best way.** A joke-serving API is mostly
  "read a row, return JSON." Go's stdlib `net/http` plus a light router (`chi`)
  gives you a fast, dependency-light service.
- **Single static binary.** Deploys as one file in a tiny container. Cheap to
  host (Fly.io, Railway, Cloud Run, a $5 VPS).
- **Great concurrency + low memory.** Handles lots of swipe writes without much
  tuning.
- **Strong typing + `pgx` + `sqlc`** gives you compile-time-checked SQL.

### The one trade-off

Your mobile app is **TypeScript**. If you write the API in **Go**, the client
and server types live in two languages, so you either:

- hand-write the TS types to match your Go structs, or
- generate an OpenAPI spec from Go and generate a TS client from it.

If you'd rather share types end-to-end for free, a **TypeScript backend**
(Node + Fastify, or Hono on Bun) lets you export the same types the app imports.
That's the main reason someone would *not* pick Go here.

### Recommendation

| If you want… | Pick |
|---|---|
| To learn/use Go, cheapest & most robust deploy, don't mind a codegen step | **Go + chi + sqlc + pgx** ✅ (what this doc assumes) |
| One language, shared types, fastest iteration solo | TypeScript (Fastify or Hono) |
| Batteries-included admin + auth with least code | Supabase / PocketBase (skip a custom API almost entirely) |

We'll proceed with **Go**, and add an **OpenAPI spec** so the RN app gets a
generated, type-safe client — that neutralizes most of the Go/TS split.

### Suggested Go stack

```
Router:      github.com/go-chi/chi/v5
DB driver:   github.com/jackc/pgx/v5
Queries:     sqlc (generates typed Go from .sql files)
Migrations:  golang-migrate  (or goose)
Config:      env vars (github.com/caarlos0/env)
Auth:        JWT (github.com/golang-jwt/jwt) — see §6
```

### Endpoints (v1)

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/v1/jokes/feed?cursor=&limit=20` | Next batch of unseen jokes |
| `POST` | `/v1/swipes` | Record a swipe `{joke_id, direction}` |
| `GET` | `/v1/favorites` | Jokes the user swiped right |
| `DELETE` | `/v1/favorites/:jokeId` | Un-save |
| `POST` | `/v1/jokes` | Submit a joke (goes to moderation queue) |
| `POST` | `/v1/auth/anon` | Get a token for an anonymous device |

Keep it **cursor-paginated**, not offset — the feed is a stream.

---

## 4. Database — PostgreSQL

**Recommendation: PostgreSQL.**

The data is relational (users ↔ swipes ↔ jokes) and you'll want simple
aggregate queries ("top jokes by like-ratio"). Postgres is the safe, scalable
default and is available managed everywhere (Supabase, Neon, RDS, Railway).

> SQLite would genuinely work for a solo/offline MVP and is even simpler, but
> it doesn't fit a multi-user hosted API as cleanly. Start on Postgres and you
> won't have to migrate later.

### Schema (v1)

```sql
-- Users (anonymous-first: a row can exist with no email)
CREATE TABLE users (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email        TEXT UNIQUE,              -- nullable: anon devices allowed
    device_id    TEXT UNIQUE,              -- ties an anon user to a device
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- The jokes themselves
CREATE TABLE jokes (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    body         TEXT NOT NULL,
    status       TEXT NOT NULL DEFAULT 'approved'  -- 'pending' | 'approved' | 'rejected'
                 CHECK (status IN ('pending','approved','rejected')),
    submitted_by UUID REFERENCES users(id),        -- null for seed jokes
    like_count   INT  NOT NULL DEFAULT 0,          -- denormalized for ranking
    pass_count   INT  NOT NULL DEFAULT 0,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Every swipe (this is your event log + "already seen" source of truth)
CREATE TABLE swipes (
    id          BIGSERIAL PRIMARY KEY,
    user_id     UUID NOT NULL REFERENCES users(id),
    joke_id     UUID NOT NULL REFERENCES jokes(id),
    direction   TEXT NOT NULL CHECK (direction IN ('left','right')),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (user_id, joke_id)     -- a user swipes a joke once
);

-- Fast lookup of a user's saved jokes.
-- (Could be derived from swipes, but a table makes un-saving trivial.)
CREATE TABLE favorites (
    user_id     UUID NOT NULL REFERENCES users(id),
    joke_id     UUID NOT NULL REFERENCES jokes(id),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, joke_id)
);

CREATE INDEX idx_swipes_user      ON swipes (user_id);
CREATE INDEX idx_jokes_status     ON jokes (status);
CREATE INDEX idx_jokes_ranking    ON jokes (like_count DESC);
```

### The one query that matters: the feed

"Give me approved jokes this user hasn't swiped yet."

```sql
SELECT j.id, j.body
FROM jokes j
WHERE j.status = 'approved'
  AND NOT EXISTS (
      SELECT 1 FROM swipes s
      WHERE s.user_id = $1 AND s.joke_id = j.id
  )
ORDER BY j.like_count DESC, j.created_at DESC
LIMIT $2;
```

When the joke catalog gets large, swap the `NOT EXISTS` for a keyset cursor and
consider caching each user's "seen" set. For thousands of jokes it's fine as-is.

### Recording a swipe (one transaction)

```sql
BEGIN;
INSERT INTO swipes (user_id, joke_id, direction) VALUES ($1, $2, $3)
  ON CONFLICT (user_id, joke_id) DO NOTHING;
UPDATE jokes
  SET like_count = like_count + ($3 = 'right')::int,
      pass_count = pass_count + ($3 = 'left')::int
  WHERE id = $2;
-- if right: INSERT INTO favorites ... ON CONFLICT DO NOTHING;
COMMIT;
```

---

## 5. Content moderation (don't skip this)

Yo-mama jokes are user-submittable and can get offensive fast. Submitted jokes
land in `status = 'pending'` and only appear in the feed after review. Options,
cheapest first:

1. **Manual approval** via a tiny admin endpoint / SQL — fine at the start.
2. **Word/regex blocklist** on submit for the obvious stuff.
3. **A moderation API** (e.g. an LLM or a hosted moderation endpoint) as volume
   grows.

Seed the DB with a curated set of clean jokes so the app isn't empty on day one.

---

## 6. Auth — start anonymous

Don't gate the fun behind a signup. Flow:

1. First launch: app generates a `device_id`, calls `POST /v1/auth/anon`.
2. API creates a `users` row, returns a **JWT**.
3. App stores the JWT in `expo-secure-store`, sends it as
   `Authorization: Bearer …`.
4. **Later**, a user can attach an email to upgrade the anon account (keeps
   their saved jokes).

This lets people swipe immediately while still tracking per-user "seen" state.

---

## 7. Suggested repo layout (monorepo)

```
yoMama/
  docs/                 # this doc + future design notes
  mobile/               # Expo React Native app
  api/                  # Go service
    cmd/server/
    internal/
      http/             # handlers, router, middleware
      store/            # sqlc-generated code + queries
      auth/
    db/
      migrations/
      queries/          # .sql files sqlc reads
    openapi.yaml        # source of truth for the client
  docker-compose.yml    # postgres + api for local dev
```

---

## 8. Suggested build order

1. **DB + migrations** — create the schema above, seed ~50 clean jokes.
2. **Go API** — `GET /v1/jokes/feed` and `POST /v1/swipes` only.
3. **RN deck** — get the swipe feel right against the two endpoints.
4. **Favorites screen** — read `/v1/favorites`.
5. **Anon auth** — wire JWTs so state is per-user.
6. **Submit + moderation** — the `pending` queue.
7. Polish: haptics, empty states, prefetch tuning, sharing.

Ship 1–3 first; that's a usable, funny, swipeable MVP.

---

## Open questions to decide next

- **Accounts:** anonymous-only forever, or real signup with email/social login?
- **Sharing:** should a liked joke be shareable to iMessage/Instagram? (affects
  whether you render jokes to images).
- **Ranking:** purely `like_count`, or a like-ratio / freshness blend?
- **Hosting:** Fly.io + Neon is a cheap, clean combo for Go + Postgres — good default?
```