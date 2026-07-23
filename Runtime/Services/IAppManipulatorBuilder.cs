using UnityEngine.UIElements;
using Exerussus.AppCore.Signals;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Services
{
    public interface IAppManipulatorBuilder
    {
        public virtual void OnBuildButtonManipulator(IAppView appView, Button button, PayloadBuilder payloadBuilder) {  }
        public virtual void OnBuildManipulators(IAppView appView) {  }
    }
}
