using System.IO;
using System.Threading;
using Exerussus.AppCore.Analytics;
using NUnit.Framework;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// Файловая очередь аналитики: замена без окна потери, порядок по времени записи и имени,
    /// лимиты по метаданным до чтения, фоновая запись по порядку вызовов.
    /// </summary>
    public sealed class AnalyticsStoreFileTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AppCoreAnalyticsStore_" + System.Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [Test]
        public void Save_over_existing_replaces_content()
        {
            var store = new FileAnalyticsStore(_dir);
            store.Save("s-00001", "old");
            store.Save("s-00001", "new");

            var all = store.LoadNewest(int.MaxValue, long.MaxValue, out var dropped);
            Assert.AreEqual(0, dropped);
            Assert.AreEqual(1, all.Count);
            Assert.AreEqual("new", all[0].Value);
            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.tmp").Length);
        }

        [Test]
        public void Same_write_time_is_ordered_by_name()
        {
            var store = new FileAnalyticsStore(_dir);
            store.Save("s-00002", "b");
            store.Save("s-00001", "a");
            store.Save("s-00003", "c");

            var time = System.DateTime.UtcNow;
            foreach (var file in Directory.GetFiles(_dir, "*.jsonl")) File.SetLastWriteTimeUtc(file, time);

            var all = store.LoadNewest(int.MaxValue, long.MaxValue, out _);
            Assert.AreEqual("s-00001", all[0].Key);
            Assert.AreEqual("s-00002", all[1].Key);
            Assert.AreEqual("s-00003", all[2].Key);
        }

        [Test]
        public void Limits_keep_newest_and_delete_oldest()
        {
            var store = new FileAnalyticsStore(_dir);
            var time = System.DateTime.UtcNow.AddMinutes(-10);
            for (var i = 1; i <= 4; i++)
            {
                var id = "s-0000" + i;
                store.Save(id, "x");
                File.SetLastWriteTimeUtc(Path.Combine(_dir, id + ".jsonl"), time.AddSeconds(i));
            }

            var kept = store.LoadNewest(2, long.MaxValue, out var dropped);
            Assert.AreEqual(2, dropped);
            Assert.AreEqual(2, kept.Count);
            Assert.AreEqual("s-00003", kept[0].Key);
            Assert.AreEqual("s-00004", kept[1].Key);
            Assert.AreEqual(2, Directory.GetFiles(_dir, "*.jsonl").Length, "лишние удалены без чтения");
        }

        [Test]
        public void Byte_limit_counts_file_size()
        {
            var store = new FileAnalyticsStore(_dir);
            var time = System.DateTime.UtcNow.AddMinutes(-10);
            store.Save("s-00001", new string('a', 100));
            File.SetLastWriteTimeUtc(Path.Combine(_dir, "s-00001.jsonl"), time);
            store.Save("s-00002", new string('b', 100));
            File.SetLastWriteTimeUtc(Path.Combine(_dir, "s-00002.jsonl"), time.AddSeconds(1));

            var kept = store.LoadNewest(int.MaxValue, 150, out var dropped);
            Assert.AreEqual(1, dropped);
            Assert.AreEqual(1, kept.Count);
            Assert.AreEqual("s-00002", kept[0].Key);
        }

        [Test]
        public void Background_store_keeps_call_order()
        {
            var store = new FileAnalyticsStore(_dir, background: true);
            store.Save("s-00001", "a");
            store.Delete("s-00001");
            store.Save("s-00002", "b");

            Assert.IsTrue(store.WaitIdle(Timeout.Infinite));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "s-00001.jsonl")), "удаление не обогнало запись");
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "s-00002.jsonl")));
        }
    }
}
