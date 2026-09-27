#if UNITY_EDITOR
using UnityEditor;
using Exerussus.AppCore.Build;
using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Автоматическая привязка единственных ассетов AppCore. Поля не назначаются руками:
    /// раннер сам берёт их по фиксированным путям <see cref="AppCorePaths"/>.
    /// </summary>
    public partial class AppRunner
    {
        private void Reset() => EditorAutoAssign();

        private void OnValidate() => EditorAutoAssign();

        /// <summary>Проставляет ссылки на ассеты из <c>Assets/App/Settings</c>. Ничего не создаёт.</summary>
        internal void EditorAutoAssign()
        {
            var changed = false;

            var nav = AssetDatabase.LoadAssetAtPath<NavigationSettings>(AppCorePaths.NavigationSettingsPath);
            if (nav != null && navigationSettings != nav)
            {
                navigationSettings = nav;
                changed = true;
            }

            var info = AssetDatabase.LoadAssetAtPath<BuildInfo>(AppCorePaths.BuildInfoPath);
            if (info != null && buildInfo != info)
            {
                buildInfo = info;
                changed = true;
            }

            if (changed) EditorUtility.SetDirty(this);
        }
    }
}
#endif
