using UnityEngine.UIElements;
using Exerussus.AppCore.Views;
using Exerussus.AppCore.Services;
using Exerussus.AppCore.Signals;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Сборка манипуляторов для страниц и попапов.
    /// </summary>
    public partial class AppRunner
    {
        internal IAppManipulatorBuilder[] _appManipulatorBuilders;

        private readonly PayloadBuilder _payloadBuilder = new();

        // Internal
        internal void RegisterAppView(IAppView appView)
        {
            var soundLibrary = appView.OverrideSoundLibrary == null ? uiSoundLibrary : appView.OverrideSoundLibrary;
            
            if (soundLibrary != null) appView.Root.Query<Button>().ForEach(btn =>
            {
                if (_appManipulatorBuilders is { Length: > 0 })
                {
                    foreach (var builder in _appManipulatorBuilders)
                    {
                        builder.OnBuildButtonManipulator(appView, btn, _payloadBuilder);
                    }
                }
                
                if (btn.ClassListContains("signal-button")) btn.AddManipulator(new SignalClickManipulator(_payloadBuilder.End()));
            });
            
            foreach (var builder in _appManipulatorBuilders)
            {
                builder.OnBuildManipulators(appView);
            }
        }
    }
}
