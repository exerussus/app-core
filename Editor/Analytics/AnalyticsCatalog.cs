using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Analytics.Editor
{
    /// <summary>
    /// Редакторские операции над каталогом событий: порядок, проверка, генерация <c>AppEvents.cs</c>.
    /// Правки каталога — через <see cref="Commit"/>: сортировка → запись ассета → генерация.
    /// </summary>
    public static class AnalyticsCatalog
    {
        public static AnalyticsSettings Load() => AssetDatabase.LoadAssetAtPath<AnalyticsSettings>(AnalyticsSettings.AssetPath);

        /// <summary>Ассет настроек: найти или создать (если AppCore развёрнут — есть Assets/App/Settings).</summary>
        public static AnalyticsSettings Ensure(bool force)
        {
            var settings = Load();
            if (settings != null) return settings;

            if (!AssetDatabase.IsValidFolder(AppCorePaths.SettingsDir))
            {
                if (force) Debug.LogWarning($"[AppAnalytics] Нет папки {AppCorePaths.SettingsDir} — AppCore не развёрнут в проекте.");
                return null;
            }

            settings = ScriptableObject.CreateInstance<AnalyticsSettings>();
            AssetDatabase.CreateAsset(settings, AnalyticsSettings.AssetPath);
            AnalyticsSettings.ResetLookup();
            Debug.Log($"[AppAnalytics] Создан ассет настроек: {AnalyticsSettings.AssetPath}");
            return settings;
        }

        /// <summary>Записать каталог: сортировка по ключу (это номера Id), ассет, генерация. Undo — одним шагом.</summary>
        public static void Commit(AnalyticsSettings settings, AnalyticsEventEntry[] entries, string undoName)
        {
            Undo.RecordObject(settings, undoName);
            var sorted = Sorted(entries);
            settings.SetEntries(sorted);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            Generate(sorted);
        }

        public static AnalyticsEventEntry[] Sorted(AnalyticsEventEntry[] entries)
        {
            var copy = (AnalyticsEventEntry[])(entries ?? Array.Empty<AnalyticsEventEntry>()).Clone();
            Array.Sort(copy, (a, b) => string.CompareOrdinal(a.key, b.key));
            return copy;
        }

        public static bool IsSorted(AnalyticsEventEntry[] entries)
        {
            for (int i = 1; i < entries.Length; i++)
                if (string.CompareOrdinal(entries[i - 1].key, entries[i].key) > 0) return false;
            return true;
        }

        /// <summary>Перегенерировать <c>AppEvents.cs</c>. true — файл изменился (будет перекомпиляция).</summary>
        public static bool Generate(AnalyticsEventEntry[] sortedEntries)
        {
            var text = AnalyticsKeys.Generate(sortedEntries);
            if (!global::Exerussus.AppCore.Editor.AppCoreAssets.WriteTextIfChanged(AnalyticsSettings.KeysPath, text)) return false;
            AssetDatabase.ImportAsset(AnalyticsSettings.KeysPath);
            return true;
        }

        public enum IssueLevel { Info, Warning, Error }

        public readonly struct Issue
        {
            public readonly IssueLevel Level;
            public readonly string Key;
            public readonly string Text;

            public Issue(IssueLevel level, string key, string text)
            {
                Level = level;
                Key = key;
                Text = text;
            }
        }

        /// <summary>Замечания по каталогу: ключи, дубли, поля.</summary>
        public static List<Issue> Validate(AnalyticsEventEntry[] entries)
        {
            var issues = new List<Issue>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var e in entries)
            {
                var error = AnalyticsKeys.ValidateKey(e.key);
                if (error != null) issues.Add(new Issue(IssueLevel.Error, e.key, error));
                if (!seen.Add(e.key ?? string.Empty)) issues.Add(new Issue(IssueLevel.Error, e.key, "ключ повторяется"));

                if (e.fields != null)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var f in e.fields)
                    {
                        var fieldError = AnalyticsKeys.ValidateField(f.name);
                        if (fieldError != null) issues.Add(new Issue(IssueLevel.Error, e.key, $"поле «{f.name}»: {fieldError}"));
                        else if (!names.Add(f.name)) issues.Add(new Issue(IssueLevel.Error, e.key, $"поле «{f.name}» повторяется"));
                    }
                }

            }

            return issues;
        }

        /// <summary>
        /// Каталог + классификация событий (файл представлений): структура каталога и куда входит каждое событие.
        /// Сборку блокирует только структура (<see cref="Validate(AnalyticsEventEntry[])"/>) — отчёт на данные не влияет.
        /// </summary>
        public static List<Issue> ValidateAll(AnalyticsSettings settings)
        {
            var issues = Validate(settings.Entries);
            foreach (var r in AnalyticsReportStore.Validate(settings))
                issues.Add(new Issue((IssueLevel)(int)r.Level, r.Key, r.Text));
            return issues;
        }

        // ---------------------------------------------------------------- меню

        [MenuItem("Exerussus/App/Analytics/Синхронизировать каталог")]
        private static void SyncMenu()
        {
            var settings = AnalyticsBootstrap.Ensure(true);
            if (settings == null) return;

            Commit(settings, settings.Entries, "Синхронизация каталога аналитики");
            var issues = ValidateAll(settings);
            foreach (var issue in issues)
            {
                var text = $"[AppAnalytics] {issue.Key}: {issue.Text}";
                if (issue.Level == IssueLevel.Error) Debug.LogError(text, settings);
                else Debug.LogWarning(text, settings);
            }

            Debug.Log($"[AppAnalytics] Каталог: {settings.Entries.Length} событий, замечаний: {issues.Count}.", settings);
            Selection.activeObject = settings;
        }

        [MenuItem("Exerussus/App/Analytics/Открыть папку данных")]
        private static void OpenDataFolder()
        {
            var path = System.IO.Path.Combine(Application.persistentDataPath, "AppAnalytics");
            System.IO.Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }
    }
}
