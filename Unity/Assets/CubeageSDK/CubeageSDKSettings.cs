using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CubeageSDK.Editor
{
    /// <summary>
    /// ScriptableObject that holds SDK configuration.
    /// Create one via Assets > Create > CubeageSDK > Settings,
    /// or let the SDK auto-create it at runtime.
    ///
    /// Fields here are used at edit-time AND baked into the build via Resources.
    /// </summary>
    [CreateAssetMenu(fileName = "CubeageSDKSettings", menuName = "CubeageSDK/Settings")]
    public class CubeageSDKSettings : ScriptableObject
    {
        private const string ResourcePath = "CubeageSDKSettings";
        private const string AssetPath   = "Assets/CubeageSDK/Resources/CubeageSDKSettings.asset";

        [Header("Server")]
        [Tooltip("Base URL of the Cubeage Platform backend, e.g. https://api.cubeage.com")]
        public string apiBaseUrl = "https://api.cubeage.com";

        [Header("App")]
        [Tooltip("Unique key identifying this game + platform, e.g. fun_showhand_ios")]
        public string appKey = "";

        [Header("SDK")]
        [Tooltip("SDK version string sent in every init call")]
        public string sdkVersion = "1.0.0";

        [Tooltip("Log SDK HTTP calls and responses to the Unity console")]
        public bool verboseLogging = false;

        [Header("Legacy Migration")]
        [Tooltip(
            "PlayerPrefs key where the OLD game SDK stored its session/device token.\n" +
            "On first launch, if this key has a value, the SDK will call /api/v1/sdk/migrate " +
            "to recover the user's legacy account.\n" +
            "Leave empty if this game has no legacy users to migrate.\n\n" +
            "Examples:\n" +
            "  tntmobi_access_token  (Big2TycoonHK old PHP SDK)\n" +
            "  platform_access_token (Cubeage Platform C# games)\n" +
            "  game_device_id        (standalone Unity games)"
        )]
        public string legacyTokenKey = "";

        // -----------------------------------------------------------------------
        // Runtime access
        // -----------------------------------------------------------------------

        private static CubeageSDKSettings _instance;

        /// <summary>
        /// Load settings from Resources. Returns null if not found.
        /// Place the asset at Assets/CubeageSDK/Resources/CubeageSDKSettings.asset.
        /// </summary>
        public static CubeageSDKSettings Load()
        {
            if (_instance != null) return _instance;
            _instance = Resources.Load<CubeageSDKSettings>(ResourcePath);
            return _instance;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Create a default settings asset in the project (editor-only).
        /// </summary>
        [MenuItem("Tools/CubeageSDK/Create Settings Asset")]
        public static void CreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<CubeageSDKSettings>(AssetPath);
            if (settings != null)
            {
                Debug.Log("[CubeageSDK] Settings asset already exists at " + AssetPath);
                Selection.activeObject = settings;
                return;
            }

            System.IO.Directory.CreateDirectory("Assets/CubeageSDK/Resources");
            settings = CreateInstance<CubeageSDKSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = settings;
            Debug.Log("[CubeageSDK] Settings asset created at " + AssetPath);
        }
#endif
    }
}
