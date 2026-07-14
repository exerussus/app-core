
using Exerussus.Payloads;

namespace AppCore.Runtime.Core.Models
{
    public class PayloadBuilder
    {
        private Payload _payload;
        
        public Payload CreatePayload()
        {
            if (_payload == Payload.None || !_payload.IsValid())
            {
                _payload = Payload.Create();
            }
            
            return  _payload;
        }
        
        internal Payload End()
        {
            if (_payload.IsEmpty())
            {
                _payload.Dispose();
                return Payload.None;
            }
            
            return  _payload;
        }
    }
}