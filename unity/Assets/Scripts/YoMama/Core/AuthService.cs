// Anonymous-first auth (docs/01-architecture.md §6).
// Persists a device id + JWT in PlayerPrefs. For production, move the token to
// the iOS Keychain / Android Keystore via a secure-storage plugin.
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace YoMama.Core
{
    public class AuthService
    {
        const string DeviceIdKey = "yomama.device_id";
        const string TokenKey = "yomama.token";
        const string UserIdKey = "yomama.user_id";

        readonly ApiClient _api;

        public string UserId { get; private set; }
        public bool IsSignedIn => !string.IsNullOrEmpty(_api.Token) && !string.IsNullOrEmpty(UserId);

        public AuthService(ApiClient api)
        {
            _api = api;
        }

        /// <summary>
        /// Always re-calls /v1/auth/anon: the same device id returns the same user
        /// and a fresh token, so an expired token heals itself on launch.
        /// Falls back to the cached token when offline.
        /// </summary>
        public async Task SignInAsync()
        {
            var deviceId = PlayerPrefs.GetString(DeviceIdKey, null);
            if (string.IsNullOrEmpty(deviceId))
            {
                deviceId = Guid.NewGuid().ToString();
                PlayerPrefs.SetString(DeviceIdKey, deviceId);
            }

            try
            {
                var res = await _api.AuthAnon(deviceId);
                _api.Token = res.Token;
                UserId = res.User.Id;
                PlayerPrefs.SetString(TokenKey, res.Token);
                PlayerPrefs.SetString(UserIdKey, res.User.Id);
                PlayerPrefs.Save();
            }
            catch (ApiException e) when (e.Status == 0)
            {
                _api.Token = PlayerPrefs.GetString(TokenKey, null);
                UserId = PlayerPrefs.GetString(UserIdKey, null);
                if (!IsSignedIn) throw;
                Debug.LogWarning("[auth] offline, using cached token");
            }
        }
    }
}
