using System.Collections.Generic;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Очередь недоставленных пакетов, переживающая перезапуск. Пакет сохраняется сюда до выдачи адаптеру
    /// и удаляется после подтверждения — так закрытое приложение не теряет данные.
    /// Дефолт — <see cref="FileAnalyticsStore"/>.
    /// </summary>
    public interface IAnalyticsStore
    {
        void Save(string packetId, string content);
        void Delete(string packetId);

        /// <summary>Все сохранённые пакеты (id, содержимое) — при старте, в порядке создания.</summary>
        IEnumerable<KeyValuePair<string, string>> LoadAll();
    }
}
