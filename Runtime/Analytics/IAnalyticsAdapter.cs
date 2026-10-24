namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Доставка пакетов аналитики — пишется проектом (сервер, файл, SDK). Ядро зовёт только на главном потоке.
    /// </summary>
    /// <remarks>
    /// <para>Контракт: на каждый <see cref="Deliver"/> — ровно один ответ <c>AppAnalytics.Complete(id)</c>
    /// (доставлен — удалить из очереди) или <c>AppAnalytics.Fail(id, retryAfter)</c> (повторить позже).
    /// Ответ можно дать сразу или позже (после сетевого запроса). Без ответа дольше
    /// <see cref="AnalyticsSettings.DeliveryLease"/> пакет вернётся в очередь сам.</para>
    /// <para>Пакет может прийти повторно — дедупликация по <see cref="AnalyticsPacket.Id"/> на стороне получателя.</para>
    /// <para>Нужно и сохранить, и отправить — это один адаптер с двумя шагами, а не два адаптера.</para>
    /// <para><b>WebGL.</b> С «Exception Support: Explicitly Thrown Exceptions Only» catch ловит только явный
    /// <c>throw</c>: обращение к null или выход за границы массива в <see cref="Deliver"/>/<see cref="OnFlush"/>
    /// роняет страницу, и защита модуля этого не перехватит. Адаптер обязан быть безопасным сам: проверять
    /// соединение и ссылки перед использованием, ничего не индексировать без проверки длины. То же — игровые
    /// часы (<c>SetGameClock</c>) и <see cref="IAnalyticsStore"/>.</para>
    /// <para>Пример безопасного адаптера:</para>
    /// <code>
    /// sealed class SocketAdapter : IAnalyticsAdapter
    /// {
    ///     public void Deliver(AnalyticsPacket p)
    ///     {
    ///         var link = Link;                       // может отсутствовать — проверить, а не ловить
    ///         if (link == null || !link.IsConnected) { AppAnalytics.Fail(p.Id, 10); return; }
    ///         link.Send(p.Id, p.Content, ok => { if (ok) AppAnalytics.Complete(p.Id); else AppAnalytics.Fail(p.Id); });
    ///     }
    ///     public void OnFlush(AnalyticsFlushReason reason) { }
    /// }
    /// // соединение появилось — отправить очередь сейчас, без пауз повторов:
    /// AppAnalytics.RetryNow();
    /// </code>
    /// </remarks>
    public interface IAnalyticsAdapter
    {
        void Deliver(AnalyticsPacket packet);

        /// <summary>
        /// Последний шанс: пакет закрыт, приложение, возможно, сейчас уйдёт. Например, WebGL на
        /// <see cref="AnalyticsFlushReason.Pause"/> может отправить через <c>navigator.sendBeacon</c>.
        /// </summary>
        void OnFlush(AnalyticsFlushReason reason);
    }
}
