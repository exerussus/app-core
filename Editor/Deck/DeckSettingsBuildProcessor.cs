using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Exerussus.AppCore.Deck.Editor
{
    /// <summary>
    /// Перед каждой сборкой кладёт настройки AppDeck в Preloaded Assets того, что собирается.
    ///
    /// У профиля сборки, переопределяющего Player Settings, свой список Preloaded Assets, и глобальный
    /// он не читает. Пока профиль активен (а собирается всегда активный — и из редактора, и с
    /// -activeBuildProfile), PlayerSettings API работает с его списком, поэтому проверка здесь
    /// попадает ровно в собираемый профиль. Без этого сборка через профиль не видит AppDeckSettings
    /// и AppDeck молча работает на умолчаниях.
    ///
    /// После сборки ссылка НЕ убирается: профиль меняется один раз (строка в его .asset — закоммитить),
    /// дальше сборки ничего не трогают и рабочее дерево остаётся чистым.
    /// </summary>
    internal sealed class DeckSettingsBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = AssetDatabase.LoadAssetAtPath<DeckSettings>(DeckSettings.AssetPath);
            if (settings == null) return;   // AppCore не развёрнут или AppDeck не настроен — умолчания

            if (!DeckSettingsBootstrap.EnsurePreloaded(settings)) return;

            BuildProfile profile = BuildProfile.GetActiveBuildProfile();
            string where = profile != null ? $"профиль сборки «{profile.name}»" : "Player Settings";
            Debug.Log($"[AppDeck] Настройки добавлены в Preloaded Assets ({where}) — закоммитьте изменённый файл.");
        }
    }
}
