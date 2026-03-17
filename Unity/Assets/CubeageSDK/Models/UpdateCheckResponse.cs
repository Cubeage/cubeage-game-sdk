using System;

namespace CubeageSDK.Models
{
    [Serializable]
    public class UpdateCheckResponse
    {
        public string currentVersion;
        public string minimumVersion;
        public string updateUrl;
        public bool forceUpdate;
    }
}
