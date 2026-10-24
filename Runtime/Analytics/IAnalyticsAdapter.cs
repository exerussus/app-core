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
