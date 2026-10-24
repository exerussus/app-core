using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Exerussus.AppCore.Analytics.Editor
{
    /// <summary>
    /// Ассет настроек аналитики появляется сам (в Assets/App/Settings, если AppCore развёрнут) и лежит в
    /// Preloaded Assets — иначе сборка не увидит его до первой сцены. <c>AppEvents.cs</c> догоняет каталог
    /// при каждой загрузке домена (пишется только при изменении).
    /// </summary>
    [InitializeOnLoad]
    internal static class AnalyticsBootstrap
    {
        static AnalyticsBootstrap()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                var settings = Ensure(false);
                if (settings == null) return;

                // роли 4.8 живут в YAML ассета до первого пересохранения — перенести их раньше, чем ассет сохранится
                AnalyticsReportStore.MigrateLegacy();

                // порядок в ассете = Id в рантайме: несортированный каталог сначала записать отсортированным
                if (!AnalyticsCatalog.IsSorted(settings.Entries)) AnalyticsCatalog.Commit(settings, settings.Entries, "Сортировка каталога аналитики");
                else AnalyticsCatalog.Generate(settings.Entries);
            };
        }

        internal static AnalyticsSettings Ensure(bool force)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode && !force) return null;
            var settings = AnalyticsCatalog.Ensure(force);
            if (settings != null) EnsurePreloaded(settings);
            return settings;
        }

        /// <summary>Ассет — в Preloaded Assets текущего профиля Player Settings. true — список изменён.</summary>
        internal static bool EnsurePreloaded(AnalyticsSettings settings)
        {
            var current = PlayerSettings.GetPreloadedAssets();
            var list = new List<UnityEngine.Object>(current.Length + 1);
            var present = false;
            var dirty = false;

            foreach (var asset in current)
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

    /// <summary>Перед сборкой: настройки в Preloaded Assets, каталог отсортирован и без ошибок.</summary>
    internal sealed class AnalyticsBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = AnalyticsCatalog.Load();
            if (settings == null) return;

            if (AnalyticsBootstrap.EnsurePreloaded(settings))
                Debug.Log("[AppAnalytics] Настройки аналитики добавлены в Preloaded Assets собираемого профиля — закоммитьте профиль.");

            if (!AnalyticsCatalog.IsSorted(settings.Entries))
                throw new BuildFailedException("[AppAnalytics] Каталог событий не отсортирован — Id в AppEvents не совпадут. " +
                                               "Exerussus/App/Analytics/Синхронизировать каталог.");

            foreach (var issue in AnalyticsCatalog.Validate(settings.Entries))
                if (issue.Level == AnalyticsCatalog.IssueLevel.Error)
                    throw new BuildFailedException($"[AppAnalytics] {issue.Key}: {issue.Text}");

            WarnOutOfRange(settings);
        }

        /// <summary>
        /// Значения вне пределов (правка YAML, мерж) — предупреждение: в рантайме они всё равно зажимаются
        /// (AnalyticsSettings), но настройка в ассете не совпадает с тем, что будет в игре.
        /// </summary>
        private static void WarnOutOfRange(AnalyticsSettings settings)
        {
            var so = new SerializedObject(settings);
            void Check(string name, float min, float max)
            {
                var p = so.FindProperty(name);
                if (p == null) return;
                var v = p.propertyType == SerializedPropertyType.Float ? p.floatValue : p.intValue;
                if (float.IsNaN(v) || v < min || v > max)
                    Debug.LogWarning($"[AppAnalytics] Настройка {name} = {v} вне [{min}; {max}] — в игре будет зажата.", settings);
            }

            Check("maxPacketKilobytes", 4, AnalyticsSettings.MaxPacketKilobytesCeiling);
            Check("maxQueuedKilobytes", 16, AnalyticsSettings.MaxQueuedKilobytesCeiling);
            Check("leanMaxQueuedKilobytes", 16, AnalyticsSettings.LeanMaxQueuedKilobytesCeiling);
            Check("maxQueuedPackets", 1, AnalyticsSettings.MaxQueuedPacketsCeiling);
            Check("leanMaxQueuedPackets", 1, AnalyticsSettings.LeanMaxQueuedPacketsCeiling);
            Check("maxEventsPerPacket", 1, 100000);
            Check("maxPacketAge", 1, 3600);
            Check("deliveryLease", 1, 3600);
            Check("retryBase", 0.1f, 3600);
            Check("retryMax", 0.1f, 86400);
            Check("maxInFlight", 1, 16);
        }
    }

    /// <summary>Импорт ассета настроек (правка в инспекторе, git pull) → порядок и AppEvents догоняют каталог.</summary>
    internal sealed class AnalyticsAssetsPostprocessor : AssetPostprocessor
    {
        private static bool _queued;

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (Array.IndexOf(imported, AnalyticsSettings.AssetPath) < 0) return;
            AnalyticsSettings.ResetLookup();
            if (_queued) return;
            _queued = true;

            // правки ассета во время импорта запрещены — после
            EditorApplication.delayCall += () =>
            {
                _queued = false;
                var settings = AnalyticsCatalog.Load();
                if (settings == null) return;

                if (!AnalyticsCatalog.IsSorted(settings.Entries))
                {
                    AnalyticsCatalog.Commit(settings, settings.Entries, "Сортировка каталога аналитики");
                    return;
                }

                AnalyticsCatalog.Generate(settings.Entries);
            };
        }
    }
}
