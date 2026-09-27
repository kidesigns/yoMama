// Thin typed client over UnityWebRequest for the Go API (docs/02 + docs/06).
using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine.Networking;

namespace YoMama.Core
{
    public class ApiException : Exception
    {
        public readonly long Status;
        public readonly string Code;

        public ApiException(long status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }

        public bool IsDailyLimit => Status == 402 && Code == "daily_limit_reached";
        public bool IsProRequired => Status == 403 && Code == "pro_required";
    }

    public class ApiClient
    {
        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
            NullValueHandling = NullValueHandling.Ignore,
        };

        readonly string _baseUrl;

        /// <summary>Bearer token; set by AuthService after /v1/auth/anon.</summary>
        public string Token { get; set; }

        public string BaseUrl => _baseUrl;

        public ApiClient(string baseUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
        }

        // ---- Endpoints -----------------------------------------------------

        public Task<AuthAnonResponse> AuthAnon(string deviceId) =>
            Send<AuthAnonResponse>("POST", "/v1/auth/anon", new AuthAnonRequest { DeviceId = deviceId });

        public Task<JokePage> GetFeed(string cursor = null, int limit = 20)
        {
            var path = $"/v1/jokes/feed?limit={limit}";
            if (!string.IsNullOrEmpty(cursor)) path += "&cursor=" + UnityWebRequest.EscapeURL(cursor);
            return Send<JokePage>("GET", path);
        }

        public Task<SwipeResponse> Swipe(string jokeId, SwipeDirection dir) =>
            Send<SwipeResponse>("POST", "/v1/swipes", new SwipeRequest { JokeId = jokeId, Direction = dir.ToWire() });

        public Task<Entitlements> GetEntitlements() => Send<Entitlements>("GET", "/v1/me/entitlements");

        public Task<RoomTicket> CreateRoom() => Send<RoomTicket>("POST", "/v1/rooms");

        public Task<RoomTicket> JoinRoom(string code) =>
            Send<RoomTicket>("POST", $"/v1/rooms/{UnityWebRequest.EscapeURL(code.ToUpperInvariant())}/join");

        // ---- Plumbing ------------------------------------------------------

        async Task<T> Send<T>(string method, string path, object body = null)
        {
            using var req = new UnityWebRequest(_baseUrl + path, method);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 15;
            if (body != null)
            {
                var json = JsonConvert.SerializeObject(body, JsonSettings);
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            if (!string.IsNullOrEmpty(Token)) req.SetRequestHeader("Authorization", "Bearer " + Token);

            await SendAsync(req);

            var text = req.downloadHandler.text;
            if (req.result == UnityWebRequest.Result.ConnectionError)
                throw new ApiException(0, "network", req.error);

            if (req.responseCode >= 400)
            {
                ApiErrorBody err = null;
                try { err = JsonConvert.DeserializeObject<ApiErrorBody>(text, JsonSettings); }
                catch (JsonException) { /* non-JSON error body */ }
                throw new ApiException(req.responseCode, err?.Error?.Code ?? "http_" + req.responseCode,
                    err?.Error?.Message ?? req.error);
            }

            if (typeof(T) == typeof(object) || string.IsNullOrEmpty(text)) return default;
            return JsonConvert.DeserializeObject<T>(text, JsonSettings);
        }

        static Task SendAsync(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<bool>();
            var op = req.SendWebRequest();
            if (op.isDone) tcs.SetResult(true);
            else op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }

        /// <summary>ws(s):// base derived from the HTTP base URL.</summary>
        public string WebSocketUrl(string path)
        {
            var b = _baseUrl.StartsWith("https://") ? "wss://" + _baseUrl.Substring(8)
                  : _baseUrl.StartsWith("http://") ? "ws://" + _baseUrl.Substring(7)
                  : _baseUrl;
            return b + path;
        }
    }
}
