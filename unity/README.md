# Yo Mama — Unity client

Starter scripts for the Unity 6 app. Design and rationale: `docs/05-unity-client.md`;
backend contract for subscriptions + Party rooms: `docs/06-subscriptions-and-multiplayer.md`.

## Open it

1. Unity Hub → **Add project from disk** → pick this `unity/` folder, editor **Unity 6 LTS (6000.0.x)**.
   Unity generates `ProjectSettings/` and resolves `Packages/manifest.json`
   (Newtonsoft JSON, uGUI/TextMeshPro, URP, NativeWebSocket via git — git must be on your PATH).
2. Import TMP Essentials when prompted.
3. Player Settings: IL2CPP, portrait only, iOS 15+, Android min API 24.

The project compiles **without** RevenueCat: `AppServices` uses `FakeStoreProvider`
until you add the `YOMAMA_REVENUECAT` scripting define (see `RevenueCatStoreProvider.cs`).

## Scenes to create

| Scene | Contains |
|---|---|
| `Boot` (index 0) | GameObject with `AppServices` (set `apiBaseUrl`). With RevenueCat: a `Purchases` component, dragged into `AppServices.revenueCat`. |
| `Main` | Canvas (Scale With Screen Size, 1080×1920, match 0.5) + EventSystem; `SwipeDeck`; the paywall panel with `PaywallController`; Party panel with `PartyController` + `RoomClient`. |

## Prefabs to create

- **JokeCard**: `Image` (card background, must be a raycast target) + `SwipeCard`, a `TMP_Text`
  body, and two `CanvasGroup` stamps ("LOVE" / "NOPE"). Size ~900×1200, pivot center.
  Used by both the deck and the Party round.
- **PaywallOption**: a `Button` with a child `TMP_Text`.

## Scripts

```
Assets/Scripts/YoMama/
  Core/         AppServices, ApiClient, ApiModels, AuthService
  Swipe/        SwipeCard (drag/tilt/fling), SwipeDeck (stack, prefetch, paywall card)
  Paywall/      IStoreProvider, FakeStoreProvider, RevenueCatStoreProvider, EntitlementService, PaywallController
  Multiplayer/  RoomMessages, RoomClient (WebSocket + reconnect), PartyController
```

## Testing without the backend pieces

- Swipe deck only needs the existing `/v1/auth/anon`, `/v1/jokes/feed`, `/v1/swipes`.
- Pro in dev: insert a promo row —
  `INSERT INTO entitlements (user_id, entitlement, store) VALUES ('<user id>', 'pro', 'promo');`
- Party rooms: two editor instances via **Multiplayer Play Mode** (Window → Multiplayer),
  or one editor + one phone build.
