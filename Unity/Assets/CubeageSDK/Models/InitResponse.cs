using System;
using System.Collections.Generic;

namespace CubeageSDK.Models
{
    [Serializable]
    public class InitResponse
    {
        public string userId;
        public string sessionToken;
        public Dictionary<string, string> config;
    }
}
