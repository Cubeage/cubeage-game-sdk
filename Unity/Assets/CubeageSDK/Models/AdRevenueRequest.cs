using System;

namespace CubeageSDK.Models
{
    [Serializable]
    public class AdRevenueRequest
    {
        public string appKey;
        public string sessionId;
        public AdImpressionItem[] impressions;
    }

    [Serializable]
    public class AdImpressionItem
    {
        public string adNetwork;
        public string adUnit;
        public string adFormat;
        public string placement;
        public float revenue;
        public string currency;
        public string precision;
        public string country;
    }

    /// <summary>
    /// Wrapper for ad impression data from mediation SDKs (e.g., LevelPlay ARM).
    /// Create an instance from the mediation callback and pass to TrackAdRevenue().
    /// </summary>
    [Serializable]
    public class AdImpressionData
    {
        public string adNetwork;
        public string adUnit;
        public string adFormat;
        public string placement;
        public float revenue;
        public string currency;
        public string precision;
        public string country;
    }
}
