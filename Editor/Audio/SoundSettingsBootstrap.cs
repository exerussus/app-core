using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Exerussus.AppCore.Audio.Editor
{
    /// <summary>
    /// Ассет настроек звуков появляется сам (в Assets/App/Settings, если AppCore развёрнут) и лежит в
    /// Preloaded Assets — иначе сборка не увидит его до первой сцены. Ключи <c>AppSounds.cs</c>
    /// догоняют каталог при каждой загрузке домена (пишутся только при изменении).
    /// </summary>
    [InitializeOnLoad]
    internal static class SoundSettingsBootstrap
    {
        static SoundSettingsBootstrap()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                SoundSettings settings = Ensure(false);
                if (settings != null) SoundCatalog.Generate(SoundCatalog.Sorted(settings.Entries));
            };
        }

        internal static SoundSettings Ensure(bool force)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode && !force) return null;
            SoundSettings settings = SoundCatalog.Ensure(force);
            if (settings != null) EnsurePreloaded(settings);
            return settings;
        }

        /// <summary>Ассет — в Preloaded Assets текущего контекста Player Settings. true — список изменён.</summary>
        internal static bool EnsurePreloaded(SoundSettings settings)
        {
            UnityEngine.Object[] current = PlayerSettings.GetPreloadedAssets();
            var list = new List<UnityEngine.Object>(current.Length + 1);
            var present = false;
            var dirty = false;

            foreach (UnityEngine.Object asset in current)
            {
                if (asset == null)
                {
                    dirty = true;
                    continue;
                }

                if (asset == settings) present = true;
                list.Add(asset);
            }

            if (!present)
            {
                list.Add(settings);
                dirty = true;
            }

            if (!dirty) return false;

            PlayerSettings.SetPreloadedAssets(list.ToArray());
            AssetDatabase.SaveAssets();
            return true;
        }
    }

    /// <summary>Перед сборкой: настройки — в Preloaded Assets собираемого профиля, каталог проверен.</summary>
    internal sealed class SoundSettingsBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            SoundSettings settings = SoundCatalog.Load();
            if (settings == null) return;

            if (SoundSettingsBootstrap.EnsurePreloaded(settings))
                Debug.Log("[AppSound] Настройки звуков добавлены в Preloaded Assets собираемого профиля — закоммитьте профиль.");

            if (!SoundCatalog.IsSorted(settings.Entries))
                throw new BuildFailedException("[AppSound] Каталог звуков не отсортирован — номера AppSounds не совпадут. " +
                                               "Откройте Nexus → App → Звуки (или Exerussus/App/Sounds/Синхронизировать).");

            foreach (SoundCatalog.Issue issue in SoundCatalog.Validate(settings.Entries))
                if (issue.Level == SoundCatalog.IssueLevel.Error)
                    throw new BuildFailedException($"[AppSound] {issue.Key}: {issue.Text}");
        }
    }

    /// <summary>
    /// Импорт ассета настроек (правка в инспекторе, git pull) → порядок и ключи догоняют каталог;
    /// новые клипы в <see cref="SoundSettings.ClipsRoot"/> получают настройки импорта под UI-звук.
    /// </summary>
    internal sealed class SoundAssetsPostprocessor : AssetPostprocessor
    {
        private static bool _queued;

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (Array.IndexOf(imported, SoundSettings.AssetPath) < 0) return;
            SoundSettings.ResetLookup();
            if (_queued) return;
            _queued = true;

            // правки ассета во время импорта запрещены — после
            EditorApplication.delayCall += () =>
            {
                _queued = false;
                SoundSettings settings = SoundCatalog.Load();
                if (settings == null) return;

                if (!SoundCatalog.IsSorted(settings.Entries))
                {
                    SoundCatalog.Commit(settings, settings.Entries, "Сортировка каталога звуков");
                    return;
                }

                SoundCatalog.Generate(settings.Entries);
            };
        }

        private void OnPreprocessAudio()
        {
            if (!assetPath.StartsWith(SoundSettings.ClipsRoot + "/", StringComparison.Ordinal)) return;
            var importer = (AudioImporter)assetImporter;
            if (!importer.importSettingsMissing) return; // ручные правки не перетираем

            importer.forceToMono = true;
            importer.loadInBackground = false;

            AudioImporterSampleSettings s = importer.defaultSampleSettings;
            s.loadType = AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = 0.5f;
            s.preloadAudioData = true;
            importer.defaultSampleSettings = s;
        }
    }
}
