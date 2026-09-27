using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Exerussus.AppCore.Build;

namespace Exerussus.AppCore.Editor.Build
{
    /// <summary>
    /// Общие настройки сборки из <c>Assets/App/Settings/build-config.json</c> — та часть, что нужна
    /// штампу BuildInfo. Остальные поля файла принадлежат вкладке «Сборка» страницы App в Nexus.
    /// </summary>
    [Serializable]
    public sealed class BuildStampSettings
    {
        public string prefix = "";
        public string postfix = "";
        public int hashLength = 7;
        public bool markDirty = true;

        public static BuildStampSettings Load()
        {
            var settings = new BuildStampSettings();
            try
            {
                if (File.Exists(AppCorePaths.BuildConfigPath))
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(AppCorePaths.BuildConfigPath), settings);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AppCore] Не удалось прочитать {AppCorePaths.BuildConfigPath}: {ex.Message}");
            }
            return settings;
        }
    }

    /// <summary>
    /// Штамп и откат <see cref="BuildInfo"/> вокруг билда. Работает без Nexus: любой билд
    /// (File → Build, CI через -executeMethod) получает правильные значения.
    /// </summary>
    /// <remarks>
    /// Откат: после билда — <see cref="IPostprocessBuildWithReport"/>; если билд упал (postprocess
    /// не вызывается) — первый тик редактора после билда; если упал сам редактор — при следующей
    /// загрузке. Инвариант: вне билда ассет ВСЕГДА в «editor-play-mode».
    /// </remarks>
    public static class BuildInfoStamp
    {
        public static void Stamp(BuildTarget target)
        {
            var info = AppCoreAssets.EnsureBuildInfo();
            if (info == null)
            {
                Debug.LogWarning("[AppCore] BuildInfo не найден и не может быть создан — штамп пропущен.");
                return;
            }

            var settings = BuildStampSettings.Load();
            var git = GitInfo.Read(settings.hashLength, settings.markDirty);
            var profile = BuildProfile.GetActiveBuildProfile();

            info.EditorStamp(
                PlayerSettings.bundleVersion,
                git.Hash,
                git.IsDirty,
                settings.prefix,
                settings.postfix,
                profile != null ? profile.name : "",
                target.ToString(),
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            Save(info);

            // Упавший билд не вызывает postprocess — откатим на первом тике после билда.
            EditorApplication.update -= WaitBuildEnd;
            EditorApplication.update += WaitBuildEnd;

            Debug.Log($"[AppCore] Штамп билда: {info.FullVersion}");
        }

        public static void Revert()
        {
            var info = AppCoreAssets.FindBuildInfo();
            if (info == null || !info.IsStamped) return;
            info.EditorReset();
            Save(info);
        }

        private static void Save(BuildInfo info)
        {
            EditorUtility.SetDirty(info);
            AssetDatabase.SaveAssetIfDirty(info);
        }

        private static void WaitBuildEnd()
        {
            if (BuildPipeline.isBuildingPlayer) return;
            EditorApplication.update -= WaitBuildEnd;
            Revert();
        }
    }

    /// <summary>Хуки пайплайна сборки Unity.</summary>
    internal sealed class BuildInfoBuildHooks : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        // Рано: чтобы остальные препроцессоры уже видели проштампованный ассет.
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report) => BuildInfoStamp.Stamp(report.summary.platform);
        public void OnPostprocessBuild(BuildReport report) => BuildInfoStamp.Revert();
    }

    /// <summary>Значения BuildInfo в Play Mode редактора: живут в SessionState, не в ассете.</summary>
    internal static class BuildInfoPlayMode
    {
        private const string KeyOn = "Exerussus.AppCore.BuildInfo.On";
        private const string KeyVersion = "Exerussus.AppCore.BuildInfo.Version";
        private const string KeyHash = "Exerussus.AppCore.BuildInfo.Hash";
        private const string KeyDirty = "Exerussus.AppCore.BuildInfo.Dirty";
        private const string KeyProfile = "Exerussus.AppCore.BuildInfo.Profile";

        // Кэш на один домен; SessionState переживает перезагрузку домена при входе в Play Mode.
        private static BuildInfo.EditorValues? _cache;

        public static BuildInfo.EditorValues? Provide()
        {
            if (_cache.HasValue) return _cache;
            if (!SessionState.GetBool(KeyOn, false)) return null;

            _cache = new BuildInfo.EditorValues
            {
                Version = SessionState.GetString(KeyVersion, ""),
                CommitHash = SessionState.GetString(KeyHash, ""),
                IsDirty = SessionState.GetBool(KeyDirty, false),
                Profile = SessionState.GetString(KeyProfile, ""),
            };
            return _cache;
        }

        public static void OnPlayModeChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                {
                    var settings = BuildStampSettings.Load();
                    var git = GitInfo.Read(settings.hashLength, settings.markDirty);
                    var profile = BuildProfile.GetActiveBuildProfile();

                    SessionState.SetString(KeyVersion, PlayerSettings.bundleVersion);
                    SessionState.SetString(KeyHash, git.Hash);
                    SessionState.SetBool(KeyDirty, git.IsDirty);
                    SessionState.SetString(KeyProfile, profile != null ? profile.name : "editor");
                    SessionState.SetBool(KeyOn, true);
                    _cache = null;
                    break;
                }

                case PlayModeStateChange.EnteredEditMode:
                    SessionState.EraseBool(KeyOn);
                    SessionState.EraseString(KeyVersion);
                    SessionState.EraseString(KeyHash);
                    SessionState.EraseBool(KeyDirty);
                    SessionState.EraseString(KeyProfile);
                    _cache = null;
                    break;
            }
        }
    }

    [InitializeOnLoad]
    internal static class BuildInfoEditorBootstrap
    {
        static BuildInfoEditorBootstrap()
        {
            BuildInfo.EditorProvider = BuildInfoPlayMode.Provide;
            EditorApplication.playModeStateChanged -= BuildInfoPlayMode.OnPlayModeChanged;
            EditorApplication.playModeStateChanged += BuildInfoPlayMode.OnPlayModeChanged;
            EditorApplication.delayCall += EnsureEditorState;
        }

        // Ассет не проштампован вне билда (восстановление после сбоя билда/редактора).
        private static void EnsureEditorState()
        {
            if (BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += EnsureEditorState;
                return;
            }

            var info = AppCoreAssets.FindBuildInfo();
            if (info != null && info.IsStamped)
            {
                BuildInfoStamp.Revert();
                Debug.Log("[AppCore] BuildInfo был проштампован вне билда — откат к «editor-play-mode».");
            }
        }
    }
}
