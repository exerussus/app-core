using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Exerussus.AppCore.Build;
using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Проверка раскладки AppCore: единственные ассеты на своих местах, вью привязаны к id по канону.
    /// </summary>
    public static class AppValidator
    {
        public sealed class Issue
        {
            public string Message;
            public UnityEngine.Object Context;
        }

        /// <summary>Все проблемы проекта и открытых сцен.</summary>
        public static List<Issue> Collect()
        {
            var issues = new List<Issue>();

            CheckSingleton<NavigationSettings>(AppCorePaths.NavigationSettingsPath, issues);
            CheckSingleton<BuildInfo>(AppCorePaths.BuildInfoPath, issues);
            CheckSingleton<AppTextSettings>(AppCorePaths.TextSettingsPath, issues);

            foreach (ViewKind kind in Enum.GetValues(typeof(ViewKind)))
            {
                var seen = new Dictionary<string, Component>(StringComparer.Ordinal);

                foreach (var view in ViewBinding.FindViewsInOpenScenes(kind))
                {
                    foreach (var problem in ViewBinding.Validate(view))
                        issues.Add(new Issue { Message = $"{kind} «{view.gameObject.name}»: {problem}", Context = view });

                    var id = ViewBinding.GetId(view);
                    if (string.IsNullOrEmpty(id)) continue;

                    // Фрагменты уникальны в пределах своего вью, страницы и попапы — глобально.
                    var key = kind == ViewKind.Fragment ? view.transform.parent?.HierarchyKey() + "/" + id : id;
                    if (seen.TryGetValue(key, out var clash))
                        issues.Add(new Issue { Message = $"{kind}: id «{id}» занят дважды — «{clash.gameObject.name}» и «{view.gameObject.name}».", Context = view });
                    else
                        seen[key] = view;
                }
            }

            return issues;
        }

        [MenuItem("Exerussus/App/Validate", false, 10)]
        public static void RunFromMenu()
        {
            var issues = Collect();
            if (issues.Count == 0)
            {
                Debug.Log("[AppCore] Проверка: всё по канону.");
                return;
            }

            foreach (var issue in issues) Debug.LogWarning("[AppCore] " + issue.Message, issue.Context);

            var fix = EditorUtility.DisplayDialog("AppCore — проверка",
                $"Проблем: {issues.Count} (подробности в консоли).\n\nПривести вью открытых сцен к канону — имена, вёрстку, контроллеры?",
                "Привести", "Не сейчас");

            if (fix) SyncAll();
        }

        [MenuItem("Exerussus/App/Sync Views", false, 11)]
        public static void SyncAll()
        {
            var sb = new StringBuilder();
            var count = 0;

            foreach (ViewKind kind in Enum.GetValues(typeof(ViewKind)))
            {
                foreach (var view in ViewBinding.FindViewsInOpenScenes(kind))
                {
                    var id = ViewBinding.GetId(view);
                    if (string.IsNullOrEmpty(id)) continue;

                    if (ViewBinding.Sync(view, recordUndo: true)) count++;
                    ViewCodeGenerator.RegenerateElements(kind, id, out _);
                }
            }

            sb.Append($"[AppCore] Вью приведено к канону: {count}.");
            Debug.Log(sb.ToString());
        }

        private static void CheckSingleton<T>(string canonical, List<Issue> issues) where T : ScriptableObject
        {
            var paths = AppCoreAssets.FindAllPaths<T>();

            if (!paths.Contains(canonical))
                issues.Add(new Issue { Message = $"Нет {typeof(T).Name} по пути {canonical}." });

            foreach (var path in paths)
                if (path != canonical)
                    issues.Add(new Issue
                    {
                        Message = $"Лишний {typeof(T).Name}: {path}. Экземпляр должен быть один — {canonical}.",
                        Context = AssetDatabase.LoadAssetAtPath<T>(path),
                    });
        }

        // Ключ родителя: путь в иерархии + сцена.
        private static string HierarchyKey(this Transform t)
        {
            var sb = new StringBuilder(t.gameObject.scene.path);
            for (var cur = t; cur != null; cur = cur.parent) sb.Append('/').Append(cur.name);
            return sb.ToString();
        }
    }
}
