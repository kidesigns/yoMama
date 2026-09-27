// The game never talks to a store SDK directly — only through this interface.
// Implementations: RevenueCatStoreProvider (real), FakeStoreProvider (editor/CI).
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YoMama.Paywall
{
    public class StoreProduct
    {
        public string PackageId;     // RevenueCat package id, e.g. "$rc_annual"
        public string ProductId;     // store product id, e.g. "yomama_pro_yearly"
        public string Title;         // "Yearly"
        public string PriceString;   // localized by the store, e.g. "$19.99" — never hard-code prices
        public string IntroOffer;    // e.g. "3-day free trial", or null
        public bool IsDefault;       // pre-selected on the paywall
    }

    public class PurchaseResult
    {
        public bool Cancelled;
        public bool ProActive;       // what the store/RevenueCat thinks — a hint, the server decides
        public string Error;

        public bool Succeeded => !Cancelled && Error == null;
    }

    public interface IStoreProvider
    {
        /// <summary>Identify the buyer as our users.id so webhooks map back to the right account.</summary>
        Task InitializeAsync(string appUserId);

        Task<IReadOnlyList<StoreProduct>> GetProductsAsync();

        Task<PurchaseResult> PurchaseAsync(string packageId);

        Task<PurchaseResult> RestoreAsync();
    }
}
