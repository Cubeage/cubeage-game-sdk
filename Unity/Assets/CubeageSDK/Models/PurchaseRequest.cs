using System;

namespace CubeageSDK.Models
{
    [Serializable]
    public class PurchaseRequest
    {
        public string sessionToken;
        public string productId;
        public float amount;
        public string currency;
        public string transactionId;
        public string receipt;
    }
}
