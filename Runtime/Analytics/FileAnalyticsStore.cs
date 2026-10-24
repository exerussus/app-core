using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Очередь пакетов файлами в <c>persistentDataPath/AppAnalytics/Outbox</c>: один пакет — один файл.
    /// Работает на десктопе, Android и WebGL (там persistentDataPath живёт в IndexedDB).
    /// </summary>
    public sealed class FileAnalyticsStore : IAnalyticsStore
    {
        private readonly string _dir;

        public FileAnalyticsStore() : this(Path.Combine(Application.persistentDataPath, "AppAnalytics", "Outbox")) { }

        public FileAnalyticsStore(string directory) => _dir = directory;

        public string Directory => _dir;

        public void Save(string packetId, string content)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                var path = PathOf(packetId);
                var temp = path + ".tmp";
                File.WriteAllText(temp, content);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppAnalytics] Не удалось сохранить пакет {packetId} в очередь: {e.Message}");
            }
        }

        public void Delete(string packetId)
        {
            try
            {
                var path = PathOf(packetId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppAnalytics] Не удалось удалить пакет {packetId} из очереди: {e.Message}");
            }
        }

        public IEnumerable<KeyValuePair<string, string>> LoadAll()
        {
            var result = new List<KeyValuePair<string, string>>();
            if (!System.IO.Directory.Exists(_dir)) return result;

            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(_dir).GetFiles("*.jsonl"));
                files.Sort((a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
                foreach (var file in files)
                    result.Add(new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(file.Name), File.ReadAllText(file.FullName)));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppAnalytics] Не удалось прочитать очередь {_dir}: {e.Message}");
            }

            return result;
        }

        private string PathOf(string packetId) => Path.Combine(_dir, packetId + ".jsonl");
    }
}
