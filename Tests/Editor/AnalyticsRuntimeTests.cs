using System;
using System.Collections.Generic;
using Exerussus.AppCore.Analytics;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// Аналитика в рантайме: умолчания, пределы настроек, выключенный модуль без записей, хранилище и адаптер
    /// проекта с отказами, срочный сброс мимо пауз, лимиты лёгкой очереди, ноль аллокаций.
    /// </summary>
    public sealed class AnalyticsRuntimeTests
    {
        private const string PrefsCollect = "AppAnalytics.Collect";
        private const string PrefsProfile = "AppAnalytics.Profile";

        private string _savedProfile;
        private int _savedCollect;
        private bool _hadProfile, _hadCollect;
        private AnalyticsSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _hadProfile = PlayerPrefs.HasKey(PrefsProfile);
            _hadCollect = PlayerPrefs.HasKey(PrefsCollect);
            _savedProfile = PlayerPrefs.GetString(PrefsProfile, string.Empty);
            _savedCollect = PlayerPrefs.GetInt(PrefsCollect, 0);
            // тесты не зависят от выбора, оставленного предыдущим тестом или разработчиком
            PlayerPrefs.DeleteKey(PrefsCollect);
            PlayerPrefs.DeleteKey(PrefsProfile);
            AppAnalytics.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            AppAnalytics.ResetForTests();
            if (_settings != null) UnityEngine.Object.DestroyImmediate(_settings);
            _settings = null;

            if (_hadProfile) PlayerPrefs.SetString(PrefsProfile, _savedProfile);
            else PlayerPrefs.DeleteKey(PrefsProfile);
            if (_hadCollect) PlayerPrefs.SetInt(PrefsCollect, _savedCollect);
            else PlayerPrefs.DeleteKey(PrefsCollect);
            PlayerPrefs.Save();
        }

        // ---------------------------------------------------------------- помощники

        private AnalyticsSettings Settings(Action<SerializedObject> edit = null)
        {
            _settings = ScriptableObject.CreateInstance<AnalyticsSettings>();
            var so = new SerializedObject(_settings);
            so.FindProperty("enabled").boolValue = true;
            so.FindProperty("collectByDefault").boolValue = true;
            so.FindProperty("disableInBatchMode").boolValue = false;
            so.FindProperty("exportToFilesByDefault").boolValue = false;
            so.FindProperty("leanMode").enumValueIndex = (int)AnalyticsLeanMode.Always;
            var entries = so.FindProperty("entries");
            entries.arraySize = 1;
            entries.GetArrayElementAtIndex(0).FindPropertyRelative("key").stringValue = "Test.Hit";
            edit?.Invoke(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            return _settings;
        }

        private static void Raise(int n = 1)
        {
            for (var i = 0; i < n; i++)
            {
                var w = AppAnalytics.Begin(0);
                if (w.Active) w.Field("v", i).End();
            }
        }

        private sealed class Recorder : IAnalyticsAdapter
        {
            public readonly List<AnalyticsPacket> Delivered = new List<AnalyticsPacket>();
            public readonly List<AnalyticsFlushReason> Flushes = new List<AnalyticsFlushReason>();
            public bool Ack;
            public bool ThrowOnDeliver, ThrowOnFlush;
            public int DeliveredBeforeFlush = -1;

            public void Deliver(AnalyticsPacket packet)
            {
                Delivered.Add(packet);
                if (ThrowOnDeliver) throw new InvalidOperationException("deliver");
                if (Ack) AppAnalytics.Complete(packet.Id);
            }

            public void OnFlush(AnalyticsFlushReason reason)
            {
                Flushes.Add(reason);
                if (DeliveredBeforeFlush < 0) DeliveredBeforeFlush = Delivered.Count;
                if (ThrowOnFlush) throw new InvalidOperationException("flush");
            }
        }

        private sealed class NullStore : IAnalyticsStore
        {
            public void Save(string id, string content) { }
            public void Delete(string id) { }
            public IEnumerable<KeyValuePair<string, string>> LoadAll() => null;
        }

        private sealed class ThrowingStore : IAnalyticsStore
        {
            public void Save(string id, string content) => throw new System.IO.IOException("disk full");
            public void Delete(string id) => throw new System.IO.IOException("disk full");

            public IEnumerable<KeyValuePair<string, string>> LoadAll()
            {
                yield return new KeyValuePair<string, string>("a", "x");
                throw new System.IO.IOException("broken");
            }
        }

        /// <summary>Аллокации действия: после прогрева, за вычетом накладных расходов самого замера.</summary>
        private static long Allocated(Action action)
        {
            action(); // прогрев: JIT, ленивые буферы
            Action empty = () => { };
            empty();
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            empty();
            var e0 = GC.GetAllocatedBytesForCurrentThread();
            var b1 = GC.GetAllocatedBytesForCurrentThread();
            action();
            var e1 = GC.GetAllocatedBytesForCurrentThread();
            return Math.Max(0, (e1 - b1) - (e0 - b0));
        }

        private static void RunsOff(int n)
        {
            for (var i = 0; i < n; i++)
            {
                AppAnalytics.BeginRun("x");
                Raise(5);
                AppAnalytics.EndRun("y");
            }
        }

        // ---------------------------------------------------------------- умолчания и пределы

        [Test]
        public void NewSettings_AreOffByDefault()
        {
            _settings = ScriptableObject.CreateInstance<AnalyticsSettings>();
            Assert.IsFalse(_settings.Enabled);
            Assert.IsFalse(_settings.CollectByDefault);
        }

        [Test]
        public void Settings_AreClamped_ForAnyYamlValue()
        {
            var s = Settings(so =>
            {
                so.FindProperty("maxPacketKilobytes").intValue = int.MaxValue;
                so.FindProperty("maxQueuedKilobytes").intValue = int.MaxValue;
                so.FindProperty("leanMaxQueuedKilobytes").intValue = int.MinValue;
                so.FindProperty("leanMaxQueuedPackets").intValue = 0;
                so.FindProperty("maxInFlight").intValue = 1000;
                so.FindProperty("maxPacketAge").floatValue = float.NaN;
                so.FindProperty("retryMax").floatValue = float.PositiveInfinity;
                so.FindProperty("maxEventsPerPacket").intValue = -5;
            });

            Assert.AreEqual(AnalyticsSettings.MaxPacketKilobytesCeiling * 512, s.MaxPacketChars);
            Assert.AreEqual(AnalyticsSettings.MaxQueuedKilobytesCeiling * 1024L, s.MaxQueuedBytes);
            Assert.LessOrEqual(s.LeanMaxQueuedBytes, AnalyticsSettings.LeanMaxQueuedKilobytesCeiling * 1024L);
            Assert.GreaterOrEqual(s.LeanMaxQueuedBytes, 3L * s.MaxPacketChars * sizeof(char));
            Assert.AreEqual(16, s.MaxInFlight);
            Assert.GreaterOrEqual(s.LeanMaxQueuedPackets, Math.Min(AnalyticsSettings.LeanMaxQueuedPacketsCeiling, s.MaxInFlight + 2));
            Assert.IsFalse(float.IsNaN(s.MaxPacketAge));
            Assert.IsFalse(float.IsInfinity(s.RetryMax));
            Assert.AreEqual(1, s.MaxEventsPerPacket);
        }

        [Test]
        public void WebGL_IsAlwaysLean()
        {
            var s = Settings(so => so.FindProperty("leanMode").enumValueIndex = (int)AnalyticsLeanMode.Never);
            Assert.IsTrue(s.IsLeanOn(RuntimePlatform.WebGLPlayer));
            Assert.IsFalse(s.IsLeanOn(RuntimePlatform.WindowsPlayer));
        }

        // ---------------------------------------------------------------- выключенный модуль

        [Test]
        public void NotStarted_WritesNoPlayerPrefs_AndThrowsNothing()
        {
            PlayerPrefs.DeleteKey(PrefsCollect);
            PlayerPrefs.DeleteKey(PrefsProfile);

            Assert.DoesNotThrow(() =>
            {
                for (var i = 0; i < 100; i++) AppAnalytics.SetCollecting(i % 2 == 0);
                AppAnalytics.NewProfile();
                AppAnalytics.SetProfileId("user-42");
                AppAnalytics.SetProperty("ab", "B");
                AppAnalytics.BeginRun("level");
                Raise(10);
                AppAnalytics.EndRun("win");
                AppAnalytics.Flush();
                AppAnalytics.RetryNow();
                AppAnalytics.Complete(null);
                AppAnalytics.Fail(null);
            });

            Assert.IsFalse(PlayerPrefs.HasKey(PrefsCollect));
            Assert.IsFalse(PlayerPrefs.HasKey(PrefsProfile));
            Assert.IsFalse(AppAnalytics.IsAvailable);
        }

        [Test]
        public void NotStarted_RaiseDoesNotAllocate()
        {
            Assert.AreEqual(0, Allocated(() => Raise(10000)));
        }

        [Test]
        public void CollectChoice_BeforeStart_IsAppliedAtStart()
        {
            AppAnalytics.SetCollecting(false);
            AppAnalytics.SetProfileId("user-7");
            AppAnalytics.StartForTests(Settings());
            Assert.IsTrue(AppAnalytics.IsAvailable);
            Assert.IsFalse(AppAnalytics.CollectEnabled);
            Assert.AreEqual("user-7", AppAnalytics.ProfileId);
        }

        // ---------------------------------------------------------------- хранилище и адаптер проекта

        [Test]
        public void StoreReturningNull_DoesNotBreakStart()
        {
            AppAnalytics.SetStore(new NullStore());
            var s = Settings(so => so.FindProperty("leanMode").enumValueIndex = (int)AnalyticsLeanMode.Never);
            Assert.DoesNotThrow(() => AppAnalytics.StartForTests(s));
            Assert.IsTrue(AppAnalytics.IsAvailable);
        }

        [Test]
        public void StoreThrowing_IsDropped_ModuleKeepsWorking()
        {
            AppAnalytics.SetStore(new ThrowingStore());
            var s = Settings(so => so.FindProperty("leanMode").enumValueIndex = (int)AnalyticsLeanMode.Never);
            LogAssert.ignoreFailingMessages = true;
            Assert.DoesNotThrow(() => AppAnalytics.StartForTests(s));
            var recorder = new Recorder { Ack = true };
            AppAnalytics.SetAdapter(recorder);
            Raise(3);
            Assert.DoesNotThrow(() => AppAnalytics.Flush(AnalyticsFlushReason.Pause));
            Assert.IsTrue(AppAnalytics.IsAvailable);
            Assert.IsFalse(AppAnalytics.IsFaultedForTests);
            Assert.Greater(recorder.Delivered.Count, 0);
        }

        [Test]
        public void ThrowingAdapter_DoesNotFaultModule()
        {
            AppAnalytics.StartForTests(Settings());
            var recorder = new Recorder { ThrowOnDeliver = true, ThrowOnFlush = true };
            AppAnalytics.SetAdapter(recorder);
            LogAssert.ignoreFailingMessages = true;
            Raise(5);
            Assert.DoesNotThrow(() => AppAnalytics.Flush(AnalyticsFlushReason.Pause));
            Assert.IsTrue(AppAnalytics.IsAvailable);
            Assert.Greater(recorder.Delivered.Count, 0);
        }

        [Test]
        public void ThrowingGameClock_StillWritesEvents()
        {
            AppAnalytics.StartForTests(Settings());
            var recorder = new Recorder { Ack = true };
            AppAnalytics.SetAdapter(recorder);
            AppAnalytics.SetGameClock(() => throw new InvalidOperationException("clock"));
            Raise(2);
            AppAnalytics.Flush(AnalyticsFlushReason.Pause);
            Assert.IsTrue(AppAnalytics.IsAvailable);
            StringAssert.Contains("\"g\":null", recorder.Delivered[recorder.Delivered.Count - 1].Content);
        }

        // ---------------------------------------------------------------- срочный сброс и лимиты

        [Test]
        public void PauseFlush_DeliversBeforeOnFlush_BypassingInFlightLimit()
        {
            var s = Settings(so =>
            {
                so.FindProperty("maxInFlight").intValue = 1;
                so.FindProperty("maxEventsPerPacket").intValue = 2;
            });
            AppAnalytics.StartForTests(s);
            var recorder = new Recorder(); // не отвечает — пакеты висят у адаптера
            AppAnalytics.SetAdapter(recorder);
            Raise(6); // три пакета

            AppAnalytics.Flush(AnalyticsFlushReason.Pause);

            Assert.GreaterOrEqual(recorder.Delivered.Count, 3, "все ждущие пакеты — адаптеру, мимо maxInFlight и паузы старта");
            Assert.AreEqual(recorder.Delivered.Count, recorder.DeliveredBeforeFlush, "пакеты — до OnFlush");
        }

        [Test]
        public void LeanQueue_InFlightPackets_DoNotEvictNewOnes()
        {
            var s = Settings(so =>
            {
                so.FindProperty("maxInFlight").intValue = 4;
                so.FindProperty("leanMaxQueuedPackets").intValue = 1;
                so.FindProperty("maxEventsPerPacket").intValue = 1;
            });
            AppAnalytics.StartForTests(s);
            var recorder = new Recorder();
            AppAnalytics.SetAdapter(recorder);

            Raise(4);
            AppAnalytics.Flush(AnalyticsFlushReason.Pause); // 4 пакета у адаптера, без ответа
            Raise(2);                                         // новые — ждут, а не вытесняются сразу
            Assert.GreaterOrEqual(AppAnalytics.QueuedPackets, 6);
        }

        [Test]
        public void RetryNow_SendsFailedPacketsWithoutBackoff()
        {
            AppAnalytics.StartForTests(Settings());
            var recorder = new Recorder();
            AppAnalytics.SetAdapter(recorder);
            Raise(1);
            AppAnalytics.Flush(AnalyticsFlushReason.Pause);
            var first = recorder.Delivered[0];
            AppAnalytics.Fail(first.Id, 3600);

            recorder.Ack = true;
            AppAnalytics.RetryNow();
            Assert.AreEqual(0, AppAnalytics.QueuedPackets);
        }

        [Test]
        public void UrgentFlushes_WithHoldingAdapter_StayUnderHardCap()
        {
            var s = Settings(so => so.FindProperty("maxEventsPerPacket").intValue = 1);
            AppAnalytics.StartForTests(s);
            AppAnalytics.SetAdapter(new Recorder()); // держит пакеты, не отвечает
            for (var i = 0; i < 500; i++)
            {
                Raise(1);
                AppAnalytics.Flush(AnalyticsFlushReason.Pause);
            }

            Assert.LessOrEqual(AppAnalytics.QueuedPackets, s.LeanMaxQueuedPackets + s.MaxInFlight);
        }

        [Test]
        public void ClockCallingIntoModule_DoesNotCorruptPackets()
        {
            AppAnalytics.StartForTests(Settings());
            var recorder = new Recorder { Ack = true };
            AppAnalytics.SetAdapter(recorder);
            AppAnalytics.SetGameClock(() =>
            {
                AppAnalytics.Flush(AnalyticsFlushReason.Manual);
                return 1.5;
            });
            Raise(3);
            AppAnalytics.SetGameClock(null);
            AppAnalytics.Flush(AnalyticsFlushReason.Pause);

            foreach (var p in recorder.Delivered)
                foreach (var line in p.Content.Split('\n'))
                    if (line.Length > 0) StringAssert.StartsWith("{", line, "каждая строка — целый объект: " + line);
            foreach (var p in recorder.Delivered)
                foreach (var line in p.Content.Split('\n'))
                    if (line.StartsWith("{\"s\":", StringComparison.Ordinal)) StringAssert.Contains("\"e\":", line);
        }

        private sealed class CompletingStore : IAnalyticsStore
        {
            public void Save(string id, string content) { }
            public void Delete(string id) => AppAnalytics.Complete(id);
            public IEnumerable<KeyValuePair<string, string>> LoadAll() => Array.Empty<KeyValuePair<string, string>>();
        }

        [Test]
        public void DiscardQueue_WithReentrantStore_DoesNotFault()
        {
            AppAnalytics.SetStore(new CompletingStore());
            var s = Settings(so =>
            {
                so.FindProperty("leanMode").enumValueIndex = (int)AnalyticsLeanMode.Never;
                so.FindProperty("discardQueueOnDisable").boolValue = true;
            });
            AppAnalytics.StartForTests(s);
            AppAnalytics.SetAdapter(new Recorder());
            Raise(3);
            AppAnalytics.Flush();
            Assert.DoesNotThrow(() => AppAnalytics.SetCollecting(false));
            Assert.IsFalse(AppAnalytics.IsFaultedForTests);
            Assert.AreEqual(0, AppAnalytics.QueuedPackets);
        }

        // ---------------------------------------------------------------- запись

        [Test]
        public void Raise_InOpenPacket_DoesNotAllocate()
        {
            var s = Settings(so => so.FindProperty("maxEventsPerPacket").intValue = 100000);
            AppAnalytics.StartForTests(s);
            AppAnalytics.SetAdapter(new Recorder { Ack = true });
            Assert.AreEqual(0, Allocated(() => Raise(200))); // буфер выделяется на первом событии — в прогреве
        }

        [Test]
        public void CollectionOff_RunsAndRaiseDoNotAllocate()
        {
            AppAnalytics.StartForTests(Settings());
            AppAnalytics.SetAdapter(new Recorder { Ack = true });
            AppAnalytics.SetCollecting(false);
            Assert.AreEqual(0, Allocated(() => RunsOff(1000)));
        }
    }
}
