namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Пакет событий для доставки. <see cref="Content"/> — UTF-8 JSONL: первая строка — заголовок
    /// (<c>"type":"header"</c>: профиль, сессия, прогон, билд, платформа, схема), дальше по событию на строку.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> стабилен и уникален (сессия + номер): пакет может прийти дважды (доставлен, но
    /// подтверждение не успело) — получатель отбрасывает дубли по нему.
    /// </remarks>
    public sealed class AnalyticsPacket
    {
        internal AnalyticsPacket(string id, string content, int eventCount)
        {
            Id = id;
            Content = content;
            EventCount = eventCount;
        }

        /// <summary>Стабильный id пакета: <c>&lt;сессия&gt;-&lt;номер&gt;</c>.</summary>
        public string Id { get; }

        /// <summary>JSONL: заголовок + события.</summary>
        public string Content { get; }

        /// <summary>Событий в пакете; -1 — пакет поднят из очереди прошлого запуска.</summary>
        public int EventCount { get; }

        /// <summary>Сколько раз пакет уже выдавался адаптеру (включая текущую).</summary>
        public int Attempt { get; internal set; }

        internal int Bytes => Content.Length * sizeof(char);
        internal PacketState State;
        internal double LeaseUntil;
        internal double RetryAt;
    }

    internal enum PacketState : byte
    {
        Pending = 0,
        InFlight = 1,
    }
}
