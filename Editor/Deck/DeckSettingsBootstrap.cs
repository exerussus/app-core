using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck.Editor
{
    /// <summary>
    /// Ассет настроек AppDeck появляется сам: лежит по фиксированному пути в Assets/App/Settings,
    /// ссылки на панель и стили проставлены, ассет — в Preloaded Assets (иначе сборка не увидит
    /// его до первой сцены). Если AppCore в проекте не развёрнут (нет Assets/App/Settings) — не мусорим.
    /// </summary>
    [InitializeOnLoad]
    internal static class DeckSettingsBootstrap
    {
        static DeckSettingsBootstrap()
        {
            EditorApplication.delayCall += () => Ensure(false);
        }

        [MenuItem("Exerussus/App/AppDeck Settings")]
        private static void SelectSettings()
        {
            DeckSettings settings = Ensure(true);
            if (settings != null) Selection.activeObject = settings;
        }

        private static DeckSettings Ensure(bool force)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return null;

            var settings = AssetDatabase.LoadAssetAtPath<DeckSettings>(DeckSettings.AssetPath);

            if (settings == null)
            {
                if (!AssetDatabase.IsValidFolder(AppCorePaths.SettingsDir))
                {
                    if (!force) return null;
                    Debug.LogWarning($"[AppDeck] Нет папки {AppCorePaths.SettingsDir} — AppCore не развёрнут в проекте.");
                    return null;
                }

                settings = ScriptableObject.CreateInstance<DeckSettings>();
                AssetDatabase.CreateAsset(settings, DeckSettings.AssetPath);
                Debug.Log($"[AppDeck] Создан ассет настроек: {DeckSettings.AssetPath}");
            }

            AssignReferences(settings);
            EnsurePreloaded(settings);
            return settings;
        }

        private static void AssignReferences(DeckSettings settings)
        {
            var so = new SerializedObject(settings);
            var changed = false;

            SerializedProperty panel = so.FindProperty("panelSettings");
            if (panel != null && panel.objectReferenceValue == null)
            {
                panel.objectReferenceValue = AssetDatabase.LoadAssetAtPath<PanelSettings>(AppCorePaths.PanelSettingsPath);
                changed |= panel.objectReferenceValue != null;
            }

            SerializedProperty sheet = so.FindProperty("styleSheet");
            if (sheet != null && sheet.objectReferenceValue == null)
            {
                sheet.objectReferenceValue = AssetDatabase.LoadAssetAtPath<StyleSheet>(DeckSettings.StyleSheetPath);
                changed |= sheet.objectReferenceValue != null;
            }

            if (!changed) return;

            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        private static void EnsurePreloaded(DeckSettings settings)
        {
            Object[] current = PlayerSettings.GetPreloadedAssets();
            var list = new List<Object>(current.Length + 1);
            var present = false;
            var dirty = false;

            foreach (Object asset in current)
            {
                // битые ссылки (удалённые ассеты) вычищаем заодно
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

            if (!dirty) return;

            PlayerSettings.SetPreloadedAssets(list.ToArray());

            // настройки проекта — на диск сразу: иначе ProjectSettings.asset запишется только при закрытии
            // редактора, и до этого сборка/коммит не увидят ассет в Preloaded Assets
            AssetDatabase.SaveAssets();
        }
    }
}
