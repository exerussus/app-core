using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Очередь пакетов файлами в <c>persistentDataPath/AppAnalytics/Outbox</c>: один пакет — один файл.
    /// Десктоп и Android. На WebGL не используется (там всегда лёгкий режим, очередь в памяти).
    /// Ошибка записи или удаления пробрасывается: модуль после первой ошибки работает без диска.
    /// </summary>
    /// <remarks>
    /// Хранилище, которое создаёт сам модуль, пишет в фоне: запись и удаление идут одной очередью по порядку
    /// вызовов в пуле потоков, ошибка фоновой операции пробрасывается следующим вызовом <see cref="Save"/> /
    /// <see cref="Delete"/>. На выходе и при остановке модуль дожидается очереди (<see cref="WaitIdle"/>).
    /// Созданное проектом через публичный конструктор — синхронное, как раньше.
    /// </remarks>
    public sealed class FileAnalyticsStore : IAnalyticsStore
    {
        private readonly string _dir;
        private readonly bool _background;
        private readonly object _lock = new();
        private Task _tail = Task.CompletedTask;
        private Exception _error;

        public FileAnalyticsStore() : this(DefaultDirectory()) { }

        public FileAnalyticsStore(string directory) => _dir = directory;

        /// <summary>Хранилище модуля: <paramref name="background"/> — запись в фоне (не на WebGL: там нет потоков).</summary>
        internal FileAnalyticsStore(string directory, bool background)
        {
            _dir = directory;
            _background = background && Application.platform != RuntimePlatform.WebGLPlayer;
        }

        internal static string DefaultDirectory() => Path.Combine(Application.persistentDataPath, "AppAnalytics", "Outbox");

        public string Directory => _dir;

        public void Save(string packetId, string content)
        {
            var path = PathOf(packetId);
            if (!_background)
            {
                Write(_dir, path, content);
                return;
            }

            var dir = _dir;
            Enqueue(() => Write(dir, path, content));
        }

        public void Delete(string packetId)
        {
            var path = PathOf(packetId);
            if (!_background)
            {
                Remove(path);
                return;
            }

            Enqueue(() => Remove(path));
        }

        /// <summary>
        /// Дождаться фоновой очереди записи (выход, остановка модуля). False — не успела за
        /// <paramref name="timeoutMs"/>. Ошибка фоновой операции пробрасывается.
        /// </summary>
        public bool WaitIdle(int timeoutMs = Timeout.Infinite)
        {
            if (!_background) return true;

            Task tail;
            lock (_lock) tail = _tail;
            var done = tail.Wait(timeoutMs);
            ThrowIfFailed();
            return done;
        }

        private void Enqueue(Action operation)
        {
            ThrowIfFailed();
            lock (_lock)
            {
                // одна цепочка: операции идут строго по порядку вызовов (удаление не обгонит запись)
                _tail = _tail.ContinueWith(_ =>
                {
                    if (Volatile.Read(ref _error) != null) return;
                    try
                    {
                        operation();
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref _error, e, null);
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        private void ThrowIfFailed()
        {
            var error = Volatile.Read(ref _error);
            if (error != null) throw new IOException($"Фоновая запись очереди аналитики не удалась: {error.Message}", error);
        }

        private static void Write(string dir, string path, string content)
        {
            System.IO.Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            File.WriteAllText(temp, content);

            // без окна потери: прежний файл заменяется атомарно, а не удаляется перед переносом
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            try
            {
                File.Replace(temp, path, null);
            }
            catch (Exception e) when (e is PlatformNotSupportedException || e is IOException)
            {
                // файловая система без атомарной замены — запасной путь с коротким окном
                File.Delete(path);
                File.Move(temp, path);
            }
        }

        private static void Remove(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public IEnumerable<KeyValuePair<string, string>> LoadAll() => LoadNewest(int.MaxValue, long.MaxValue, out _);

        /// <summary>
        /// Пакеты прошлых запусков в порядке создания, не больше лимитов: лимиты применяются по метаданным
        /// (число файлов, размер на диске), читается содержимое только тех, что остаются. Остальные — самые
        /// старые — удаляются, их число в <paramref name="dropped"/>.
        /// </summary>
        internal List<KeyValuePair<string, string>> LoadNewest(int maxPackets, long maxBytes, out int dropped)
        {
            dropped = 0;
            WaitIdleQuietly();

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

                // время создания на Android/Linux ненадёжно (часто = изменению или не хранится): порядок — по времени
                // записи, при равенстве — по имени (номер пакета внутри сессии с ведущими нулями)
                var files = new List<FileInfo>(dir.GetFiles("*.jsonl"));
                files.Sort(CompareByAge);

                // с самых новых: сколько уместится в лимиты — без чтения содержимого
                var keepFrom = files.Count;
                long bytes = 0;
                for (int i = files.Count - 1; i >= 0; i--)
                {
                    var length = files[i].Length;
                    if (files.Count - i > maxPackets || bytes + length > maxBytes) break;
                    bytes += length;
                    keepFrom = i;
                }

                for (int i = 0; i < keepFrom; i++)
                {
                    dropped++;
                    try
                    {
                        files[i].Delete();
                    }
                    catch
                    {
                        // не удалился — останется за лимитом и в следующий запуск
                    }
                }

                for (int i = keepFrom; i < files.Count; i++)
                    result.Add(new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(files[i].Name), File.ReadAllText(files[i].FullName)));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppAnalytics] Не удалось прочитать очередь {_dir}: {e.Message}");
            }

            return result;
        }

        private void WaitIdleQuietly()
        {
            try
            {
                WaitIdle(5000);
            }
            catch
            {
                // ошибка фоновой записи всплывёт на следующем Save/Delete
            }
        }

        private static int CompareByAge(FileInfo a, FileInfo b)
        {
            var byTime = a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Name, b.Name);
        }

        private string PathOf(string packetId) => Path.Combine(_dir, (packetId ?? "null") + ".jsonl");
    }
}
