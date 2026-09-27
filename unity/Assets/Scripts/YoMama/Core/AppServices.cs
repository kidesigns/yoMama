// Composition root. Lives in the Boot scene, survives scene loads, and owns the
// long-lived services: API client, auth, store, entitlements.
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using YoMama.Paywall;

namespace YoMama.Core
{
    public class AppServices : MonoBehaviour
    {
        [SerializeField] string apiBaseUrl = "http://localhost:8080";
        [SerializeField] string mainScene = "Main";

#if YOMAMA_REVENUECAT
        [SerializeField] Purchases revenueCat;
#endif

        public static AppServices Instance { get; private set; }

        public ApiClient Api { get; private set; }
        public AuthService Auth { get; private set; }
        public IStoreProvider Store { get; private set; }
        public EntitlementService Entitlements { get; private set; }

        /// <summary>Completes once auth + entitlements are ready; await before calling the API.</summary>
        public Task Ready { get; private set; }

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            Api = new ApiClient(apiBaseUrl);
            Auth = new AuthService(Api);
            Entitlements = new EntitlementService(Api);
#if YOMAMA_REVENUECAT
            Store = new RevenueCatStoreProvider(revenueCat);
#else
            Store = new FakeStoreProvider();
#endif
            Ready = Boot();
        }

        async Task Boot()
        {
            try
            {
                await Auth.SignInAsync();
            }
            catch (ApiException e)
            {
                // First launch while offline: nothing cached to fall back on.
                // TODO: show a "can't connect — retry" screen in the Boot scene.
                Debug.LogError($"[boot] sign-in failed: {e.Code} {e.Message}");
                throw;
            }

            try
            {
                await Store.InitializeAsync(Auth.UserId);
            }
            catch (System.Exception e)
            {
                // Store problems must never block playing the free tier.
                Debug.LogWarning("[boot] store init failed: " + e.Message);
            }
            await Entitlements.RefreshAsync();

            if (SceneManager.GetActiveScene().name != mainScene)
                await AwaitOp(SceneManager.LoadSceneAsync(mainScene));
        }

        void OnApplicationPause(bool paused)
        {
            // Subscriptions change while we're backgrounded (renewals, cancels, Family Sharing).
            if (!paused && Auth != null && Auth.IsSignedIn) _ = Entitlements.RefreshAsync();
        }

        static Task AwaitOp(AsyncOperation op)
        {
            var tcs = new TaskCompletionSource<bool>();
            if (op.isDone) tcs.SetResult(true);
            else op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
