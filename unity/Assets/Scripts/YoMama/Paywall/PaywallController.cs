// The paywall screen. App Review checklist (docs/05 §5.5): localized prices from
// the store, Restore button, Terms + Privacy links, and a visible close button.
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YoMama.Core;

namespace YoMama.Paywall
{
    public class PaywallController : MonoBehaviour
    {
        [Header("Layout")]
        [SerializeField] GameObject root;
        [SerializeField] Transform optionsContainer;
        [SerializeField] Button optionPrefab;          // child TMP_Text used as the label
        [SerializeField] TMP_Text headline;
        [SerializeField] TMP_Text statusText;

        [Header("Actions")]
        [SerializeField] Button buyButton;
        [SerializeField] Button restoreButton;
        [SerializeField] Button closeButton;
        [SerializeField] Button termsButton;
        [SerializeField] Button privacyButton;

        [Header("Links")]
        [SerializeField] string termsUrl = "https://yomama.app/terms";
        [SerializeField] string privacyUrl = "https://yomama.app/privacy";

        readonly List<(Button button, StoreProduct product)> _options = new List<(Button, StoreProduct)>();
        StoreProduct _selected;
        bool _busy;

        public static PaywallController Instance { get; private set; }

        void Awake()
        {
            Instance = this;
            root.SetActive(false);
            buyButton.onClick.AddListener(OnBuy);
            restoreButton.onClick.AddListener(OnRestore);
            closeButton.onClick.AddListener(Hide);
            termsButton.onClick.AddListener(() => Application.OpenURL(termsUrl));
            privacyButton.onClick.AddListener(() => Application.OpenURL(privacyUrl));
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <param name="reason">Shown as the headline: why the user hit the wall.</param>
        public async void Show(string reason = "Go Pro")
        {
            headline.text = reason;
            statusText.text = "";
            root.SetActive(true);
            SetBusy(true);

            foreach (var (b, _) in _options) Destroy(b.gameObject);
            _options.Clear();

            try
            {
                var products = await AppServices.Instance.Store.GetProductsAsync();
                foreach (var p in products)
                {
                    var b = Instantiate(optionPrefab, optionsContainer);
                    var label = p.IntroOffer != null ? $"{p.Title} · {p.PriceString}\n<size=70%>{p.IntroOffer}</size>"
                                                     : $"{p.Title} · {p.PriceString}";
                    b.GetComponentInChildren<TMP_Text>().text = label;
                    var captured = p;
                    b.onClick.AddListener(() => Select(captured));
                    _options.Add((b, p));
                    if (p.IsDefault) Select(p);
                }
                if (_options.Count == 0) statusText.text = "Subscriptions aren't available right now.";
            }
            catch (System.Exception e)
            {
                statusText.text = "Couldn't reach the store. Try again later.";
                Debug.LogWarning("[paywall] " + e.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        public void Hide() => root.SetActive(false);

        void Select(StoreProduct p)
        {
            _selected = p;
            foreach (var (b, product) in _options)
                b.interactable = product != p; // selected option renders as "pressed"
        }

        async void OnBuy()
        {
            if (_selected == null || _busy) return;
            SetBusy(true);
            statusText.text = "";
            try
            {
                var result = await AppServices.Instance.Store.PurchaseAsync(_selected.PackageId);
                await Settle(result, "Welcome to Pro!");
            }
            finally
            {
                SetBusy(false);
            }
        }

        async void OnRestore()
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                var result = await AppServices.Instance.Store.RestoreAsync();
                if (result.Succeeded && !result.ProActive) { statusText.text = "No active subscription found."; return; }
                await Settle(result, "Purchases restored.");
            }
            finally
            {
                SetBusy(false);
            }
        }

        async System.Threading.Tasks.Task Settle(PurchaseResult result, string successMessage)
        {
            if (result.Cancelled) return;
            if (result.Error != null) { statusText.text = result.Error; return; }

            statusText.text = "Confirming…";
            var confirmed = await AppServices.Instance.Entitlements.WaitForServerProAsync();
            statusText.text = confirmed ? successMessage
                                        : "Purchase received — Pro will unlock in a moment.";
            if (confirmed) Hide();
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            buyButton.interactable = !busy;
            restoreButton.interactable = !busy;
        }
    }
}
