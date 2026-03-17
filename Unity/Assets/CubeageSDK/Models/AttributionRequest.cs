using System;

namespace CubeageSDK.Models
{
    [Serializable]
    public class AttributionRequest
    {
        public string appKey;
        public string platform;
        public string deviceId;
        public string advertisingId;
        public string appVersion;
        public string osVersion;
        public string deviceModel;
        public string locale;
        public string referrer;
        public string utmSource;
        public string utmMedium;
        public string utmCampaign;
    }
}
