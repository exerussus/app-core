using UnityEngine.UIElements;
using Exerussus.AppCore.Views;
using Exerussus.AppCore.Services;
using Exerussus.AppCore.Signals;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Обвязка вёрстки вью: кнопки-сигналы и манипуляторы сервисов.
    /// </summary>
    public partial class AppRunner
    {
        internal const string SignalButtonClass = "signal-button";

        internal IAppManipulatorBuilder[] _appManipulatorBuilders = System.Array.Empty<IAppManipulatorBuilder>();

        /// <summary>
        /// Навешивает поведение на кнопки вью (страницы, попапа или фрагмента).
        /// Вызывается один раз на фактическое монтирование вёрстки.
        /// </summary>
        internal void RegisterAppView(IAppView appView)
        {
            if (appView?.Root == null) return;

            appView.Root.Query<Button>().ForEach(btn =>
            {
                for (var i = 0; i < _appManipulatorBuilders.Length; i++)
                    _appManipulatorBuilders[i].OnBuildButtonManipulator(appView, btn);

                // SignalButton поднимает сигнал сам; класс нужен обычным кнопкам.
                if (btn is not SignalButton && btn.ClassListContains(SignalButtonClass))
                {
                    var id = btn.name;
                    if (!string.IsNullOrEmpty(id)) btn.clicked += () => AppSignals.Raise(id);
                }
            });

            for (var i = 0; i < _appManipulatorBuilders.Length; i++)
                _appManipulatorBuilders[i].OnBuildManipulators(appView);
        }
    }
}
