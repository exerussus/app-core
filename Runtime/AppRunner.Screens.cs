using Cysharp.Threading.Tasks;
using UnityEngine;
using Exerussus.AppCore.Screens;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Скрины: экран загрузки и самодостаточные оверлеи ошибок.
    /// </summary>
    public partial class AppRunner
    {
        /// <summary>
        /// Флаг принудительного удержания экрана загрузки.
        /// Пока <c>true</c> — экран не скрывается по завершении навигации.
        /// Управляется через <see cref="SetForceScreen"/>.
        /// </summary>
        private bool _isForceScreen;

        
        /// <summary>
        /// Есть ли у раннера экран загрузки. Вычисляется один раз в <see cref="Awake"/>
        /// как <c>loadingScreen != null</c>. Если экран не назначен в инспекторе — весь код
        /// показа/скрытия экрана становится no-op, а приложение продолжает работать без него.
        /// </summary>
        private bool _hasScreen;

        /// <summary>
        /// Виден ли сейчас экран загрузки. Всегда <c>false</c>, если экран не назначен
        /// (см. <see cref="_hasScreen"/>).
        /// </summary>
        private bool IsScreenVisible => _hasScreen && loadingScreen.IsVisible;

        /// <summary>
        /// Показывает экран загрузки, если он назначен; иначе — no-op
        /// (возвращает <see cref="UniTask.CompletedTask"/>).
        /// </summary>
        /// <param name="instant">Если <c>true</c> — без анимации появления.</param>
        private UniTask ShowScreen(bool instant = false)
            => _hasScreen ? loadingScreen.Show(_screensLayer, instant) : UniTask.CompletedTask;

        /// <summary>
        /// Скрывает экран загрузки, если он назначен; иначе — no-op
        /// (возвращает <see cref="UniTask.CompletedTask"/>).
        /// </summary>
        private UniTask HideScreen()
            => _hasScreen ? loadingScreen.Hide() : UniTask.CompletedTask;

        /// <summary>Показывает закрываемый скрин ошибки (если назначен). Доступно на любом этапе.</summary>
        public void ShowError(string message)
        {
            if (_hasErrorScreen) errorScreen.Show(message);
            else Debug.LogError($"[APP] ShowError без ErrorScreen: {message}");
        }

        /// <summary>Показывает терминальный критический скрин (reboot/quit). Доступно на любом этапе.</summary>
        public void ShowCritical(string message, bool canReboot = true)
        {
            if (_hasCriticalScreen) criticalScreen.Show(message, canReboot, RestartBoot, null);
            else ShowBootErrorOverlay(message);
        }

        
        /// <summary>
        /// Принудительно удерживает экран загрузки поднятым независимо от навигации.
        /// </summary>
        /// <remarks>
        /// Пока включено — экран не скрывается по завершении переходов между страницами.
        /// Если экран загрузки не назначен (см. <see cref="_hasScreen"/>) — метод лишь
        /// запоминает флаг и ничего не показывает.
        /// </remarks>
        /// <param name="isEnabled"><c>true</c> — удерживать экран; <c>false</c> — разрешить его скрытие.</param>
        public void SetForceScreen(bool isEnabled)
        {
            _isForceScreen = isEnabled;

            // Экран опционален: держать/скрывать нечего.
            if (!_hasScreen) return;

            if (isEnabled)
            {
                if (!loadingScreen.gameObject.activeSelf)
                    loadingScreen.Show(_screensLayer).Forget(Debug.LogException);
            }
            else
            {
                if (loadingScreen.gameObject.activeSelf && !_isChangingPage)
                    loadingScreen.Hide().SuppressCancellationThrow().Forget();
            }
        }

        private bool _hasErrorScreen;

        private bool _hasCriticalScreen;
    }
}
