// WebSocket connection to one Party room: connect, dispatch typed events,
// keepalive, and reconnect with backoff (mobile sockets die on every backgrounding).
// On (re)connect the server sends a full room_state snapshot, so there is
// nothing to reconcile client-side. Protocol: docs/06 §4.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NativeWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YoMama.Core;

namespace YoMama.Multiplayer
{
    public class RoomClient : MonoBehaviour
    {
        [SerializeField] float pingInterval = 20f;
        [SerializeField] float maxBackoff = 15f;

        public event Action<RoomState> StateReceived;
        public event Action<RoundStart> RoundStarted;
        public event Action<VoteProgress> VoteProgressed;
        public event Action<RoundResult> RoundEnded;
        public event Action<GameOver> GameEnded;
        public event Action<RoomError> ErrorReceived;
        public event Action<bool> ConnectionChanged;

        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;

        static readonly JsonSerializer Serializer = JsonSerializer.Create(ApiClient.JsonSettings);

        WebSocket _ws;
        string _url;
        string _displayName;
        bool _wantConnected;
        int _attempt;
        long _lastSeq;
        float _pingTimer;

        public void Connect(string wsUrl, string displayName)
        {
            _url = wsUrl;
            _displayName = displayName;
            _wantConnected = true;
            _attempt = 0;
            _lastSeq = 0;
            Open();
        }

        public async Task Leave()
        {
            _wantConnected = false;
            StopAllCoroutines();
            if (_ws != null) await _ws.Close();
            _ws = null;
        }

        // ---- Intents (client → server) --------------------------------------

        public void Vote(int round, SwipeDirection dir) => Send(new { type = "vote", round, direction = dir.ToWire() });
        public void StartGame(int rounds = 10) => Send(new { type = "start", rounds });
        public void Next() => Send(new { type = "next" });
        public void Kick(string playerId) => Send(new { type = "kick", player_id = playerId });

        // ---- Connection -----------------------------------------------------

        void Open()
        {
            var token = AppServices.Instance.Api.Token;
#if UNITY_WEBGL && !UNITY_EDITOR
            // Browsers can't set WS headers; the server also accepts ?token= (and must not log it).
            _ws = new WebSocket(_url + "?token=" + Uri.EscapeDataString(token));
#else
            _ws = new WebSocket(_url, new Dictionary<string, string> { { "Authorization", "Bearer " + token } });
#endif
            var ws = _ws;
            ws.OnOpen += () =>
            {
                _attempt = 0;
                _pingTimer = 0f;
                ConnectionChanged?.Invoke(true);
                Send(new { type = "hello", name = _displayName });
            };
            ws.OnMessage += bytes => Handle(System.Text.Encoding.UTF8.GetString(bytes));
            ws.OnError += err => Debug.LogWarning("[room] ws error: " + err);
            ws.OnClose += code =>
            {
                if (ws != _ws) return; // a stale socket from before a reconnect
                ConnectionChanged?.Invoke(false);
                if (_wantConnected && isActiveAndEnabled) StartCoroutine(Reconnect());
            };

            // NativeWebSocket's Connect() task runs the receive loop and only completes
            // when the socket closes — don't await it.
            _ = ws.Connect();
        }

        IEnumerator Reconnect()
        {
            var delay = Mathf.Min(Mathf.Pow(2f, _attempt), maxBackoff) + UnityEngine.Random.Range(0f, 0.5f);
            _attempt++;
            yield return new WaitForSecondsRealtime(delay);
            if (_wantConnected) Open();
        }

        void Update()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            _ws?.DispatchMessageQueue(); // NativeWebSocket delivers messages on the main thread only via this
#endif
            if (!IsConnected) return;
            _pingTimer += Time.unscaledDeltaTime;
            if (_pingTimer >= pingInterval)
            {
                _pingTimer = 0f;
                Send(new { type = "ping" });
            }
        }

        async void OnDestroy()
        {
            _wantConnected = false;
            if (_ws != null) await _ws.Close();
        }

        void Send(object message)
        {
            if (!IsConnected) return;
            _ = _ws.SendText(JsonConvert.SerializeObject(message, ApiClient.JsonSettings));
        }

        // ---- Dispatch (server → client) -------------------------------------

        void Handle(string json)
        {
            JObject msg;
            try { msg = JObject.Parse(json); }
            catch (JsonException) { Debug.LogWarning("[room] bad frame: " + json); return; }

            var type = (string)msg["type"];
            var seq = (long?)msg["seq"] ?? 0;

            // room_state is a full snapshot and always wins; anything older than it is stale.
            if (type == "room_state") _lastSeq = seq;
            else if (seq != 0 && seq <= _lastSeq) return;
            else if (seq != 0) _lastSeq = seq;

            switch (type)
            {
                case "room_state": StateReceived?.Invoke(msg.ToObject<RoomState>(Serializer)); break;
                case "round_start": RoundStarted?.Invoke(msg.ToObject<RoundStart>(Serializer)); break;
                case "vote_progress": VoteProgressed?.Invoke(msg.ToObject<VoteProgress>(Serializer)); break;
                case "round_result": RoundEnded?.Invoke(msg.ToObject<RoundResult>(Serializer)); break;
                case "game_over": GameEnded?.Invoke(msg.ToObject<GameOver>(Serializer)); break;
                case "entitlements_changed": _ = AppServices.Instance.Entitlements.RefreshAsync(); break;
                case "error": ErrorReceived?.Invoke(msg.ToObject<RoomError>(Serializer)); break;
                case "pong": break;
                default: Debug.Log("[room] unknown message type: " + type); break;
            }
        }
    }
}
