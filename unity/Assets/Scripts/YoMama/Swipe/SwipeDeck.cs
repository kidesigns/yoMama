// The card stack: batching, prefetch, optimistic swipes, and the free-tier
// paywall card. See docs/05 §4.2 and docs/06 §2.3.
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using YoMama.Core;
using YoMama.Paywall;

namespace YoMama.Swipe
{
    public class SwipeDeck : MonoBehaviour
    {
        [SerializeField] SwipeCard cardPrefab;
        [SerializeField] RectTransform stackRoot;
        [SerializeField] Button loveButton;
        [SerializeField] Button nopeButton;

        [Header("States")]
        [SerializeField] GameObject loadingState;
        [SerializeField] GameObject emptyState;        // "You've seen 'em all — submit your own?"
        [SerializeField] GameObject limitState;        // "Out of free swipes — come back tomorrow or Go Pro"
        [SerializeField] GameObject errorState;

        [Header("Tuning")]
        [SerializeField] int visibleCards = 3;
        [SerializeField] int pageSize = 20;
        [SerializeField] int prefetchWhenRemaining = 5;
        [SerializeField] float stackOffsetY = -30f;
        [SerializeField] float stackScaleStep = 0.05f;

        const string PaywallCopy = "You've used today's free swipes.\n\nSwipe right to go unlimited with Pro.";
        static readonly object PaywallMarker = new object();

        readonly List<Joke> _queue = new List<Joke>();         // not yet on screen
        readonly List<SwipeCard> _cards = new List<SwipeCard>(); // [0] = top
        readonly HashSet<string> _seen = new HashSet<string>();  // feed pages can overlap (docs/02 §5)

        string _cursor;
        bool _endOfFeed;
        bool _fetching;
        bool _loadedOnce;

        AppServices S => AppServices.Instance;
        bool PaywallCardShowing => _cards.Count > 0 && _cards[0].Payload == PaywallMarker;

        async void Start()
        {
            loveButton.onClick.AddListener(() => Top()?.SwipeProgrammatically(SwipeDirection.Right));
            nopeButton.onClick.AddListener(() => Top()?.SwipeProgrammatically(SwipeDirection.Left));

            await S.Ready;
            S.Entitlements.Changed += OnEntitlementsChanged;
            await FetchMore();
        }

        void OnDestroy()
        {
            if (S != null && S.Entitlements != null) S.Entitlements.Changed -= OnEntitlementsChanged;
        }

        SwipeCard Top() => _cards.Count > 0 ? _cards[0] : null;

        // ---- Feed -----------------------------------------------------------

        async Task FetchMore()
        {
            if (_fetching || _endOfFeed) return;
            _fetching = true;
            errorState.SetActive(false);
            try
            {
                var page = await S.Api.GetFeed(_cursor, pageSize);
                foreach (var j in page.Jokes)
                    if (_seen.Add(j.Id)) _queue.Add(j);
                _cursor = page.NextCursor;
                _endOfFeed = string.IsNullOrEmpty(_cursor);
                _loadedOnce = true;
            }
            catch (ApiException e)
            {
                Debug.LogWarning($"[deck] feed failed: {e.Code} {e.Message}");
                if (_cards.Count == 0) errorState.SetActive(true);
            }
            finally
            {
                _fetching = false;
            }
            Fill();
        }

        /// <summary>Hook this to the error state's "Retry" button.</summary>
        public void Retry() => _ = FetchMore();

        void Fill()
        {
            if (!PaywallCardShowing)
            {
                if (!S.Entitlements.HasSwipesLeft)
                {
                    ShowPaywallCard();
                    return;
                }
                while (_cards.Count < visibleCards && _queue.Count > 0)
                {
                    var joke = _queue[0];
                    _queue.RemoveAt(0);
                    AddCard(joke, joke.Body);
                }
            }

            Layout();
            UpdateStates();

            if (_queue.Count + _cards.Count <= prefetchWhenRemaining) _ = FetchMore();
        }

        SwipeCard AddCard(object payload, string text)
        {
            var card = Instantiate(cardPrefab, stackRoot);
            card.Payload = payload;
            card.SetText(text);
            card.Swiped += OnSwiped;
            card.Tapped += OnTapped;
            _cards.Add(card);
            return card;
        }

        void Layout()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                var card = _cards[i];
                card.transform.SetSiblingIndex(_cards.Count - 1 - i); // top card renders last
                card.transform.localScale = Vector3.one * (1f - stackScaleStep * Mathf.Min(i, visibleCards - 1));
                card.SetHome(new Vector2(0f, stackOffsetY * Mathf.Min(i, visibleCards - 1)));
                var becomesTop = i == 0 && !card.Interactable;
                card.Interactable = i == 0;
                if (becomesTop) card.Pop();
            }
        }

        void UpdateStates()
        {
            var noCards = _cards.Count == 0;
            loadingState.SetActive(noCards && !_loadedOnce);
            limitState.SetActive(noCards && _loadedOnce && !S.Entitlements.HasSwipesLeft);
            emptyState.SetActive(noCards && _loadedOnce && S.Entitlements.HasSwipesLeft && _endOfFeed && _queue.Count == 0);
            loveButton.interactable = nopeButton.interactable = !noCards;
        }

        // ---- Swipes ---------------------------------------------------------

        void OnSwiped(SwipeCard card, SwipeDirection dir)
        {
            _cards.Remove(card);
            Destroy(card.gameObject);

            if (card.Payload == PaywallMarker)
            {
                if (dir == SwipeDirection.Right) OpenPaywall();
            }
            else if (card.Payload is Joke joke)
            {
                _ = RecordSwipe(joke, dir); // optimistic: the deck never waits on the network
            }
            Fill();
        }

        void OnTapped(SwipeCard card)
        {
            if (card.Payload == PaywallMarker) OpenPaywall();
        }

        async Task RecordSwipe(Joke joke, SwipeDirection dir)
        {
            // POST /v1/swipes is idempotent, so retrying transient failures is safe.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await S.Api.Swipe(joke.Id, dir);
                    S.Entitlements.NoteSwipeRecorded();
                    return;
                }
                catch (ApiException e) when (e.IsDailyLimit)
                {
                    // Server says we're capped: put the joke back so it isn't lost, show the wall.
                    S.Entitlements.NoteDailyLimitHit();
                    _queue.Insert(0, joke);
                    ShowPaywallCard();
                    return;
                }
                catch (ApiException e) when (e.Status == 0 || e.Status >= 500)
                {
                    await Task.Delay(1000 * (attempt + 1));
                }
                catch (ApiException e)
                {
                    Debug.LogWarning($"[deck] swipe rejected: {e.Code} {e.Message}");
                    return;
                }
            }
        }

        // ---- Paywall --------------------------------------------------------

        void ShowPaywallCard()
        {
            if (PaywallCardShowing) return;

            // Return on-screen jokes to the front of the queue, in order.
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                if (_cards[i].Payload is Joke j) _queue.Insert(0, j);
                Destroy(_cards[i].gameObject);
            }
            _cards.Clear();

            AddCard(PaywallMarker, PaywallCopy);
            Layout();
            UpdateStates();
        }

        void OpenPaywall()
        {
            if (PaywallController.Instance != null)
                PaywallController.Instance.Show("Unlimited swipes with Pro");
        }

        void OnEntitlementsChanged(Entitlements e)
        {
            // Resume only once the SERVER lifts the cap; an optimistic purchase alone
            // would just earn more 402s until the webhook lands.
            if (!S.Entitlements.HasSwipesLeft) return;

            // Upgraded (or a new day): drop the paywall card and resume jokes.
            if (PaywallCardShowing)
            {
                Destroy(_cards[0].gameObject);
                _cards.RemoveAt(0);
            }
            Fill();
        }
    }
}
