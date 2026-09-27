using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Screens;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Переход проекта с AppCore 3.x на 4.0: единственный NavigationSettings по фиксированному
    /// пути, реестр id из NavigationData, вёрстка вью из двух файлов → один uxml со слоями.
    /// </summary>
    /// <remarks>
    /// Вью и скрины мигрируются в ОТКРЫТЫХ сценах. Остальные сцены открыть и запустить снова —
    /// повторный запуск безопасен: уже мигрированное не трогается.
    /// </remarks>
    public static class AppMigration
    {
        [MenuItem("Exerussus/App/Migrate to 4.0", false, 20)]
        public static void Run()
        {
            var log = new StringBuilder();

            MigrateNavigationSettings(log);
            MigrateViews(log);
            MigrateScreens(log);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var text = log.Length == 0 ? "Мигрировать нечего." : log.ToString().TrimEnd();
            Debug.Log("[AppCore] Миграция 4.0:\n" + text);
            EditorUtility.DisplayDialog("AppCore — миграция 4.0", text, "OK");
        }

        // ============================================================ NavigationSettings

        private static void MigrateNavigationSettings(StringBuilder log)
        {
            var canonical = AppCorePaths.NavigationSettingsPath;
            var paths = AppCoreAssets.FindAllPaths<NavigationSettings>();

            if (!paths.Contains(canonical))
            {
                var source = paths.Count > 0 ? paths[0] : null;
                if (source != null)
                {
                    AppCoreAssets.EnsureFolder(Path.GetDirectoryName(canonical));
                    var error = AssetDatabase.MoveAsset(source, canonical);
                    if (string.IsNullOrEmpty(error)) log.AppendLine($"NavigationSettings перенесён: {source} → {canonical}");
                    else log.AppendLine($"(!) Не удалось перенести {source}: {error}");
                }
                else
                {
                    AppCoreAssets.EnsureNavigationSettings();
                    log.AppendLine($"NavigationSettings создан: {canonical}");
                }
            }

            foreach (var extra in AppCoreAssets.FindAllPaths<NavigationSettings>())
                if (extra != canonical) log.AppendLine($"(!) Лишний NavigationSettings: {extra} — удалите его руками после проверки.");

            // NavigationData (3.x): класса больше нет, ассет читаем как текст.
            var settings = AppCoreAssets.FindNavigationSettings();
            foreach (var guid in AssetDatabase.FindAssets("NavigationData"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) continue;

                var yaml = File.ReadAllText(path);
                if (!yaml.Contains("pages:") && !yaml.Contains("popups:")) continue;

                var pages = ReadYamlList(yaml, "pages");
                var popups = ReadYamlList(yaml, "popups");

                if (settings != null && (pages.Count > 0 || popups.Count > 0))
                {
                    Undo.RecordObject(settings, "Migrate NavigationData");
                    settings.EditorSetIds(ViewKind.Page, Union(settings.Pages, pages));
                    settings.EditorSetIds(ViewKind.Popup, Union(settings.Popups, popups));
                    EditorUtility.SetDirty(settings);
                }

                if (AssetDatabase.DeleteAsset(path))
                    log.AppendLine($"NavigationData слит в NavigationSettings и удалён: {path} ({pages.Count} стр., {popups.Count} поп.)");
            }
        }

        private static List<string> ReadYamlList(string yaml, string key)
        {
            var result = new List<string>();
            var m = Regex.Match(yaml, @"^\s*" + key + @":\s*\n((?:\s*-\s*.*\n?)*)", RegexOptions.Multiline);
            if (!m.Success) return result;

            foreach (Match item in Regex.Matches(m.Groups[1].Value, @"^\s*-\s*(.*?)\s*$", RegexOptions.Multiline))
            {
                var value = item.Groups[1].Value.Trim().Trim('\'', '"');
                if (value.Length > 0) result.Add(value);
            }

            return result;
        }

        private static List<string> Union(IReadOnlyList<string> a, List<string> b)
        {
            var result = new List<string>(a);
            foreach (var id in b) if (!result.Contains(id)) result.Add(id);
            return result;
        }

        // ============================================================ вью

        private static void MigrateViews(StringBuilder log)
        {
            foreach (ViewKind kind in Enum.GetValues(typeof(ViewKind)))
            {
                foreach (var view in ViewBinding.FindViewsInOpenScenes(kind))
                {
                    var so = new SerializedObject(view);
                    var full = so.FindProperty(ViewBinding.LegacyFullProperty);
                    var safe = so.FindProperty(ViewBinding.LegacySafeProperty);
                    if (full == null || safe == null) continue;

                    var fullTree = full.objectReferenceValue as VisualTreeAsset;
                    var safeTree = safe.objectReferenceValue as VisualTreeAsset;
                    if (fullTree == null && safeTree == null) continue;

                    var id = so.FindProperty(ViewBinding.IdPropertyName(kind)).stringValue;
                    if (string.IsNullOrEmpty(id))
                    {
                        log.AppendLine($"(!) {view.gameObject.name}: нет id — вёрстку некуда сложить.");
                        continue;
                    }

                    var target = AppCorePaths.ViewUxmlPath(kind, id);
                    if (!WriteMerged(fullTree, safeTree, target, log)) continue;

                    so.FindProperty(ViewBinding.VisualTreeProperty).objectReferenceValue = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(target);
                    full.objectReferenceValue = null;
                    safe.objectReferenceValue = null;
                    so.ApplyModifiedPropertiesWithoutUndo();

                    ViewBinding.Sync(view, recordUndo: false);
                    ViewBinding.MarkDirty(view);
                    log.AppendLine($"{kind} «{id}»: вёрстка слита в {target}");
                }
            }

            SaveOpenScenes();
        }

        // ============================================================ скрины

        private static void MigrateScreens(StringBuilder log)
        {
            var screens = new List<MonoBehaviour>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    screens.AddRange(root.GetComponentsInChildren<AppScreen>(true));
                    screens.AddRange(root.GetComponentsInChildren<LoadingScreen>(true));
                }
            }

            foreach (var screen in screens)
            {
                var so = new SerializedObject(screen);
                var full = so.FindProperty(ViewBinding.LegacyFullProperty);
                var safe = so.FindProperty(ViewBinding.LegacySafeProperty);
                if (full == null || safe == null) continue;

                var fullTree = full.objectReferenceValue as VisualTreeAsset;
                var safeTree = safe.objectReferenceValue as VisualTreeAsset;
                if (fullTree == null && safeTree == null) continue;

                // Скрины живут где угодно: кладём слитый файл рядом с безопасной вёрсткой.
                var anchor = AssetDatabase.GetAssetPath(safeTree != null ? safeTree : fullTree);
                var target = $"{Path.GetDirectoryName(anchor)?.Replace('\\', '/')}/{screen.GetType().Name}.uxml";

                if (!WriteMerged(fullTree, safeTree, target, log)) continue;

                so.FindProperty(ViewBinding.VisualTreeProperty).objectReferenceValue = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(target);
                full.objectReferenceValue = null;
                safe.objectReferenceValue = null;
                so.ApplyModifiedPropertiesWithoutUndo();

                EditorUtility.SetDirty(screen);
                EditorSceneManager.MarkSceneDirty(screen.gameObject.scene);
                log.AppendLine($"Скрин {screen.GetType().Name}: вёрстка слита в {target}");
            }

            SaveOpenScenes();
        }

        private static bool WriteMerged(VisualTreeAsset fullTree, VisualTreeAsset safeTree, string target, StringBuilder log)
        {
            try
            {
                var fullText = fullTree != null ? File.ReadAllText(AssetDatabase.GetAssetPath(fullTree)) : null;
                var safeText = safeTree != null ? File.ReadAllText(AssetDatabase.GetAssetPath(safeTree)) : null;
                var merged = UxmlLayerMerge.Merge(fullText, safeText);

                AppCoreAssets.EnsureFolder(Path.GetDirectoryName(target));
                File.WriteAllText(target, merged, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(target);
                return true;
            }
            catch (Exception e)
            {
                log.AppendLine($"(!) {target}: не удалось слить вёрстку — {e.Message}");
                return false;
            }
        }

        private static void SaveOpenScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.isDirty && !string.IsNullOrEmpty(scene.path))
                    EditorSceneManager.SaveScene(scene);
            }
        }
    }
}
