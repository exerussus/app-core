using System;
using System.IO;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Дефолтная «доставка»: пакет кладётся файлом в <c>persistentDataPath/AppAnalytics/Export/&lt;дата&gt;/&lt;id&gt;.jsonl</c>
    /// и сразу подтверждается. Для десктопных тестов; отправку на сервер проект делает своим адаптером.
    /// </summary>
    public sealed class FileExportAdapter : IAnalyticsAdapter
    {
        private readonly string _root;

        public FileExportAdapter() : this(Path.Combine(Application.persistentDataPath, "AppAnalytics", "Export")) { }

        public FileExportAdapter(string root) => _root = root;

        public string Root => _root;

        public void Deliver(AnalyticsPacket packet)
        {
            try
            {
                var dir = Path.Combine(_root, DateTime.UtcNow.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, packet.Id + ".jsonl"), packet.Content);
                AppAnalytics.Complete(packet.Id);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppAnalytics] Экспорт пакета {packet.Id} не удался: {e.Message}");
                AppAnalytics.Fail(packet.Id);
            }
        }

        public void OnFlush(AnalyticsFlushReason reason) { }
    }
}
