namespace Exerussus.AppCore.Analytics
{
    /// <summary>Почему аналитика закрыла открытый пакет и просит адаптер поторопиться.</summary>
    public enum AnalyticsFlushReason : byte
    {
        /// <summary>Прогон (партия) закончился.</summary>
        RunEnd = 0,
        /// <summary>Приложение уходит в фон / вкладка скрыта — может не вернуться (Android, WebGL).</summary>
        Pause = 1,
        /// <summary>Приложение закрывается.</summary>
        Quit = 2,
        /// <summary>Сменился профиль.</summary>
        ProfileChanged = 3,
        /// <summary>Явный вызов <c>AppAnalytics.Flush()</c>.</summary>
        Manual = 4,
    }
}
