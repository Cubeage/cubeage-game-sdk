using System;
using System.Collections.Generic;

namespace CubeageSDK.Models
{
    [Serializable]
    public class EventRequest
    {
        public string sessionToken;
        public string name;
        public Dictionary<string, object> attributes;
    }
}
