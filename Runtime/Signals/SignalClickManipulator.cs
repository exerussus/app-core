using Exerussus.Payloads;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Signals
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
            UISignal.InvokeButtonPressed(target.name, _payload);
        }
    }
}
