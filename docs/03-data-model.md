# Yo Mama — Data Model, Migrations & Seed

> The database side of the app. Ships the migration files (up/down), the `sqlc`
> query definitions the Go API compiles against, and a seed set of clean jokes
> so the feed isn't empty on day one.
>
> Engine: **PostgreSQL 15+**. Migration tool: **golang-migrate**. Query codegen:
> **sqlc** (typed Go from raw SQL).

---

## 1. Entity overview

```
users ──1───many──► swipes ◄──many───1── jokes
  │                                        ▲
  │                                        │ submitted_by (nullable)
  └──1───many──► favorites ──many───1──────┘
```

- **users** — one row per person; may be anonymous (email null).
- **jokes** — the content; has a moderation `status` and denormalized counts.
- **swipes** — the event log + source of truth for "already seen." One per
  `(user, joke)`.
- **favorites** — a user's saved (right-swiped) jokes; makes un-saving trivial.

Design notes:
- `swipes.UNIQUE(user_id, joke_id)` makes swipe writes idempotent.
- `jokes.like_count` / `pass_count` are **denormalized** so the feed can rank
  without a join+aggregate on every request. They're maintained in the same
  transaction as the swipe.
- `favorites` is technically derivable from `swipes WHERE direction='right'`,
  but a dedicated table makes "un-save" and the favorites list a single-table op.

---

## 2. Migrations (golang-migrate)

Files live in `api/db/migrations/`. golang-migrate expects paired
`NNNN_name.up.sql` / `NNNN_name.down.sql`.

### `0001_init.up.sql`

```sql
-- Extensions ---------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS "pgcrypto";   -- gen_random_uuid()

-- Users --------------------------------------------------------------------
CREATE TABLE users (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email       TEXT UNIQUE,                    -- nullable: anonymous users
    device_id   TEXT UNIQUE,                    -- links an anon user to a device
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Jokes --------------------------------------------------------------------
CREATE TABLE jokes (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    body          TEXT NOT NULL,
    status        TEXT NOT NULL DEFAULT 'approved'
                  CHECK (status IN ('pending','approved','rejected')),
    submitted_by  UUID REFERENCES users(id) ON DELETE SET NULL,   -- null = seed
    like_count    INT  NOT NULL DEFAULT 0,
    pass_count    INT  NOT NULL DEFAULT 0,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT body_not_blank CHECK (length(btrim(body)) > 0),
    CONSTRAINT body_max_len   CHECK (length(body) <= 280)
);

-- Swipes (event log + "seen" set) -----------------------------------------
CREATE TABLE swipes (
    id          BIGSERIAL PRIMARY KEY,
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    joke_id     UUID NOT NULL REFERENCES jokes(id) ON DELETE CASCADE,
    direction   TEXT NOT NULL CHECK (direction IN ('left','right')),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (user_id, joke_id)
);

-- Favorites (saved jokes) --------------------------------------------------
CREATE TABLE favorites (
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    joke_id     UUID NOT NULL REFERENCES jokes(id) ON DELETE CASCADE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, joke_id)
);

-- Indexes ------------------------------------------------------------------
CREATE INDEX idx_swipes_user       ON swipes (user_id);
CREATE INDEX idx_swipes_joke       ON swipes (joke_id);
CREATE INDEX idx_jokes_status      ON jokes (status);
CREATE INDEX idx_jokes_ranking     ON jokes (like_count DESC, created_at DESC)
                                   WHERE status = 'approved';
CREATE INDEX idx_favorites_user    ON favorites (user_id, created_at DESC);
```

### `0001_init.down.sql`

```sql
DROP TABLE IF EXISTS favorites;
DROP TABLE IF EXISTS swipes;
DROP TABLE IF EXISTS jokes;
DROP TABLE IF EXISTS users;
-- pgcrypto left installed on purpose; harmless if reused.
```

### `0002_seed_jokes.up.sql`

Loads the starter jokes (full list in §4). `submitted_by` is null and status is
`approved` so they show immediately.

```sql
INSERT INTO jokes (body, status) VALUES
  ('Yo mama so slow, she took a screenshot of the loading spinner.', 'approved'),
  ('Yo mama so old, her birth certificate is in Roman numerals.', 'approved');
  -- ...see §4 for the full seed set
```

### `0002_seed_jokes.down.sql`

```sql
-- Remove only the seed jokes (those with no submitter).
DELETE FROM jokes WHERE submitted_by IS NULL;
```

> **Migration hygiene:** never edit a migration that has run in production —
> add a new numbered pair instead. Keep seed data in its own migration so you
> can roll it back independently of the schema.

---

## 3. sqlc queries

`sqlc` reads these `.sql` files and generates typed Go (`store.Queries`) with a
method per query. Config below, then the queries.

### `api/sqlc.yaml`

```yaml
version: "2"
sql:
  - engine: postgresql
    schema: "db/migrations"
    queries: "db/queries"
    gen:
      go:
        package: "store"
        out: "internal/store"
        sql_package: "pgx/v5"
        emit_json_tags: true
        emit_pointers_for_null_types: true
```

### `api/db/queries/users.sql`

```sql
-- name: UpsertAnonUser :one
-- Create a user for a device, or return the existing one. Idempotent.
INSERT INTO users (device_id)
VALUES ($1)
ON CONFLICT (device_id) DO UPDATE SET device_id = EXCLUDED.device_id
RETURNING id, email, device_id, created_at;

-- name: GetUserByID :one
SELECT id, email, device_id, created_at
FROM users
WHERE id = $1;

-- name: AttachEmail :one
-- Upgrade an anonymous user to an email account (keeps their history).
UPDATE users SET email = $2 WHERE id = $1
RETURNING id, email, device_id, created_at;
```

### `api/db/queries/jokes.sql`

```sql
-- name: GetFeed :many
-- Approved jokes this user hasn't swiped yet, best first.
-- $1 = user_id, $2 = limit
SELECT j.id, j.body, j.like_count, j.created_at
FROM jokes j
WHERE j.status = 'approved'
  AND NOT EXISTS (
      SELECT 1 FROM swipes s
      WHERE s.user_id = $1 AND s.joke_id = j.id
  )
ORDER BY j.like_count DESC, j.created_at DESC
LIMIT $2;

-- name: SubmitJoke :one
-- User-submitted jokes land in the moderation queue.
INSERT INTO jokes (body, status, submitted_by)
VALUES ($1, 'pending', $2)
RETURNING id, status, created_at;

-- name: GetJokeByID :one
SELECT id, body, status, like_count, pass_count, submitted_by, created_at
FROM jokes
WHERE id = $1;

-- name: ApproveJoke :exec
UPDATE jokes SET status = 'approved' WHERE id = $1;

-- name: RejectJoke :exec
UPDATE jokes SET status = 'rejected' WHERE id = $1;

-- name: CountPendingJokes :one
SELECT count(*) FROM jokes WHERE status = 'pending';
```

### `api/db/queries/swipes.sql`

```sql
-- name: RecordSwipe :one
-- Idempotent insert. xmax = 0 on a fresh insert; nonzero if the row already
-- existed (ON CONFLICT hit) — the API uses `inserted` to skip double-counting.
INSERT INTO swipes (user_id, joke_id, direction)
VALUES ($1, $2, $3)
ON CONFLICT (user_id, joke_id) DO NOTHING
RETURNING id, (xmax = 0) AS inserted;

-- name: BumpCounters :exec
-- Only call when a swipe was newly inserted.
UPDATE jokes
SET like_count = like_count + (sqlc.arg(direction) = 'right')::int,
    pass_count = pass_count + (sqlc.arg(direction) = 'left')::int
WHERE id = sqlc.arg(joke_id);

-- name: HasSwiped :one
SELECT direction FROM swipes WHERE user_id = $1 AND joke_id = $2;
```

> **Note on `RETURNING (xmax = 0)`:** with `ON CONFLICT DO NOTHING`, a row that
> already exists returns *nothing*. To distinguish "inserted" from "already
> there" reliably, either (a) use `ON CONFLICT DO UPDATE SET direction =
> swipes.direction` so a row is always returned with the `xmax` trick, or (b)
> check the affected-row count in Go. The service layer wraps `RecordSwipe` +
> `BumpCounters` + favorite upsert in **one transaction** (see §5).

### `api/db/queries/favorites.sql`

```sql
-- name: AddFavorite :exec
INSERT INTO favorites (user_id, joke_id)
VALUES ($1, $2)
ON CONFLICT (user_id, joke_id) DO NOTHING;

-- name: RemoveFavorite :exec
DELETE FROM favorites WHERE user_id = $1 AND joke_id = $2;

-- name: ListFavorites :many
-- $1 = user_id, $2 = limit, $3 = created_before cursor (or now())
SELECT j.id, j.body, j.like_count, f.created_at AS favorited_at
FROM favorites f
JOIN jokes j ON j.id = f.joke_id
WHERE f.user_id = $1
  AND f.created_at < $3
ORDER BY f.created_at DESC
LIMIT $2;

-- name: IsFavorited :one
SELECT EXISTS (
  SELECT 1 FROM favorites WHERE user_id = $1 AND joke_id = $2
) AS favorited;
```

---

## 4. Seed jokes

Curated, clean (PG-13, punching at nobody real) so the app has content and
passes app-store review. Use these in `0002_seed_jokes.up.sql`.

```sql
INSERT INTO jokes (body, status) VALUES
  ('Yo mama so slow, she took a screenshot of the loading spinner.', 'approved'),
  ('Yo mama so old, her birth certificate is in Roman numerals.', 'approved'),
  ('Yo mama so forgetful, she needs a sticky note to remember her sticky notes.', 'approved'),
  ('Yo mama so bad at hide and seek, the GPS always finds her first.', 'approved'),
  ('Yo mama so into recycling, she reuses the same joke twice.', 'approved'),
  ('Yo mama so organized, her to-do list has a to-do list.', 'approved'),
  ('Yo mama so early, she showed up to the party before it was planned.', 'approved'),
  ('Yo mama so competitive, she high-fives herself just to win.', 'approved'),
  ('Yo mama so extra, she puts a bookmark in a one-page menu.', 'approved'),
  ('Yo mama so chill, she pauses live TV to take a nap.', 'approved'),
  ('Yo mama so thrifty, she waters the plastic plants to save on real ones.', 'approved'),
  ('Yo mama so punctual, she arrives yesterday.', 'approved'),
  ('Yo mama so tech-savvy, she alphabetized her emoji.', 'approved'),
  ('Yo mama so dramatic, she narrates her own grocery run.', 'approved'),
  ('Yo mama so patient, she waited for the microwave to hit 00:00 out of respect.', 'approved'),
  ('Yo mama so prepared, she brings a spare umbrella for the rain that never comes.', 'approved'),
  ('Yo mama so loyal, she still roots for the loading bar at 99%.', 'approved'),
  ('Yo mama so wholesome, her roast came with a side of compliments.', 'approved'),
  ('Yo mama so quiet, the library asked HER to keep it down.', 'approved'),
  ('Yo mama so careful, she reads the terms and conditions. All of them.', 'approved');
```

> Keep the seed list **clean and non-targeted** — anything edgier goes through
> the `pending` moderation queue (see `01-architecture.md` §5), not the seed.

---

## 5. The one write path that matters: recording a swipe

The handler for `POST /v1/swipes` runs a single transaction so counters and
favorites never drift from the swipe log. Pseudocode over the sqlc methods:

```go
func (s *Service) Swipe(ctx context.Context, userID, jokeID uuid.UUID, dir string) (bool, error) {
    tx, err := s.pool.Begin(ctx)
    if err != nil { return false, err }
    defer tx.Rollback(ctx)
    q := s.q.WithTx(tx)

    row, err := q.RecordSwipe(ctx, RecordSwipeParams{UserID: userID, JokeID: jokeID, Direction: dir})
    if err != nil { return false, err }

    if row.Inserted {                       // first time this user swiped this joke
        if err := q.BumpCounters(ctx, BumpCountersParams{Direction: dir, JokeID: jokeID}); err != nil {
            return false, err
        }
    }

    favorited := dir == "right"
    if favorited {
        if err := q.AddFavorite(ctx, AddFavoriteParams{UserID: userID, JokeID: jokeID}); err != nil {
            return false, err
        }
    }
    return favorited, tx.Commit(ctx)
}
```

Key properties:
- **Idempotent** — a repeat swipe hits `ON CONFLICT DO NOTHING`, `Inserted` is
  false, counters don't move, and the response still reports `favorited`.
- **Atomic** — counters and favorites commit with the swipe or not at all.
- **Cheap** — no aggregates; the feed reads the denormalized `like_count`.

---

## 6. Local dev

`docker-compose.yml` (Postgres + the API):

```yaml
services:
  db:
    image: postgres:16
    environment:
      POSTGRES_USER: yomama
      POSTGRES_PASSWORD: yomama
      POSTGRES_DB: yomama
    ports: ["5432:5432"]
    volumes: ["dbdata:/var/lib/postgresql/data"]
volumes:
  dbdata:
```

Bring it up and migrate:

```bash
docker compose up -d db
migrate -path api/db/migrations -database "$DATABASE_URL" up
sqlc generate -f api/sqlc.yaml
```

Where `DATABASE_URL=postgres://yomama:yomama@localhost:5432/yomama?sslmode=disable`.

---

## 7. Future-proofing (later, not now)

- **Categories/tags:** add a `jokes.tags TEXT[]` or a `tags` + `joke_tags`
  join table when you want themed decks.
- **Reporting:** a `reports (user_id, joke_id, reason)` table for user flags,
  feeding the moderation queue.
- **Ranking upgrade:** move from raw `like_count` to a
  `like_count / (like_count + pass_count)` ratio with a freshness decay; consider
  a materialized view refreshed on a schedule if it gets heavy.
- **Analytics:** the `swipes` table already is your event log — you can compute
  daily active swipers, per-joke like-ratio, and funnel metrics straight from it.
```