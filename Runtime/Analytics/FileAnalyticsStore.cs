using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Очередь пакетов файлами в <c>persistentDataPath/AppAnalytics/Outbox</c>: один пакет — один файл.
    /// Десктоп и Android. На WebGL не используется (там всегда лёгкий режим, очередь в памяти).
    /// Ошибка записи или удаления пробрасывается: модуль после первой ошибки работает без диска.
    /// </summary>
    public sealed class FileAnalyticsStore : IAnalyticsStore
    {
        private readonly string _dir;

        public FileAnalyticsStore() : this(Path.Combine(Application.persistentDataPath, "AppAnalytics", "Outbox")) { }

        public FileAnalyticsStore(string directory) => _dir = directory;

        public string Directory => _dir;

        public void Save(string packetId, string content)
        {
            System.IO.Directory.CreateDirectory(_dir);
            var path = PathOf(packetId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public void Delete(string packetId)
        {
            var path = PathOf(packetId);
            if (File.Exists(path)) File.Delete(path);
        }

        public IEnumerable<KeyValuePair<string, string>> LoadAll()
        {
            var result = new List<KeyValuePair<string, string>>();
            if (!System.IO.Directory.Exists(_dir)) return result;

            try
            {
                var dir = new DirectoryInfo(_dir);

                // недописанные копии от падения посреди Save
                foreach (var temp in dir.GetFiles("*.tmp"))
                {
                    try
                    {
                        temp.Delete();
                    }
                    catch
                    {
                        // занят — удалим в следующий запуск
                    }
                }

                var files = new List<FileInfo>(dir.GetFiles("*.jsonl"));
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

        private string PathOf(string packetId) => Path.Combine(_dir, (packetId ?? "null") + ".jsonl");
    }
}
