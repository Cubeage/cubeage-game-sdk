using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CubeageSDK.Models;
using UnityEngine;
using UnityEngine.Networking;

namespace CubeageSDK
{
    /// <summary>
    /// Cubeage Game SDK — Commercial Grade
    ///
    /// Features:
    ///   - Anonymous auth + Bearer token management
    ///   - Automatic session management (foreground/background)
    ///   - Event tracking with batch support
    ///   - IAP purchase tracking
    ///   - Ad revenue tracking (LevelPlay ARM compatible)
    ///   - Install attribution (Singular S2S)
    ///   - User properties
    ///   - Force update checks
    ///   - Offline queue with automatic flush
    ///   - Retry with exponential backoff
    ///
    /// Usage:
    ///   CubeageSDK.Instance.Init(config => Debug.Log("Ready"));
    /// </summary>
    public class CubeageSDK : MonoBehaviour
    {
        // -----------------------------------------------------------------------
        // Singleton
        // -----------------------------------------------------------------------

        private static CubeageSDK _instance;

        public static CubeageSDK Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("[CubeageSDK]");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<CubeageSDK>();
                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        private string _apiBaseUrl;
        private string _appKey;
        private string _sdkVersion;
        private bool   _verboseLogging;

        private string _deviceId;
        private string _accessToken;
        private string _refreshToken;
        private string _userId;
        private bool   _initialized;
        private string _currentSessionId;
        private bool   _sessionActive;

        private readonly SdkConfig _config = new SdkConfig();
        private readonly List<QueuedRequest> _offlineQueue = new List<QueuedRequest>();
        private bool _isFlushing;

        private const string PrefDeviceId       = "cubeage_sdk_device_id";
        private const string PrefUserId         = "cubeage_sdk_user_id";
        private const string PrefFirstLaunch    = "cubeage_sdk_launched";
        private const string PrefAccessToken    = "cubeage_sdk_access_token";
        private const string PrefRefreshToken   = "cubeage_sdk_refresh_token";
        private const string QueueFilePath      = "cubeage_sdk_queue.json";

        private const int MaxRetries        = 3;
        private const float BaseRetryDelay  = 1.0f;
        private const int MaxQueueSize      = 500;

        // -----------------------------------------------------------------------
        // Public API — Init
        // -----------------------------------------------------------------------

        /// <summary>
        /// Initialize the SDK. Call once on game start.
        /// Authenticates anonymously, fetches config, starts a session.
        /// </summary>
        public void Init(Action<SdkConfig> onComplete = null, Action<string> onError = null)
        {
            if (_initialized) { onComplete?.Invoke(_config); return; }
            LoadSettings();
            LoadOfflineQueue();
            StartCoroutine(DoInit(onComplete, onError));
        }

        // -----------------------------------------------------------------------
        // Public API — Events
        // -----------------------------------------------------------------------

        /// <summary>Track a custom game event.</summary>
        public void TrackEvent(string eventName, Dictionary<string, object> attributes = null)
        {
            if (!_initialized) { LogWarning("SDK not initialized"); return; }
            var body = JsonUtility.ToJson(new EventBatchRequest
            {
                appKey = _appKey,
                events = new EventItem[]
                {
                    new EventItem { name = eventName }
                }
            });
            EnqueueOrSend("POST", "/api/v1/events/batch", body);
        }

        /// <summary>Track an IAP purchase.</summary>
        public void TrackPurchase(string productId, float amount, string currency,
                                  string transactionId, string receipt)
        {
            if (!_initialized) { LogWarning("SDK not initialized"); return; }
            var body = JsonUtility.ToJson(new PurchaseRequest
            {
                productId     = productId,
                amount        = amount,
                currency      = currency,
                transactionId = transactionId,
                receipt       = receipt
            });
            EnqueueOrSend("POST", "/api/v1/iap/validate", body);
        }

        // -----------------------------------------------------------------------
        // Public API — Sessions
        // -----------------------------------------------------------------------

        /// <summary>Start a game session. Called automatically on foreground.</summary>
        public void StartSession(Action<string> onComplete = null)
        {
            if (!_initialized) { LogWarning("SDK not initialized"); return; }
            if (_sessionActive) { onComplete?.Invoke(_currentSessionId); return; }
            StartCoroutine(DoStartSession(onComplete));
        }

        /// <summary>End the current game session. Called automatically on background.</summary>
        public void EndSession(Action<int> onComplete = null)
        {
            if (!_initialized || !_sessionActive || string.IsNullOrEmpty(_currentSessionId)) return;
            StartCoroutine(DoEndSession(onComplete));
        }

        // -----------------------------------------------------------------------
        // Public API — Attribution
        // -----------------------------------------------------------------------

        /// <summary>
        /// Track install attribution. Call on first launch.
        /// Collects IDFA/GAID (if available), device info, and sends to server.
        /// Server forwards to Singular S2S.
        /// </summary>
        public void TrackAttribution(Action<string, bool> onComplete = null)
        {
            if (!_initialized) { LogWarning("SDK not initialized"); return; }
            StartCoroutine(DoTrackAttribution(onComplete));
        }

        // -----------------------------------------------------------------------
        // Public API — Ad Revenue
        // -----------------------------------------------------------------------

        /// <summary>
        /// Track ad revenue from LevelPlay ARM callback.
        /// Call this from the IronSource ImpressionDataReady event.
        /// </summary>
        public void TrackAdRevenue(AdImpressionData impressionData)
        {
            if (!_initialized) { LogWarning("SDK not initialized"); return; }
            var req = new AdRevenueRequest
            {
                appKey    = _appKey,
                sessionId = _currentSessionId ?? "",
                impressions = new AdImpressionItem[]
                {
                    new AdImpressionItem
                    {
                        adNetwork = impressionData.adNetwork ?? "unknown",
                        adUnit    = impressionData.adUnit ?? "",
                        adFormat  = impressionData.adFormat ?? "",
                        placement = impressionData.placement ?? "",
                        revenue   = impressionData.revenue,
                        currency  = impressionData.currency ?? "USD",
                        precision = impressionData.precision ?? "estimated",
                        country   = impressionData.country ?? ""
                    }
                }
            };
            EnqueueOrSend("POST", "/api/v1/revenue/ad", JsonUtility.ToJson(req));
        }

        // -----------------------------------------------------------------------
        // Public API — User Properties
        // -----------------------------------------------------------------------

        /// <summary>Set a single user property (upsert).</summary>
        public void SetUserProperty(string key, string value)
        {
            SetUserProperties(new Dictionary<string, string> { { key, value } });
        }

        /// <summary>Set multiple user properties at once (upsert).</summary>
        public void SetUserProperties(Dictionary<string, string> properties)
        {
            if (!_initialized) { LogWarning("SDK not initialized"); return; }
            // Build JSON manually since JsonUtility doesn't handle Dictionary
            var sb = new StringBuilder();
            sb.Append("{\"properties\":{");
            bool first = true;
            foreach (var kv in properties)
            {
                if (!first) sb.Append(",");
                sb.Append("\"").Append(EscapeJson(kv.Key)).Append("\":\"")
                  .Append(EscapeJson(kv.Value)).Append("\"");
                first = false;
            }
            sb.Append("}}");
            EnqueueOrSend("PUT", "/api/v1/user/properties", sb.ToString());
        }

        // -----------------------------------------------------------------------
        // Public API — Update Check
        // -----------------------------------------------------------------------

        /// <summary>Check for app updates. Returns version info from server.</summary>
        public void CheckForUpdate(Action<UpdateCheckResponse> onComplete, Action<string> onError = null)
        {
            if (!_initialized) { onError?.Invoke("SDK not initialized"); return; }
            StartCoroutine(DoCheckForUpdate(onComplete, onError));
        }

        // -----------------------------------------------------------------------
        // Public API — Config
        // -----------------------------------------------------------------------

        public string GetConfig(string key, string defaultValue = "")
            => _config.Get(key, defaultValue);

        public bool GetConfigBool(string key, bool defaultValue = false)
            => _config.GetBool(key, defaultValue);

        public int GetConfigInt(string key, int defaultValue = 0)
            => _config.GetInt(key, defaultValue);

        // -----------------------------------------------------------------------
        // Automatic Session Management
        // -----------------------------------------------------------------------

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!_initialized) return;
            if (pauseStatus)
            {
                // App going to background
                EndSession();
                SaveOfflineQueue();
            }
            else
            {
                // App coming to foreground
                StartSession();
                FlushOfflineQueue();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!_initialized) return;
            if (hasFocus)
            {
                StartSession();
                FlushOfflineQueue();
            }
            else
            {
                EndSession();
                SaveOfflineQueue();
            }
        }

        private void OnApplicationQuit()
        {
            if (_initialized && _sessionActive)
            {
                // Best-effort end session (may not complete)
                EndSession();
                SaveOfflineQueue();
            }
        }

        // -----------------------------------------------------------------------
        // Init Coroutine
        // -----------------------------------------------------------------------

        private IEnumerator DoInit(Action<SdkConfig> onComplete, Action<string> onError)
        {
            _deviceId = ResolveDeviceId();

            // Load persisted tokens
            _accessToken  = PlayerPrefs.GetString(PrefAccessToken, "");
            _refreshToken = PlayerPrefs.GetString(PrefRefreshToken, "");
            _userId       = PlayerPrefs.GetString(PrefUserId, "");

            // Authenticate if no token
            if (string.IsNullOrEmpty(_accessToken))
            {
                yield return DoAnonymousAuth();
                if (string.IsNullOrEmpty(_accessToken))
                {
                    onError?.Invoke("Authentication failed");
                    yield break;
                }
            }

            // Fetch remote config
            yield return DoFetchConfig();

            _initialized = true;
            LogVerbose($"[CubeageSDK] Init complete. userId={_userId}");

            // Start initial session
            StartSession();

            // Track attribution on first launch
            if (!PlayerPrefs.HasKey(PrefFirstLaunch))
            {
                PlayerPrefs.SetInt(PrefFirstLaunch, 1);
                PlayerPrefs.Save();
                TrackAttribution();
            }

            // Flush any offline events
            FlushOfflineQueue();

            onComplete?.Invoke(_config);
        }

        // -----------------------------------------------------------------------
        // Anonymous Auth
        // -----------------------------------------------------------------------

        private IEnumerator DoAnonymousAuth()
        {
            var body = $"{{\"deviceId\":\"{EscapeJson(_deviceId)}\",\"appKey\":\"{EscapeJson(_appKey)}\"}}";
            var url = $"{_apiBaseUrl.TrimEnd('/')}/api/v1/auth/anonymous";
            LogVerbose($"POST {url}");

            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 15;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                LogWarning($"[CubeageSDK] Auth failed: {req.error}");
                yield break;
            }

            var resp = JsonUtility.FromJson<AuthResponse>(req.downloadHandler.text);
            if (resp != null && !string.IsNullOrEmpty(resp.accessToken))
            {
                _accessToken  = resp.accessToken;
                _refreshToken = resp.refreshToken;
                _userId       = resp.userId;
                PlayerPrefs.SetString(PrefAccessToken, _accessToken);
                PlayerPrefs.SetString(PrefRefreshToken, _refreshToken);
                PlayerPrefs.SetString(PrefUserId, _userId);
                PlayerPrefs.Save();
                LogVerbose($"[CubeageSDK] Authenticated: {_userId}");
            }
        }

        // -----------------------------------------------------------------------
        // Token Refresh
        // -----------------------------------------------------------------------

        private IEnumerator DoRefreshToken()
        {
            if (string.IsNullOrEmpty(_refreshToken)) yield break;

            var body = $"{{\"refreshToken\":\"{EscapeJson(_refreshToken)}\"}}";
            var url = $"{_apiBaseUrl.TrimEnd('/')}/api/v1/auth/refresh";

            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 15;

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var resp = JsonUtility.FromJson<AuthResponse>(req.downloadHandler.text);
                if (resp != null && !string.IsNullOrEmpty(resp.accessToken))
                {
                    _accessToken  = resp.accessToken;
                    _refreshToken = resp.refreshToken;
                    PlayerPrefs.SetString(PrefAccessToken, _accessToken);
                    PlayerPrefs.SetString(PrefRefreshToken, _refreshToken);
                    PlayerPrefs.Save();
                }
            }
            else
            {
                // Refresh failed — re-authenticate
                _accessToken = "";
                _refreshToken = "";
                yield return DoAnonymousAuth();
            }
        }

        // -----------------------------------------------------------------------
        // Fetch Config
        // -----------------------------------------------------------------------

        private IEnumerator DoFetchConfig()
        {
            var url = $"{_apiBaseUrl.TrimEnd('/')}/api/v1/apps/{UnityWebRequest.EscapeURL(_appKey)}/config";
            LogVerbose($"GET {url}");

            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Authorization", $"Bearer {_accessToken}");
            req.timeout = 15;

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var resp = JsonUtility.FromJson<ConfigResponse>(req.downloadHandler.text);
                    if (resp?.config != null) _config.SetValues(resp.config);
                }
                catch (Exception ex)
                {
                    LogWarning($"[CubeageSDK] Config parse failed: {ex.Message}");
                }
            }
            else if (req.responseCode == 401)
            {
                // Token expired — refresh and retry
                yield return DoRefreshToken();
                if (!string.IsNullOrEmpty(_accessToken))
                {
                    yield return DoFetchConfig();
                }
            }
        }

        // -----------------------------------------------------------------------
        // Session Coroutines
        // -----------------------------------------------------------------------

        private IEnumerator DoStartSession(Action<string> onComplete = null)
        {
            var body = JsonUtility.ToJson(new SessionStartRequest
            {
                appKey     = _appKey,
                appVersion = Application.version,
                osVersion  = SystemInfo.operatingSystem,
                deviceModel = SystemInfo.deviceModel,
                locale     = Application.systemLanguage.ToString(),
                timezone   = TimeZoneInfo.Local.Id
            });

            string responseText = null;
            yield return AuthenticatedRequest("POST", "/api/v1/sessions/start", body,
                (text) => responseText = text);

            if (!string.IsNullOrEmpty(responseText))
            {
                var resp = JsonUtility.FromJson<SessionStartResponse>(responseText);
                if (resp != null && !string.IsNullOrEmpty(resp.sessionId))
                {
                    _currentSessionId = resp.sessionId;
                    _sessionActive = true;
                    LogVerbose($"[CubeageSDK] Session started: {_currentSessionId}");
                    onComplete?.Invoke(_currentSessionId);
                }
            }
        }

        private IEnumerator DoEndSession(Action<int> onComplete = null)
        {
            if (string.IsNullOrEmpty(_currentSessionId)) yield break;

            var sessionId = _currentSessionId;
            _sessionActive = false;
            _currentSessionId = null;

            var body = $"{{\"sessionId\":\"{EscapeJson(sessionId)}\"}}";
            string responseText = null;
            yield return AuthenticatedRequest("POST", "/api/v1/sessions/end", body,
                (text) => responseText = text);

            if (!string.IsNullOrEmpty(responseText))
            {
                var resp = JsonUtility.FromJson<SessionEndResponse>(responseText);
                if (resp != null)
                {
                    LogVerbose($"[CubeageSDK] Session ended: {resp.durationSeconds}s");
                    onComplete?.Invoke(resp.durationSeconds);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Attribution Coroutine
        // -----------------------------------------------------------------------

        private IEnumerator DoTrackAttribution(Action<string, bool> onComplete = null)
        {
            var body = JsonUtility.ToJson(new AttributionRequest
            {
                appKey        = _appKey,
                platform      = GetPlatformString(),
                deviceId      = _deviceId,
                advertisingId = GetAdvertisingId(),
                appVersion    = Application.version,
                osVersion     = SystemInfo.operatingSystem,
                deviceModel   = SystemInfo.deviceModel,
                locale        = Application.systemLanguage.ToString(),
                referrer      = ""
            });

            string responseText = null;
            yield return AuthenticatedRequest("POST", "/api/v1/attribution/install", body,
                (text) => responseText = text);

            if (!string.IsNullOrEmpty(responseText))
            {
                var resp = JsonUtility.FromJson<AttributionResponse>(responseText);
                if (resp != null)
                {
                    LogVerbose($"[CubeageSDK] Attribution: {resp.attributionId} first={resp.isFirstInstall}");
                    onComplete?.Invoke(resp.attributionId, resp.isFirstInstall);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Update Check
        // -----------------------------------------------------------------------

        private IEnumerator DoCheckForUpdate(Action<UpdateCheckResponse> onComplete, Action<string> onError)
        {
            string responseText = null;
            yield return AuthenticatedRequest("GET",
                $"/api/v1/apps/{UnityWebRequest.EscapeURL(_appKey)}/update", null,
                (text) => responseText = text,
                (err) => onError?.Invoke(err));

            if (!string.IsNullOrEmpty(responseText))
            {
                var resp = JsonUtility.FromJson<UpdateCheckResponse>(responseText);
                onComplete?.Invoke(resp);
            }
        }

        // -----------------------------------------------------------------------
        // Authenticated HTTP with Retry + Token Refresh
        // -----------------------------------------------------------------------

        private IEnumerator AuthenticatedRequest(string method, string path, string body,
                                                  Action<string> onSuccess = null,
                                                  Action<string> onError = null,
                                                  int retryCount = 0)
        {
            var url = _apiBaseUrl.TrimEnd('/') + path;
            LogVerbose($"{method} {url}");

            using var req = new UnityWebRequest(url, method);
            if (body != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {_accessToken}");
            req.timeout = 15;

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                onSuccess?.Invoke(req.downloadHandler.text);
            }
            else if (req.responseCode == 401 && retryCount == 0)
            {
                // Token expired — refresh and retry once
                yield return DoRefreshToken();
                if (!string.IsNullOrEmpty(_accessToken))
                {
                    yield return AuthenticatedRequest(method, path, body, onSuccess, onError, 1);
                }
                else
                {
                    onError?.Invoke("Authentication expired");
                }
            }
            else if (retryCount < MaxRetries && IsRetryable(req))
            {
                // Exponential backoff
                var delay = BaseRetryDelay * Mathf.Pow(2, retryCount);
                LogVerbose($"[CubeageSDK] Retrying {path} in {delay}s (attempt {retryCount + 1})");
                yield return new WaitForSeconds(delay);
                yield return AuthenticatedRequest(method, path, body, onSuccess, onError, retryCount + 1);
            }
            else
            {
                var err = $"{method} {path} failed: {req.error} (HTTP {req.responseCode})";
                LogWarning($"[CubeageSDK] {err}");
                onError?.Invoke(err);
            }
        }

        private bool IsRetryable(UnityWebRequest req)
        {
            if (req.result == UnityWebRequest.Result.ConnectionError) return true;
            var code = req.responseCode;
            return code == 429 || code >= 500;
        }

        // -----------------------------------------------------------------------
        // Offline Queue
        // -----------------------------------------------------------------------

        private void EnqueueOrSend(string method, string path, string body)
        {
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                EnqueueOffline(method, path, body);
                return;
            }
            StartCoroutine(AuthenticatedRequestWithFallback(method, path, body));
        }

        private IEnumerator AuthenticatedRequestWithFallback(string method, string path, string body)
        {
            bool success = false;
            yield return AuthenticatedRequest(method, path, body,
                (_) => success = true,
                (_) => success = false);

            if (!success)
            {
                EnqueueOffline(method, path, body);
            }
        }

        private void EnqueueOffline(string method, string path, string body)
        {
            if (_offlineQueue.Count >= MaxQueueSize)
            {
                LogWarning("[CubeageSDK] Offline queue full — dropping oldest event");
                _offlineQueue.RemoveAt(0);
            }
            _offlineQueue.Add(new QueuedRequest
            {
                method    = method,
                path      = path,
                body      = body,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        private void FlushOfflineQueue()
        {
            if (_isFlushing || _offlineQueue.Count == 0) return;
            if (Application.internetReachability == NetworkReachability.NotReachable) return;
            StartCoroutine(DoFlushQueue());
        }

        private IEnumerator DoFlushQueue()
        {
            _isFlushing = true;
            LogVerbose($"[CubeageSDK] Flushing {_offlineQueue.Count} queued events");

            while (_offlineQueue.Count > 0 &&
                   Application.internetReachability != NetworkReachability.NotReachable)
            {
                var item = _offlineQueue[0];
                bool success = false;

                yield return AuthenticatedRequest(item.method, item.path, item.body,
                    (_) => success = true);

                if (success)
                {
                    _offlineQueue.RemoveAt(0);
                }
                else
                {
                    // Stop flushing on failure — will retry later
                    break;
                }

                // Small delay between requests to avoid overwhelming the server
                yield return new WaitForSeconds(0.1f);
            }

            _isFlushing = false;
            SaveOfflineQueue();
        }

        // -----------------------------------------------------------------------
        // Queue Persistence
        // -----------------------------------------------------------------------

        private void SaveOfflineQueue()
        {
            try
            {
                var path = Path.Combine(Application.persistentDataPath, QueueFilePath);
                var wrapper = new QueueWrapper { items = _offlineQueue.ToArray() };
                File.WriteAllText(path, JsonUtility.ToJson(wrapper));
            }
            catch (Exception ex)
            {
                LogWarning($"[CubeageSDK] Failed to save queue: {ex.Message}");
            }
        }

        private void LoadOfflineQueue()
        {
            try
            {
                var path = Path.Combine(Application.persistentDataPath, QueueFilePath);
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                var wrapper = JsonUtility.FromJson<QueueWrapper>(json);
                if (wrapper?.items != null)
                {
                    _offlineQueue.Clear();
                    _offlineQueue.AddRange(wrapper.items);
                    LogVerbose($"[CubeageSDK] Loaded {_offlineQueue.Count} queued events");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"[CubeageSDK] Failed to load queue: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Device ID
        // -----------------------------------------------------------------------

        private string ResolveDeviceId()
        {
            var id = SystemInfo.deviceUniqueIdentifier;
            if (!string.IsNullOrEmpty(id) && id != SystemInfo.unsupportedIdentifier) return id;

            id = PlayerPrefs.GetString(PrefDeviceId, "");
            if (!string.IsNullOrEmpty(id)) return id;

            id = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(PrefDeviceId, id);
            PlayerPrefs.Save();
            return id;
        }

        private static string GetAdvertisingId()
        {
#if UNITY_IOS
            return UnityEngine.iOS.Device.advertisingIdentifier ?? "";
#elif UNITY_ANDROID
            // Requires com.google.android.gms
            try
            {
                using var cls = new AndroidJavaClass("com.google.android.gms.ads.identifier.AdvertisingIdClient");
                using var context = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                    .GetStatic<AndroidJavaObject>("currentActivity");
                using var info = cls.CallStatic<AndroidJavaObject>("getAdvertisingIdInfo", context);
                return info.Call<string>("getId") ?? "";
            }
            catch { return ""; }
#else
            return "";
#endif
        }

        // -----------------------------------------------------------------------
        // Settings
        // -----------------------------------------------------------------------

        private void LoadSettings()
        {
            var settings = CubeageSDKSettings.Load();
            if (settings != null)
            {
                _apiBaseUrl     = settings.apiBaseUrl;
                _appKey         = settings.appKey;
                _sdkVersion     = settings.sdkVersion;
                _verboseLogging = settings.verboseLogging;
            }
            else
            {
                _apiBaseUrl     = "https://api.cubeage.com";
                _appKey         = Application.identifier;
                _sdkVersion     = "2.0.0";
                _verboseLogging = Debug.isDebugBuild;
                LogWarning("[CubeageSDK] CubeageSDKSettings asset not found — using defaults");
            }
        }

        // -----------------------------------------------------------------------
        // Utilities
        // -----------------------------------------------------------------------

        private static string GetPlatformString()
        {
#if UNITY_IOS
            return "ios";
#elif UNITY_ANDROID
            return "android";
#else
            return Application.platform.ToString().ToLower();
#endif
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private void LogVerbose(string msg)
        {
            if (_verboseLogging) Debug.Log(msg);
        }

        private static void LogWarning(string msg) => Debug.LogWarning(msg);

        // -----------------------------------------------------------------------
        // Internal Models (private, for JSON serialization)
        // -----------------------------------------------------------------------

        [Serializable]
        private class AuthResponse
        {
            public string accessToken;
            public string refreshToken;
            public string userId;
        }

        [Serializable]
        private class ConfigResponse
        {
            public Dictionary<string, string> config;
        }

        [Serializable]
        private class SessionEndResponse
        {
            public int durationSeconds;
        }

        [Serializable]
        private class AttributionResponse
        {
            public string attributionId;
            public bool isFirstInstall;
        }

        [Serializable]
        private class QueuedRequest
        {
            public string method;
            public string path;
            public string body;
            public long timestamp;
        }

        [Serializable]
        private class QueueWrapper
        {
            public QueuedRequest[] items;
        }

        [Serializable]
        private class EventBatchRequest
        {
            public string appKey;
            public EventItem[] events;
        }

        [Serializable]
        private class EventItem
        {
            public string name;
        }
    }
}
