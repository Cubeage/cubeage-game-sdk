using System;

namespace CubeageSDK.Models
{
    [Serializable]
    public class InitRequest
    {
        public string appKey;
        public string sdkVersion;
        public string deviceId;
        public string advertisingId;
        public string platform;
        public string appVersion;
        public string osVersion;
        public string deviceModel;
        public string language;
        public string timezone;
        public bool isFirstLaunch;
        public string sessionId;
    }
}
