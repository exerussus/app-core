using System;

namespace Exerussus.AppCore.Boot
{
    /// <summary>
    /// Штатная остановка бута: сервис сообщает, что дальше идти незачем — приложения не будет.
    /// </summary>
    /// <remarks>
    /// В отличие от обычного исключения (оно уводит машину в <c>BootState.Failed</c>
    /// с показом <c>CriticalScreen</c> от ЯДРА), это НЕ ошибка загрузки: инициатор
    /// уже показал пользователю свой скрин (регион-лок, гейт, QR), и никакой дополнительной
    /// ошибки поверх показывать не нужно.
    /// <para>
    /// AppRunner ловит его в <c>RunAsyncStepInternal</c> / <c>StartInitializeCoreStep</c>
    /// и переводит машину в терминальный <c>BootState.Halted</c>: остальные сервисы
    /// не инициализируются, стартовая навигация не выполняется, аварийный оверлей не рисуется.
    /// </para>
    /// <para>
    /// Бросать имеет смысл только ПОСЛЕ регистрации страниц/попапов (стейт InitializingCore
    /// и позже), когда инициатор уже может показать свой скрин.
    /// </para>
    /// </remarks>
    public class BootHaltException : Exception
    {
        public BootHaltException(string reason) : base(reason) { }
    }
}
