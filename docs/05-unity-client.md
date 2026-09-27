# Yo Mama — Unity Client (interactions, paywall, multiplayer)

> **This supersedes the React Native client in `01-architecture.md` §2.** The
> Go API and Postgres stay exactly as designed; Unity replaces Expo as the app
> that runs on the phone. New backend pieces for subscriptions and multiplayer
> live in `06-subscriptions-and-multiplayer.md`.
>
> Starter code: `unity/` (C# scripts + package manifest — open the folder in
> Unity 6 and it generates the rest).

---

## 1. New high-level shape

```
┌──────────────────────────────┐   HTTPS/JSON (REST)    ┌──────────────────────────┐
│   Unity 6 app (iOS/Android)  │ ─────────────────────► │         Go API           │
│   - Swipe deck (uGUI + drag) │ ◄───────────────────── │  REST  (existing v1)     │
│   - Paywall (RevenueCat)     │                        │  + /v1/me/entitlements   │
│   - Party rooms (WebSocket)  │ ◄════ WebSocket ═════► │  + /v1/rooms  + WS hub   │
└──────────────┬───────────────┘                        │  + RevenueCat webhook    │
               │ StoreKit / Play Billing                └────────────┬─────────────┘
               ▼                                                     │ SQL
      ┌──────────────────┐     webhook (subscription events)  ┌──────▼──────────┐
      │    RevenueCat    │ ─────────────────────────────────► │   PostgreSQL    │
      └──────────────────┘                                    │ + entitlements  │
                                                              │ + billing_events│
                                                              └─────────────────┘
```

Three things change versus the original plan:

1. **Client = Unity 6 LTS** (C#, uGUI + TextMeshPro).
2. **Paywall = RevenueCat** on top of App Store / Google Play subscriptions,
   with the **Go API as the source of truth** for who is "Pro".
3. **Multiplayer = server-authoritative Party rooms over WebSocket**, hosted in
   the existing Go API.

---

## 2. Should the whole app be Unity? (yes — here's the honest trade-off)

You could embed Unity inside a native/React Native shell ("Unity as a
Library") and use it only for the fun parts. **Don't** — for an app this size
the bridge (two runtimes, two build systems, message passing, two sets of
crashes) costs more than it saves. Go all-in on Unity.

What you gain:
- One engine for the swipe physics, juicy animations, particles, sound, and
  the multiplayer game loop — this is exactly what Unity is good at.
- Room to grow the "party game" side (timers, reveals, confetti, avatars)
  without fighting a UI framework.

What you give up (plan for it):

| Cost | Mitigation |
|---|---|
| Bigger download (~40–60 MB vs ~15 MB) | IL2CPP + managed stripping "High", ASTC textures, no unused packages |
| Slower cold start | Tiny bootstrap scene; load the deck scene async behind the splash |
| Weaker screen-reader / accessibility | Large type, high contrast, big buttons; revisit if it matters |
| No Expo-style over-the-air updates | Keep jokes/config server-driven; Addressables later if needed |
| Native share sheet / haptics need plugins | NativeShare (yasirkula) for sharing; `Handheld.Vibrate` → a haptics plugin later |

---

## 3. Unity project setup

| Setting | Value |
|---|---|
| Unity version | **Unity 6 LTS** (6000.0.x) |
| Template | 2D (URP) — flat UI, no 3D needed |
| Scripting backend | IL2CPP (required for iOS, recommended on Android) |
| Target API | iOS 15+, Android API 24+ (min) / latest (target) |
| UI | **uGUI + TextMeshPro** (Canvas, `Screen Space – Overlay`, Canvas Scaler "Scale With Screen Size", ref 1080×1920, match 0.5) |
| Orientation | Portrait only |

### Packages (`unity/Packages/manifest.json`)

| Package | Why |
|---|---|
| `com.unity.ugui` | Canvas UI, drag events (includes TextMeshPro in Unity 6) |
| `com.unity.nuget.newtonsoft-json` | Real JSON (nullables, dictionaries, snake_case) — `JsonUtility` can't handle the API |
| `com.endel.nativewebsocket` (git) | WebSocket that works on iOS, Android, and WebGL |
| **RevenueCat `purchases-unity`** | Subscriptions. Install from the RevenueCat `.unitypackage` or OpenUPM, then add the scripting define **`YOMAMA_REVENUECAT`** (see §5.4) |

### Scenes

```
Boot        → anon auth, RevenueCat login, fetch entitlements → loads Main
Main        → swipe deck (home), bottom tabs: Deck · Saved · Party
Party       → lobby → rounds → results (additively loaded over Main)
```

Keep one long-lived `AppServices` object (`DontDestroyOnLoad`) holding the API
client, auth, and entitlements. Everything else is per-scene.

### Script layout (`unity/Assets/Scripts/YoMama/`)

```
Core/
  AppServices.cs         # composition root, DontDestroyOnLoad singleton
  ApiClient.cs           # UnityWebRequest + Newtonsoft, bearer token, error shape
  ApiModels.cs           # DTOs mirroring docs/02-api-spec.md + 06
  AuthService.cs         # device id → POST /v1/auth/anon, token in PlayerPrefs
Swipe/
  SwipeCard.cs           # drag / rotate / fling / snap-back for ONE card
  SwipeDeck.cs           # the stack, batching, prefetch, POST /v1/swipes
Paywall/
  IStoreProvider.cs      # interface so the game never talks to a store SDK directly
  RevenueCatStoreProvider.cs  # real implementation (compiled only with YOMAMA_REVENUECAT)
  FakeStoreProvider.cs   # editor / CI stand-in — "buy" always succeeds
  EntitlementService.cs  # "is this user Pro?" — trusts the SERVER, not the device
  PaywallController.cs   # the paywall screen: offerings, buy, restore
Multiplayer/
  RoomMessages.cs        # WS message DTOs (protocol in doc 06 §4)
  RoomClient.cs          # connect / send / dispatch / reconnect
  PartyController.cs     # drives the Party UI from room state
```

---

## 4. Interactions — the swipe deck in Unity

The feel is still the whole product. In Unity you get it by owning the card's
transform directly instead of going through a gesture library.

### 4.1 One card (`SwipeCard.cs`)

Implements `IBeginDragHandler`, `IDragHandler`, `IEndDragHandler`:

| Phase | Behavior |
|---|---|
| Drag | Card follows the finger (anchored position). Rotation = `-dx / width * maxTilt` (≈15°). "LOVE" / "NOPE" stamps fade in with `|dx|`. |
| Release past threshold | `|dx| > 35% of card width` **or** flick velocity `> 1500 px/s` → fling off-screen in that direction, then raise `Swiped(direction)`. |
| Release under threshold | Ease back to center (≈0.18 s, ease-out-back). |
| Buttons | ❤ / ✕ buttons call `SwipeProgrammatically(dir)` — same fling animation, same event. |

Animations are plain coroutines with easing — no tween package required (swap
in DOTween/PrimeTween later if you want).

### 4.2 The deck (`SwipeDeck.cs`)

- Shows the **top 3 cards** stacked (the ones underneath scaled 0.95 / 0.90
  and offset down) — only the top card is interactive.
- On `Swiped`: optimistic — remove card immediately, promote the next, fire
  `POST /v1/swipes` in the background (the endpoint is idempotent, so retry on
  failure is safe).
- **Prefetch:** when ≤ 5 cards remain, fetch the next page via
  `GET /v1/jokes/feed?cursor=…`. Never block the deck on the network.
- **Free-tier limit** (see §5.1): after N swipes/day the next card is a
  "paywall card" instead of a joke. Swiping it away closes it; tapping opens
  the paywall. The server also enforces the cap (doc 06 §2.3).

### 4.3 Juice checklist

Cheap things that make it feel like a game, not a list:
- Haptic tick on crossing the threshold, thunk on a right-swipe fling.
- Card "pop" (scale 1.0 → 1.04 → 1.0) when it becomes the top card.
- Particle hearts on love; a subtle whoosh sound on fling.
- Empty-deck state with a "submit your own" CTA (from doc 04 §6).

---

## 5. Paywall & subscriptions

### 5.1 What's free vs. paid

| | **Free** | **Pro** (subscription) |
|---|---|---|
| Swipe deck | 30 jokes / day | Unlimited |
| Saved jokes | ✔ | ✔ |
| Submit jokes | ✔ | ✔ |
| **Join** a Party room | ✔ (up to 8 players) | ✔ |
| **Host** a Party room | ✘ | ✔ (up to 12 players) |
| Themed packs (later) | ✘ | ✔ |

**Why "guests join free, host pays":** it's the Jackbox model. Every Party a
Pro user hosts is a demo in front of up to 11 free users — that's your growth
loop. Gating *joining* would kill it.

Products (create the same IDs in App Store Connect and Google Play):

| Product ID | Price (suggested) | Notes |
|---|---|---|
| `yomama_pro_monthly` | $2.99 / mo | 3-day free trial |
| `yomama_pro_yearly` | $19.99 / yr | Default-selected on the paywall ("save 44%") |

Both grant the single RevenueCat **entitlement `pro`**. The game code only ever
asks "is `pro` active?", never "which product did they buy?".

### 5.2 Why RevenueCat (and not raw Unity IAP)

Unity IAP can *sell* a subscription, but subscriptions are mostly a
**server** problem: renewals, grace periods, billing retries, refunds,
cross-platform restore, and receipt validation against Apple and Google.
RevenueCat does all of that, has a Unity SDK, and sends a webhook to your Go
API on every state change. It's free until $2.5k/mo in tracked revenue.

If you'd rather not depend on it, the `IStoreProvider` interface lets you swap
in Unity IAP later — but then you must also build server-side validation with
the App Store Server API and Google Play Developer API yourself.

### 5.3 The flow

```
Boot:
  1. POST /v1/auth/anon           → our user id + JWT
  2. Purchases.LogIn(user.id)     → RevenueCat app_user_id == our users.id
  3. GET /v1/me/entitlements      → { pro: true/false, expires_at, limits }

Buy:
  4. Paywall shows offerings from RevenueCat (localized prices!)
  5. PurchasePackage(...)         → StoreKit / Play sheet
  6. RevenueCat validates receipt → webhook → Go API upserts `entitlements`
  7. App re-fetches GET /v1/me/entitlements (poll up to ~10 s for the webhook)
```

**Rule: the device never decides it's Pro on its own for anything the server
enforces** (hosting rooms, the swipe cap). The client may *optimistically*
unlock UI right after a successful purchase using RevenueCat's
`CustomerInfo`, but hosting a room only works once the server agrees.

### 5.4 Keeping the project compiling without the SDK

`RevenueCatStoreProvider.cs` is wrapped in `#if YOMAMA_REVENUECAT`. Until you
install the SDK and add that define (Project Settings → Player → Scripting
Define Symbols), `AppServices` uses `FakeStoreProvider`, so the project
compiles and the paywall is fully clickable in the editor.

### 5.5 Paywall screen — must-haves (App Review will check)

- Price, period, and trial terms clearly shown, from the store's localized
  strings — never hard-coded prices.
- **Restore Purchases** button.
- Links to **Terms of Use** and **Privacy Policy**.
- A visible close (✕) button — no dark patterns.
- Show it from: the free-tier swipe cap card, the "Host a Party" button, and
  Settings → "Go Pro".

---

## 6. Multiplayer — Party mode

### 6.1 The game

**"Roast Room"**: a host creates a room and shares a 5-letter code. Everyone
sees the **same joke at the same time** and swipes LOVE or NOPE within 10 s.
After each round the result is revealed ("7/9 loved it 🔥"), and players
score points for **matching the majority** (a "read the room" game). 10 rounds,
then a podium.

It reuses the swipe interaction you already built — the Party screen is the
same `SwipeCard`, driven by the server instead of the feed.

### 6.2 Why WebSocket-on-Go, not Unity Netcode/Relay

| Option | Verdict |
|---|---|
| **Go API WebSocket hub, server-authoritative** ✅ | State is tiny (a joke id, a timer, votes). The server already knows who's Pro, so it can enforce "only Pro can host" and the player caps. No cheating host. One deploy. |
| Unity Netcode for GameObjects + Relay + Lobby | Built for real-time physics games. Host-authoritative by default (the host's phone is the server → drops the room if they background the app). Paid per CCU. Overkill here. |
| Photon / Fusion | Same as above, plus another vendor bill. |

If you later add a real-time mini-game (e.g. a physics "throw a pie" round),
revisit Netcode for *that* mode only.

### 6.3 Client side

- `POST /v1/rooms` (Pro only) → `{ code }`; `POST /v1/rooms/{code}/join` →
  `{ ws_url }`.
- `RoomClient` opens `wss://api.yomama.app/v1/rooms/{code}/ws` with the JWT,
  dispatches server messages to C# events, and **reconnects** with backoff —
  mobile sockets drop every time the app backgrounds.
- On reconnect the server sends a full `room_state` snapshot, so the client
  never has to reconcile deltas.
- `NativeWebSocket` needs `DispatchMessageQueue()` called from `Update()` on
  non-WebGL platforms — `RoomClient` does this.

Protocol, room lifecycle, and limits: `06-subscriptions-and-multiplayer.md` §4.

---

## 7. Build order (Unity edition)

1. **Boot + ApiClient + anon auth** against the existing `/v1` endpoints.
2. **Swipe deck** — `SwipeCard` + `SwipeDeck` on `/v1/jokes/feed` and
   `/v1/swipes`. Tune the feel on a real phone. *(This is the MVP.)*
3. **Saved list** (`/v1/favorites`).
4. **Paywall with `FakeStoreProvider`** — design + flow, fully in the editor.
5. **Backend: entitlements + webhook** (doc 06 §2–3), then install RevenueCat,
   flip on `YOMAMA_REVENUECAT`, test with sandbox / license-tester accounts.
6. **Swipe cap** enforced server-side, paywall card in the deck.
7. **Party mode**: Go WS hub first (testable with a WS CLI), then
   `RoomClient` + `PartyController`.
8. Polish: juice (§4.3), share sheet, analytics.

## Open questions

- **Account upgrade:** anonymous users who subscribe and then lose their phone
  need Restore to work — RevenueCat handles it via the store account, but do
  you also want email/Sign in with Apple so Saved jokes follow them?
- **Pricing:** $2.99/mo & $19.99/yr are placeholders — test before launch.
- **Party size:** 12 is a guess; the WS hub handles far more, the UI may not.
