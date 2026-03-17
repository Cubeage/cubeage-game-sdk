using System;
using System.Collections.Generic;

namespace CubeageSDK.Models
{
    /// <summary>
    /// Holds the remote config key-value map returned by the server on init.
    /// </summary>
    [Serializable]
    public class SdkConfig
    {
        private Dictionary<string, string> _values = new Dictionary<string, string>();

        public SdkConfig() { }

        public SdkConfig(Dictionary<string, string> values)
        {
            _values = values ?? new Dictionary<string, string>();
        }

        /// <summary>Returns the config value for key, or defaultValue if not found.</summary>
        public string Get(string key, string defaultValue = "")
        {
            return _values.TryGetValue(key, out var val) ? val : defaultValue;
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            var val = Get(key);
            if (string.IsNullOrEmpty(val)) return defaultValue;
            return val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public int GetInt(string key, int defaultValue = 0)
        {
            var val = Get(key);
            return int.TryParse(val, out var i) ? i : defaultValue;
        }

        public void SetValues(Dictionary<string, string> values)
        {
            _values = values ?? new Dictionary<string, string>();
        }
    }
}
