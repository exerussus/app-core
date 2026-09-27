using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Exerussus.AppCore.Build;
using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Единственные ассеты AppCore по фиксированным путям <see cref="AppCorePaths"/>:
    /// поиск, создание, проверка на дубликаты.
    /// </summary>
    public static class AppCoreAssets
    {
        public static NavigationSettings FindNavigationSettings()
            => AssetDatabase.LoadAssetAtPath<NavigationSettings>(AppCorePaths.NavigationSettingsPath);

        public static BuildInfo FindBuildInfo()
            => AssetDatabase.LoadAssetAtPath<BuildInfo>(AppCorePaths.BuildInfoPath);

        public static AppTextSettings FindTextSettings()
            => AssetDatabase.LoadAssetAtPath<AppTextSettings>(AppCorePaths.TextSettingsPath);

        public static NavigationSettings EnsureNavigationSettings() => Ensure<NavigationSettings>(AppCorePaths.NavigationSettingsPath);

        public static BuildInfo EnsureBuildInfo()
        {
            var existing = FindBuildInfo();
            if (existing != null) return existing;

            var info = Ensure<BuildInfo>(AppCorePaths.BuildInfoPath);
            info.EditorReset();
            EditorUtility.SetDirty(info);
            AssetDatabase.SaveAssetIfDirty(info);
            return info;
        }

        public static AppTextSettings EnsureTextSettings() => Ensure<AppTextSettings>(AppCorePaths.TextSettingsPath);

        /// <summary>Зарегистрированные id указанного вида (пусто, если реестра нет).</summary>
        public static IReadOnlyList<string> GetIds(ViewKind kind)
        {
            var settings = FindNavigationSettings();
            return settings != null ? settings.GetIds(kind) : System.Array.Empty<string>();
        }

        /// <summary>Все ассеты типа в проекте — чтобы поймать лишние экземпляры.</summary>
        public static List<string> FindAllPaths<T>() where T : Object
        {
            var result = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + typeof(T).Name))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.LoadAssetAtPath<T>(path) != null) result.Add(path);
            }
            return result;
        }

        public static T Ensure<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;

            EnsureFolder(Path.GetDirectoryName(path));
            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AppCore] Создан {path}");
            return asset;
        }

        /// <summary>Рекурсивно создаёт папку ассетов (с .meta). <c>Assets</c> валиден всегда.</summary>
        public static void EnsureFolder(string folder)
        {
            folder = (folder ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;

            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var leaf = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>Пишет текст, только если он изменился. <c>true</c> — файл записан.</summary>
        public static bool WriteTextIfChanged(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return false;

            EnsureFolder(Path.GetDirectoryName(path));
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            return true;
        }
    }

    /// <summary>
    /// Первичная раскладка: ассеты из <c>Assets/App/Settings</c> создаются при первой загрузке
    /// редактора, если их нет. Ничего не перезаписывает.
    /// </summary>
    [InitializeOnLoad]
    internal static class AppCoreBootstrap
    {
        static AppCoreBootstrap()
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

            // Не навязываем раскладку проекту, где AppCore ещё не используется.
            if (!AssetDatabase.IsValidFolder(AppCorePaths.Root)) return;

            AppCoreAssets.EnsureNavigationSettings();
            AppCoreAssets.EnsureBuildInfo();
            AppCoreAssets.EnsureTextSettings();
        }
    }
}
