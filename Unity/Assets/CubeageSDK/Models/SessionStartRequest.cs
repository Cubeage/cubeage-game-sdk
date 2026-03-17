using System;

namespace CubeageSDK.Models
{
    [Serializable]
    public class SessionStartRequest
    {
        public string appKey;
        public string appVersion;
        public string osVersion;
        public string deviceModel;
        public string locale;
        public string timezone;
    }
}
