using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>
    /// Редакторская обвязка локализации: ассет настроек, базовая таблица, Preloaded Assets,
    /// константы ключей, горячая перезагрузка.
    /// </summary>
    public static class LocEditor
    {
        /// <summary>Ассет настроек (создаётся, если нет и есть папка AppCore).</summary>
        public static LocSettings EnsureSettings(bool force)
        {
            var settings = AssetDatabase.LoadAssetAtPath<LocSettings>(LocSettings.AssetPath);

            if (settings == null)
            {
                if (!AssetDatabase.IsValidFolder(AppCorePaths.SettingsDir))
                {
                    if (force) Debug.LogWarning($"[Loc] Нет папки {AppCorePaths.SettingsDir} — AppCore не развёрнут в проекте.");
                    return null;
                }

                settings = ScriptableObject.CreateInstance<LocSettings>();
                AssetDatabase.CreateAsset(settings, LocSettings.AssetPath);
                LocSettings.ResetLookup();
                Debug.Log($"[Loc] Создан ассет настроек: {LocSettings.AssetPath}");
            }

            EnsurePreloaded(settings);
            if (settings.Enabled) EnsureBaseTable(settings);
            return settings;
        }

        /// <summary>
        /// Включённой локализации нужна таблица: если не назначена — берётся base.loc по
        /// стандартному пути, а нет и его — создаётся с русским и английским.
        /// </summary>
        public static TextAsset EnsureBaseTable(LocSettings settings)
        {
            if (settings.BaseTable != null) return settings.BaseTable;

            var path = AppCorePaths.BaseLocTablePath;
            if (!File.Exists(path))
            {
                AppCoreAssets.EnsureFolder(AppCorePaths.LocalizationDir);

                var table = new LocTable();
                table.AddLanguage("ru", "Русский");
                table.AddLanguage("en", "English");
                File.WriteAllText(path, LocFormat.Write(table), new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(path);
                Debug.Log($"[Loc] Создана базовая таблица: {path}");
            }

            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) return null;

            var so = new SerializedObject(settings);
            so.FindProperty("baseTable").objectReferenceValue = asset;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(settings);
            return asset;
        }

        /// <summary>Путь базовой таблицы. Пусто — не назначена.</summary>
        public static string BaseTablePath
        {
            get
            {
                var settings = LocSettings.Current;
                return settings != null && settings.BaseTable != null ? AssetDatabase.GetAssetPath(settings.BaseTable) : null;
            }
        }

        /// <summary>Читает базовую таблицу с диска. <c>null</c> — нет таблицы.</summary>
        public static LocParseResult ReadBaseTable()
        {
            var path = BaseTablePath;
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : LocFormat.Parse(File.ReadAllText(path));
        }

        /// <summary>
        /// Пишет базовую таблицу (LF, без BOM), импортирует, перегенерирует константы и, если идёт
        /// игра, перечитывает таблицу в рантайме.
        /// </summary>
        public static bool WriteBaseTable(LocTable table)
        {
            var path = BaseTablePath;
            if (string.IsNullOrEmpty(path)) return false;

            var text = LocFormat.Write(table);
            if (File.Exists(path) && File.ReadAllText(path) == text) return false;

            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            AssetDatabase.ImportAsset(path);
            return true;
        }

        /// <summary>
        /// Ассет — в Preloaded Assets текущего контекста Player Settings (глобальных или активного
        /// профиля сборки). true — список изменён.
        /// </summary>
        public static bool EnsurePreloaded(Object asset)
        {
            var current = PlayerSettings.GetPreloadedAssets();
            var list = new List<Object>(current.Length + 1);
            var present = false;
            var dirty = false;

            foreach (var item in current)
            {
                if (item == null)
                {
                    dirty = true;
                    continue;
                }

                if (item == asset) present = true;
                list.Add(item);
            }

            if (!present)
            {
                list.Add(asset);
                dirty = true;
            }

            if (!dirty) return false;

            PlayerSettings.SetPreloadedAssets(list.ToArray());
            AssetDatabase.SaveAssets();
            return true;
        }

        // ---------------------------------------------------------------- константы ключей

        /// <summary>
        /// Генерирует <c>L.MENU_PLAY = "^MENU_PLAY"</c> по базовой таблице и встроенным ключам.
        /// Пишет файл, только если он изменился. true — записан.
        /// </summary>
        public static bool GenerateKeys()
        {
            var settings = LocSettings.Current;
            if (settings == null || !settings.Enabled || !settings.GenerateKeys) return false;

            var parsed = ReadBaseTable();
            var keys = new SortedSet<string>(System.StringComparer.Ordinal);

            foreach (var key in new[] { LocBuiltin.Restart, LocBuiltin.Quit, LocBuiltin.Close, LocBuiltin.CriticalError }) keys.Add(key);
            if (parsed != null)
                foreach (var entry in parsed.Table.Entries)
                    keys.Add(entry.Key);

            var sb = new System.Text.StringBuilder();
            sb.Append("// <auto-generated>\n");
            sb.Append("// Сгенерировано AppCore из таблицы локализации. Не редактировать руками —\n");
            sb.Append("// файл перезаписывается при каждом изменении таблицы.\n");
            sb.Append("// </auto-generated>\n\n");
            sb.Append("namespace ").Append(settings.KeysNamespace).Append("\n{\n");
            sb.Append("    /// <summary>Ключи локализации. Значение — строка с '^', её понимают Loc и LocBinder.</summary>\n");
            sb.Append("    public static class L\n    {\n");

            var used = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var name = Identifier(key);
                if (!used.Add(name))
                {
                    sb.Append("        // ").Append(key).Append(": имя ").Append(name).Append(" занято другим ключом\n");
                    continue;
                }

                sb.Append("        public const string ").Append(name).Append(" = \"^").Append(key).Append("\";\n");
            }

            sb.Append("    }\n}\n");

            if (!AppCoreAssets.WriteTextIfChanged(AppCorePaths.LocKeysPath, sb.ToString())) return false;

            AssetDatabase.ImportAsset(AppCorePaths.LocKeysPath);
            return true;
        }

        private static string Identifier(string key)
        {
            var sb = new System.Text.StringBuilder(key.Length + 1);
            foreach (var ch in key) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }

    /// <summary>Настройки появляются сами, меню открывает их.</summary>
    [InitializeOnLoad]
    internal static class LocSettingsBootstrap
    {
        static LocSettingsBootstrap()
        {
            EditorApplication.delayCall += EnsureOnce;
        }

        private static void EnsureOnce()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += EnsureOnce;
                return;
            }

            if (LocEditor.EnsureSettings(false) != null) LocEditor.GenerateKeys();
        }

        [MenuItem("Exerussus/App/Localization Settings")]
        private static void SelectSettings()
        {
            var settings = LocEditor.EnsureSettings(true);
            if (settings != null) Selection.activeObject = settings;
        }
    }

    /// <summary>
    /// Изменился <c>.loc</c> — перегенерировать константы и, если идёт игра, перечитать таблицы.
    /// Изменились настройки — то же самое.
    /// </summary>
    internal sealed class LocAssetWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var touched = false;
            foreach (var path in imported)
            {
                if (path.EndsWith(LocFormat.Extension, System.StringComparison.OrdinalIgnoreCase) ||
                    path == LocSettings.AssetPath)
                {
                    touched = true;
                    break;
                }
            }

            if (!touched) return;

            LocSettings.ResetLookup();

            // Генерация пишет .cs и импортирует его — не во время текущего импорта.
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling) return;

                LocEditor.GenerateKeys();

                var settings = LocSettings.Current;
                if (Application.isPlaying && settings != null && settings.Enabled && settings.HotReload) Loc.Reload();
            };
        }
    }

    /// <summary>
    /// Перед сборкой: настройки — в Preloaded Assets собираемого профиля, ключи кода и вёрстки
    /// сверяются с таблицей (см. <see cref="LocSettings.BuildCheck"/>).
    /// </summary>
    internal sealed class LocBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = AssetDatabase.LoadAssetAtPath<LocSettings>(LocSettings.AssetPath);
            if (settings == null) return;

            if (LocEditor.EnsurePreloaded(settings))
            {
                var profile = BuildProfile.GetActiveBuildProfile();
                var where = profile != null ? $"профиль сборки «{profile.name}»" : "Player Settings";
                Debug.Log($"[Loc] Настройки добавлены в Preloaded Assets ({where}) — закоммитьте изменённый файл.");
            }

            if (!settings.Enabled || settings.BuildCheck == LocBuildCheck.Off) return;

            var parsed = LocEditor.ReadBaseTable();
            if (parsed == null)
            {
                Fail(settings, "[Loc] Локализация включена, а базовая таблица не назначена.");
                return;
            }

            if (!parsed.Ok) Fail(settings, "[Loc] Ошибки в базовой таблице:\n" + parsed.ErrorText(Path.GetFileName(LocEditor.BaseTablePath)));

            var report2 = LocKeyScanner.Check(parsed.Table, settings);
            if (report2.Unknown.Count > 0)
                Fail(settings, "[Loc] В коде и вёрстке есть ключи, которых нет в таблице:\n" + report2.Describe(20));
        }

        private static void Fail(LocSettings settings, string message)
        {
            if (settings.BuildCheck == LocBuildCheck.Fail) throw new BuildFailedException(message);
            Debug.LogWarning(message);
        }
    }
}
