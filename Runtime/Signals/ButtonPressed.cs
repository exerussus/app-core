using Exerussus.Payloads;

namespace Exerussus.AppCore.Signals
{
    public struct ButtonPressed
    {
        public string Id;
        public Payload Payload;
        
        public override string ToString()
        {
            return $"id:{Id}, payload:{{{Payload}}}";
        }
    }
}
