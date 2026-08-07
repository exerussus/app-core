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

        /// <summary>
        /// Навешивает манипуляторы на кнопки вью (страницы или попапа).
        /// Вызывается один раз на вью — при первой активации страницы или первом монтировании попапа.
        /// </summary>
        internal void RegisterAppView(IAppView appView)
        {
            // Проход по кнопкам не зависит от библиотеки звуков: тот же цикл строит навигационные
            // манипуляторы. Решение «есть ли звук» принимает звуковой сервис внутри себя.
            appView.Root.Query<Button>().ForEach(btn =>
            {
                foreach (var builder in _appManipulatorBuilders)
                {
                    builder.OnBuildButtonManipulator(appView, btn, _payloadBuilder);
                }

                // Скоуп пейлоада закрываем на КАЖДОЙ кнопке, даже если её никто не пометил:
                // иначе недозабранный пейлоад утёк бы в следующую кнопку и переписал ей цель.
                var payload = _payloadBuilder.End();

                if (btn.ClassListContains("signal-button")) btn.AddManipulator(new SignalClickManipulator(payload));
                else if (payload.IsValid()) payload.Dispose();
            });

            foreach (var builder in _appManipulatorBuilders)
            {
                builder.OnBuildManipulators(appView);
            }
        }
    }
}
