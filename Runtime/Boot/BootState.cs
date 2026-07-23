namespace Exerussus.AppCore.Boot
{
    /// <summary>Этапы инициализации приложения.</summary>
    public enum BootState
    {
        /// <summary>Машина ещё не запущена (до <c>Awake</c>).</summary>
        NotStarted,

        /// <summary>Поднимается экран загрузки. Страницы и сервисы ещё не тронуты.</summary>
        CoveringScreen,

        /// <summary>Выполняется <see cref="AppBootstrapper.PreInitialize"/>, если бутстраппер задан.</summary>
        PreBootstrap,

        /// <summary>Синхронная регистрация сервисов в DI: контейнер, инъекции, PreInitialize. Попапов ещё нет.</summary>
        RegisteringServices,

        /// <summary>Инициализация страниц/попапов и их контроллеров. Идёт ДО асинхронной инициализации сервисов.</summary>
        InitializingCore,

        /// <summary>Асинхронная инициализация сервисов (Initialize/PostInitialize). Попапы и скрины уже доступны.</summary>
        InitializingServices,

        /// <summary>Выполняется <see cref="AppBootstrapper.PostInitialize"/>, если бутстраппер задан.</summary>
        PostBootstrap,

        /// <summary>Финальное состояние. При входе снимается экран и происходит навигация на дефолтную страницу.</summary>
        Ready,

        /// <summary>Терминальное состояние ошибки: показан <c>CriticalScreen</c> от ядра, машина остановлена.</summary>
        Failed,

        /// <summary>
        /// Терминальное состояние штатной остановки: сервис сообщил, что приложения не будет
        /// (гейт/регион-лок). Свой скрин уже показан инициатором, аварийный оверлей НЕ показываем.
        /// </summary>
        Halted
    }
}
