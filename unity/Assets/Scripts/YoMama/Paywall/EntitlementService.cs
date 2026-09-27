// "Is this user Pro?" — the SERVER is the source of truth (docs/06 §1).
// After a purchase the client may optimistically unlock UI, but anything the
// server enforces (hosting rooms, the swipe cap) waits for GET /v1/me/entitlements.
using System;
using System.Threading.Tasks;
using UnityEngine;
using YoMama.Core;

namespace YoMama.Paywall
{
    public class EntitlementService
    {
        readonly ApiClient _api;

        public Entitlements Current { get; private set; } = new Entitlements();

        /// <summary>True once the store reported a purchase, before the webhook lands.</summary>
        public bool OptimisticPro { get; private set; }

        public bool IsPro => Current.Pro;
        public bool ShowAsPro => Current.Pro || OptimisticPro;

        public event Action<Entitlements> Changed;

        public EntitlementService(ApiClient api)
        {
            _api = api;
        }

        public async Task RefreshAsync()
        {
            try
            {
                Current = await _api.GetEntitlements();
                if (Current.Pro) OptimisticPro = false;
                Changed?.Invoke(Current);
            }
            catch (ApiException e)
            {
                Debug.LogWarning($"[entitlements] refresh failed: {e.Code} {e.Message}");
            }
        }

        /// <summary>
        /// Called right after a successful purchase/restore. The RevenueCat webhook
        /// usually reaches our API within a couple of seconds; poll until it has.
        /// </summary>
        public async Task<bool> WaitForServerProAsync(float timeoutSeconds = 10f)
        {
            OptimisticPro = true;
            Changed?.Invoke(Current);

            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            var delayMs = 500;
            while (Time.realtimeSinceStartup < deadline)
            {
                await RefreshAsync();
                if (Current.Pro) return true;
                await Task.Delay(delayMs);
                delayMs = Math.Min(delayMs * 2, 2000);
            }
            return false; // keep OptimisticPro; next foreground refresh will settle it
        }

        /// <summary>Local mirror of the free-tier cap, so the deck can show the paywall card early.</summary>
        public bool HasSwipesLeft =>
            Current.Limits.DailySwipes == null || Current.Limits.SwipesToday < Current.Limits.DailySwipes.Value;

        public void NoteSwipeRecorded() => Current.Limits.SwipesToday++;

        public void NoteDailyLimitHit()
        {
            if (Current.Limits.DailySwipes is int cap) Current.Limits.SwipesToday = cap;
            Changed?.Invoke(Current);
        }
    }
}
