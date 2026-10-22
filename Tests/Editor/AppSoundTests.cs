using System;
using Exerussus.AppCore.Audio;
using Exerussus.AppCore.Audio.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Tests
{
    /// <summary>Звуки: запекание, режимы, кулдаун, без повтора, ноль аллокаций в Play, ключи и генерация.</summary>
    public sealed class AppSoundTests
    {
        private SoundSettings _settings;
        private AudioClip[] _clips;

        // индексы в отсортированном каталоге
        private const int Click = 0;      // Defaults.Click — дефолт, 2 клипа
        private const int Silent = 1;     // Defaults.Silent — дефолт выключен
        private const int Back = 2;       // UI.Back — через дефолт Click
        private const int Mute = 3;       // UI.Mute — молчит
        private const int Tap = 4;        // UI.Tap — свои 3 клипа
        private const int ViaSilent = 5;  // UI.ViaSilent — через выключенный дефолт

        [SetUp]
        public void SetUp()
        {
            _clips = new AudioClip[5];
            for (int i = 0; i < _clips.Length; i++) _clips[i] = AudioClip.Create("c" + i, 4410, 1, 44100, false);

            _settings = ScriptableObject.CreateInstance<SoundSettings>();
            var so = new SerializedObject(_settings);
            so.FindProperty("enabled").boolValue = true;
            so.FindProperty("disableInBatchMode").boolValue = false;
            so.FindProperty("voices").intValue = 4;
            so.ApplyModifiedPropertiesWithoutUndo();

            SoundEntry click = SoundEntry.Create("Defaults.Click", true);
            click.volume = 0.5f;
            click.clips = new[] { SoundClip.Of(_clips[0]), SoundClip.Of(_clips[1]) };

            SoundEntry silent = SoundEntry.Create("Defaults.Silent", true);
            silent.mode = SoundMode.Off;
            silent.clips = new[] { SoundClip.Of(_clips[2]) };

            SoundEntry back = SoundEntry.Create("UI.Back");
            back.mode = SoundMode.Default;
            back.defaultKey = "Defaults.Click";
            back.volume = 0.8f;

            SoundEntry mute = SoundEntry.Create("UI.Mute");
            mute.mode = SoundMode.Off;

            SoundEntry tap = SoundEntry.Create("UI.Tap");
            tap.mode = SoundMode.Custom;
            tap.cooldown = 0.1f;
            tap.clips = new[] { SoundClip.Of(_clips[2]), SoundClip.Of(_clips[3]), SoundClip.Of(_clips[4]) };

            SoundEntry viaSilent = SoundEntry.Create("UI.ViaSilent");
            viaSilent.mode = SoundMode.Default;
            viaSilent.defaultKey = "Defaults.Silent";

            _settings.SetEntries(SoundCatalog.Sorted(new[] { tap, back, viaSilent, click, mute, silent }));
            AppSound.StartWith(_settings);
            AppSound.SetTime(1f);
        }

        [TearDown]
        public void TearDown()
        {
            AppSound.Stop();
            UnityEngine.Object.DestroyImmediate(_settings);
            foreach (AudioClip clip in _clips) UnityEngine.Object.DestroyImmediate(clip);
        }

        [Test]
        public void Bake_ResolvesModes()
        {
            Assert.IsTrue(AppSound.Enabled);
            Assert.AreEqual(6, AppSound.Count);
            Assert.AreEqual(2, AppSound.ClipCountOf(Click));
            Assert.AreEqual(0, AppSound.ClipCountOf(Silent));
            Assert.AreEqual(2, AppSound.ClipCountOf(Back), "событие через дефолт берёт клипы дефолта");
            Assert.AreEqual(0.4f, AppSound.VolumeOf(Back), 1e-5f, "громкость события × громкость дефолта");
            Assert.AreEqual(0, AppSound.ClipCountOf(Mute));
            Assert.AreEqual(3, AppSound.ClipCountOf(Tap));
            Assert.AreEqual(0, AppSound.ClipCountOf(ViaSilent), "выключенный дефолт — тишина");
            Assert.AreEqual(4, AppSound.VoiceCount);
        }

        [Test]
        public void Get_FindsByKey_AndEmptyHandleIsSilent()
        {
            Assert.AreEqual(Tap, AppSound.Get("UI.Tap").Id);
            Assert.AreEqual(Back, AppSound.IndexOf("UI.Back"));
            Assert.AreEqual(-1, AppSound.IndexOf("UI.Nope"));
            Assert.IsFalse(default(SoundHandle).IsValid);
            Assert.DoesNotThrow(() => default(SoundHandle).Play());
            Assert.DoesNotThrow(() => AppSound.Play(-1));
            Assert.DoesNotThrow(() => AppSound.Play(int.MaxValue));
        }

        [Test]
        public void Cooldown_BlocksRepeatWithinWindow()
        {
            AppSound.Play(Tap);
            int first = AppSound.LastClipOf(Tap);
            AppSound.SetTime(1.05f);
            AppSound.Play(Tap); // кулдаун 0.1 — не играет
            Assert.AreEqual(first, AppSound.LastClipOf(Tap));
            AppSound.SetTime(1.2f);
            AppSound.Play(Tap);
            Assert.AreNotEqual(first, AppSound.LastClipOf(Tap), "после кулдауна играет, и не тот же клип");
        }

        [Test]
        public void Clips_NeverRepeatInARow()
        {
            float t = 1f;
            int last = -1;
            for (int i = 0; i < 500; i++)
            {
                t += 1f;
                AppSound.SetTime(t);
                AppSound.Play(Tap);
                int now = AppSound.LastClipOf(Tap);
                Assert.AreNotEqual(last, now);
                last = now;
            }
        }

        [Test]
        public void Play_DoesNotAllocate()
        {
            float t = 10f;
            for (int i = 0; i < 64; i++) { t += 0.2f; AppSound.SetTime(t); AppSound.Play(Tap); AppSound.Play(Back); }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                t += 0.011f;
                AppSound.SetTime(t);
                AppSound.Play(Tap);
                AppSound.Play(Back);
                AppSound.Play(Mute);
                AppSound.Play(ViaSilent);
                AppSound.Play(-5);
                AppSound.Play(999);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.AreEqual(0, after - before, "Play аллоцирует");
        }

        [Test]
        public void Disabled_CreatesNothing()
        {
            var so = new SerializedObject(_settings);
            so.FindProperty("enabled").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            AppSound.StartWith(_settings);
            Assert.IsFalse(AppSound.Enabled);
            Assert.AreEqual(0, AppSound.Count);
            Assert.AreEqual(0, AppSound.VoiceCount);
            Assert.DoesNotThrow(() => AppSound.Play(0));
        }

        [Test]
        public void Keys_Validation()
        {
            Assert.IsNull(SoundKeys.ValidateKey("UI.Effects.Click"));
            Assert.IsNotNull(SoundKeys.ValidateKey("UI.UI"));
            Assert.IsNotNull(SoundKeys.ValidateKey("UI.1Click"));
            Assert.IsNotNull(SoundKeys.ValidateKey("UI.Клик"));
            Assert.IsNotNull(SoundKeys.ValidateKey("UI.default"));
            Assert.IsNotNull(SoundKeys.ValidateKey("UI.Click_S"));
            Assert.IsNotNull(SoundKeys.ValidateKey("A.B.C.D.E.F"));
            Assert.IsNotNull(SoundKeys.ValidateKey("Count"));
            Assert.IsNull(SoundKeys.ValidateKey("A.B.C.D.E"));
            Assert.AreEqual("Assets/AppSounds/UI/Effects/Click", SoundKeys.FolderOf("UI.Effects.Click"));
            Assert.AreEqual("Click_03.wav", SoundKeys.ClipFileName("UI.Effects.Click", 3, ".WAV"));
        }

        [Test]
        public void Keys_GenerateNestedConstants()
        {
            var entries = SoundCatalog.Sorted(new[]
            {
                SoundEntry.Create("UI.Effects.Click"), SoundEntry.Create("UI.Effects"), SoundEntry.Create("Hand"),
            });
            string text = SoundKeys.Generate(entries);
            StringAssert.Contains("public const int Count = 3;", text);
            StringAssert.Contains("public const int Hand_S = 0;", text);
            StringAssert.Contains("public const int Effects_S = 1;", text);
            StringAssert.Contains("public static class Effects", text);
            StringAssert.Contains("public const int Click_S = 2;", text);
        }
    }
}
