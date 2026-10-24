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
