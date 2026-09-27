// Editor / CI stand-in so the paywall flow is fully clickable without a store.
// "Purchases" always succeed locally; the server will still say pro=false unless
// you grant a 'promo' entitlement in the DB (see docs/06 §2.1).
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace YoMama.Paywall
{
    public class FakeStoreProvider : IStoreProvider
    {
        public Task InitializeAsync(string appUserId)
        {
            Debug.Log($"[store:fake] initialized for {appUserId}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoreProduct>> GetProductsAsync()
        {
            IReadOnlyList<StoreProduct> products = new List<StoreProduct>
            {
                new StoreProduct { PackageId = "$rc_annual", ProductId = "yomama_pro_yearly", Title = "Yearly", PriceString = "$19.99", IsDefault = true },
                new StoreProduct { PackageId = "$rc_monthly", ProductId = "yomama_pro_monthly", Title = "Monthly", PriceString = "$2.99", IntroOffer = "3-day free trial" },
            };
            return Task.FromResult(products);
        }

        public async Task<PurchaseResult> PurchaseAsync(string packageId)
        {
            await Task.Delay(600); // feel of a store sheet
            Debug.Log($"[store:fake] purchased {packageId}");
            return new PurchaseResult { ProActive = true };
        }

        public Task<PurchaseResult> RestoreAsync() =>
            Task.FromResult(new PurchaseResult { ProActive = false });
    }
}
