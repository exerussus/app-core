using Exerussus.Payloads;
using UnityEngine.UIElements;

namespace AppCore.Runtime.Core.InternalServices.Manipulators.Signal
{
    public class SignalClickManipulator : Manipulator
    {
        public SignalClickManipulator(Payload payload)
        {
            _payload = payload;
        }

        private readonly Payload _payload;
        
        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<PointerUpEvent>(OnClick, TrickleDown.TrickleDown);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerUpEvent>(OnClick, TrickleDown.TrickleDown);
            _payload.Dispose();
        }

        private void OnClick(PointerUpEvent evt)
        {
            UiSignal.InvokeButtonPressed(target.name, _payload);
        }
    }

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