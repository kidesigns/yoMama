// One draggable card: follow finger, tilt, LOVE/NOPE stamps, fling or snap back.
// Only the top card of the deck is Interactable. See docs/05 §4.1.
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using YoMama.Core;

namespace YoMama.Swipe
{
    [RequireComponent(typeof(RectTransform))]
    public class SwipeCard : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        [SerializeField] TMP_Text body;
        [SerializeField] CanvasGroup loveStamp;
        [SerializeField] CanvasGroup nopeStamp;

        [Header("Feel")]
        [SerializeField] float maxTilt = 15f;              // degrees at one card-width of drag
        [SerializeField, Range(0.1f, 0.9f)] float throwThreshold = 0.35f; // fraction of card width
        [SerializeField] float flickVelocity = 1500f;      // px/s that throws regardless of distance
        [SerializeField] float flingDuration = 0.25f;
        [SerializeField] float snapDuration = 0.18f;

        /// <summary>Raised after the fling animation finishes.</summary>
        public event Action<SwipeCard, SwipeDirection> Swiped;

        /// <summary>Raised when the drag crosses (true) or un-crosses (false) the throw threshold — hook haptics/sound here.</summary>
        public event Action<bool> ThresholdChanged;

        public event Action<SwipeCard> Tapped;

        /// <summary>What this card represents (a Joke, or a marker for the paywall card).</summary>
        public object Payload { get; set; }

        public bool Interactable { get; set; }

        RectTransform _rt;
        Canvas _canvas;
        Vector2 _home;
        Vector2 _velocity;
        bool _dragging;
        bool _flying;
        bool _pastThreshold;

        float Width => _rt.rect.width;

        void Awake()
        {
            _rt = (RectTransform)transform;
            _canvas = GetComponentInParent<Canvas>();
            SetStamps(0f);
        }

        public void SetText(string text) => body.text = text;

        /// <summary>Where the card rests in the stack; the deck sets this when it lays out cards.</summary>
        public void SetHome(Vector2 anchoredPos)
        {
            _home = anchoredPos;
            if (!_dragging && !_flying) _rt.anchoredPosition = anchoredPos;
        }

        public void SwipeProgrammatically(SwipeDirection dir)
        {
            if (!Interactable || _flying) return;
            StopAllCoroutines();
            StartCoroutine(Fling(dir, Vector2.zero));
        }

        // ---- Drag -----------------------------------------------------------

        public void OnBeginDrag(PointerEventData e)
        {
            if (!Interactable || _flying) return;
            StopAllCoroutines();
            _dragging = true;
            _velocity = Vector2.zero;
        }

        public void OnDrag(PointerEventData e)
        {
            if (!_dragging) return;
            var delta = e.delta / (_canvas ? _canvas.scaleFactor : 1f);
            _rt.anchoredPosition += delta;
            if (Time.unscaledDeltaTime > 0f)
                _velocity = Vector2.Lerp(_velocity, delta / Time.unscaledDeltaTime, 0.5f);
            ApplyDragVisuals();
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (!_dragging) return;
            _dragging = false;

            var dx = _rt.anchoredPosition.x - _home.x;
            var farEnough = Mathf.Abs(dx) > Width * throwThreshold;
            var flicked = Mathf.Abs(_velocity.x) > flickVelocity && Mathf.Sign(_velocity.x) == Mathf.Sign(dx);

            if (farEnough || flicked)
                StartCoroutine(Fling(dx > 0 ? SwipeDirection.Right : SwipeDirection.Left, _velocity));
            else
                StartCoroutine(SnapBack());
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (Interactable && !e.dragging) Tapped?.Invoke(this);
        }

        // ---- Visuals & animation -------------------------------------------

        void ApplyDragVisuals()
        {
            var dx = _rt.anchoredPosition.x - _home.x;
            var t = Mathf.Clamp(dx / Width, -1f, 1f);
            _rt.localRotation = Quaternion.Euler(0f, 0f, -t * maxTilt);
            SetStamps(t / throwThreshold);

            var past = Mathf.Abs(dx) > Width * throwThreshold;
            if (past != _pastThreshold)
            {
                _pastThreshold = past;
                ThresholdChanged?.Invoke(past);
            }
        }

        /// <param name="signed">-1..1 (beyond is clamped): negative shows NOPE, positive shows LOVE.</param>
        void SetStamps(float signed)
        {
            if (loveStamp) loveStamp.alpha = Mathf.Clamp01(signed);
            if (nopeStamp) nopeStamp.alpha = Mathf.Clamp01(-signed);
        }

        IEnumerator SnapBack()
        {
            var from = _rt.anchoredPosition;
            var fromRot = _rt.localRotation;
            var fromStamp = (loveStamp ? loveStamp.alpha : 0f) - (nopeStamp ? nopeStamp.alpha : 0f);
            for (float t = 0; t < 1f; t += Time.unscaledDeltaTime / snapDuration)
            {
                var k = EaseOutBack(t);
                _rt.anchoredPosition = Vector2.LerpUnclamped(from, _home, k);
                _rt.localRotation = Quaternion.SlerpUnclamped(fromRot, Quaternion.identity, k);
                SetStamps(Mathf.Lerp(fromStamp, 0f, t));
                yield return null;
            }
            _rt.anchoredPosition = _home;
            _rt.localRotation = Quaternion.identity;
            SetStamps(0f);
            _pastThreshold = false;
        }

        IEnumerator Fling(SwipeDirection dir, Vector2 releaseVelocity)
        {
            _flying = true;
            Interactable = false;

            var sign = dir == SwipeDirection.Right ? 1f : -1f;
            var parentWidth = ((RectTransform)_rt.parent).rect.width;
            var from = _rt.anchoredPosition;
            // Off-screen horizontally; keep some of the vertical throw so it feels physical.
            var to = new Vector2(_home.x + sign * (parentWidth + Width),
                                 from.y + Mathf.Clamp(releaseVelocity.y * flingDuration, -Width, Width));
            var fromRot = _rt.localRotation.eulerAngles.z;
            if (fromRot > 180f) fromRot -= 360f;
            var toRot = -sign * maxTilt * 2f;

            SetStamps(sign);
            for (float t = 0; t < 1f; t += Time.unscaledDeltaTime / flingDuration)
            {
                var k = t * t; // ease-in: accelerates away
                _rt.anchoredPosition = Vector2.Lerp(from, to, k);
                _rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(fromRot, toRot, k));
                yield return null;
            }

            Swiped?.Invoke(this, dir);
        }

        /// <summary>Small scale "pop" when a card becomes the top card.</summary>
        public void Pop() => StartCoroutine(PopRoutine());

        IEnumerator PopRoutine()
        {
            const float duration = 0.16f;
            for (float t = 0; t < 1f; t += Time.unscaledDeltaTime / duration)
            {
                _rt.localScale = Vector3.one * (1f + 0.04f * Mathf.Sin(t * Mathf.PI));
                yield return null;
            }
            _rt.localScale = Vector3.one;
        }

        static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            t = Mathf.Clamp01(t) - 1f;
            return 1f + c3 * t * t * t + c1 * t * t;
        }
    }
}
