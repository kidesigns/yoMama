// Real store provider backed by the RevenueCat Unity SDK (purchases-unity).
//
// Compiled only when the scripting define YOMAMA_REVENUECAT is set, so the
// project builds before the SDK is installed. To enable:
//   1. Import purchases-unity (RevenueCat .unitypackage or OpenUPM).
//   2. Add a GameObject with the `Purchases` component to the Boot scene;
//      set the Apple/Google public API keys, leave App User ID empty
//      (we log in with our own users.id below).
//   3. Player Settings → Scripting Define Symbols → add YOMAMA_REVENUECAT.
//   4. Drag that `Purchases` component onto AppServices.revenueCat.
//
// Callback signatures vary slightly between SDK majors; the lambdas below use
// inferred parameter types so they only depend on the argument count.
#if YOMAMA_REVENUECAT
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace YoMama.Paywall
{
    public class RevenueCatStoreProvider : IStoreProvider
    {
        const string ProEntitlement = "pro";

        readonly Purchases _purchases;
        readonly Dictionary<string, Purchases.Package> _packages = new Dictionary<string, Purchases.Package>();

        public RevenueCatStoreProvider(Purchases purchases)
        {
            _purchases = purchases;
        }

        public Task InitializeAsync(string appUserId)
        {
            var tcs = new TaskCompletionSource<bool>();
            _purchases.LogIn(appUserId, (info, created, error) =>
            {
                if (error != null) tcs.TrySetException(new System.Exception(error.Message));
                else tcs.TrySetResult(true);
            });
            return tcs.Task;
        }

        public Task<IReadOnlyList<StoreProduct>> GetProductsAsync()
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<StoreProduct>>();
            _purchases.GetOfferings((offerings, error) =>
            {
                if (error != null) { tcs.TrySetException(new System.Exception(error.Message)); return; }

                _packages.Clear();
                var current = offerings.Current;
                if (current == null) { tcs.TrySetResult(new List<StoreProduct>()); return; }

                var list = new List<StoreProduct>();
                foreach (var p in current.AvailablePackages)
                {
                    _packages[p.Identifier] = p;
                    var intro = p.StoreProduct.IntroductoryPrice;
                    list.Add(new StoreProduct
                    {
                        PackageId = p.Identifier,
                        ProductId = p.StoreProduct.Identifier,
                        Title = p.StoreProduct.Title,
                        PriceString = p.StoreProduct.PriceString,
                        IntroOffer = intro != null ? $"{intro.PeriodNumberOfUnits} {intro.PeriodUnit.ToLowerInvariant()} free trial" : null,
                        IsDefault = p.Identifier == "$rc_annual",
                    });
                }
                if (list.Count > 0 && !list.Any(x => x.IsDefault)) list[0].IsDefault = true;
                tcs.TrySetResult(list);
            });
            return tcs.Task;
        }

        public Task<PurchaseResult> PurchaseAsync(string packageId)
        {
            var tcs = new TaskCompletionSource<PurchaseResult>();
            if (!_packages.TryGetValue(packageId, out var package))
            {
                tcs.SetResult(new PurchaseResult { Error = "Unknown package " + packageId });
                return tcs.Task;
            }

            _purchases.PurchasePackage(package, (transaction, info, userCancelled, error) =>
            {
                if (userCancelled) tcs.TrySetResult(new PurchaseResult { Cancelled = true });
                else if (error != null) tcs.TrySetResult(new PurchaseResult { Error = error.Message });
                else tcs.TrySetResult(new PurchaseResult { ProActive = IsPro(info) });
            });
            return tcs.Task;
        }

        public Task<PurchaseResult> RestoreAsync()
        {
            var tcs = new TaskCompletionSource<PurchaseResult>();
            _purchases.RestorePurchases((info, error) =>
            {
                if (error != null) tcs.TrySetResult(new PurchaseResult { Error = error.Message });
                else tcs.TrySetResult(new PurchaseResult { ProActive = IsPro(info) });
            });
            return tcs.Task;
        }

        static bool IsPro(Purchases.CustomerInfo info) =>
            info != null && info.Entitlements.Active.ContainsKey(ProEntitlement);
    }
}
#endif
