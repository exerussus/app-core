using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
#endif

namespace App.Services.Navigator
{
    /// <summary>
    /// Источник истины по id страниц и попапов приложения.
    /// Заменяет старый <c>DataBase.Pages</c> / <c>DataBase.Popups</c> — теперь
    /// реестр живёт в коре (app.core), без зависимости от пакета db.
    ///
    /// Редактор-плагин (Nexus → App) читает и перезаписывает эти списки на Apply.
    /// Навигационные связи (className → target) по-прежнему пишутся в
    /// <see cref="NavigationSetting"/> — этот SO держит только сами id и кормит
    /// ими выделенный дропдаун (<c>NavigationDropdownDrawer</c>).
    /// </summary>
    [CreateAssetMenu(fileName = "NavigationData", menuName = "App/Navigation Data")]
    public class NavigationData : ScriptableObject
    {
        // Имя ассета в Resources (без расширения). Должно совпадать с тем, что
        // генерит плагин при скаффолде. Менять — только синхронно с конфигом плагина.
        public const string ResourceName = "NavigationData";

        [SerializeField] private List<string> pages  = new();
        [SerializeField] private List<string> popups = new();

        /// <summary>Зарегистрированные id страниц (источник <see cref="App.Core.PageUID"/>).</summary>
        public IReadOnlyList<string> Pages => pages;

        /// <summary>Зарегистрированные id попапов (источник <see cref="App.Core.PopupUID"/>).</summary>
        public IReadOnlyList<string> Popups => popups;

        private static NavigationData _instance;

        /// <summary>
        /// Рантайм-доступ: грузит ассет из Resources один раз и кеширует.
        /// Может вернуть null, если ассет ещё не сгенерирован.
        /// </summary>
        public static NavigationData Instance =>
            _instance != null ? _instance : _instance = Resources.Load<NavigationData>(ResourceName);

#if UNITY_EDITOR
        // ---- редактор-only точки мутации: плагин на Apply пересобирает списки целиком ----

        public void EditorSetPages(IEnumerable<string> ids)
        {
            pages.Clear();
            pages.AddRange(ids);
        }

        public void EditorSetPopups(IEnumerable<string> ids)
        {
            popups.Clear();
            popups.AddRange(ids);
        }

        // ---- источники значений для выделенного дропдауна (NavigationDropdownDrawer) ----
        // Ищем ассет по типу, не завязываясь на путь (путь к Assets/App настраивается в плагине).

        public static IEnumerable<string> EditorPageIds()  => LoadEditor()?.pages  ?? Enumerable.Empty<string>();
        public static IEnumerable<string> EditorPopupIds() => LoadEditor()?.popups ?? Enumerable.Empty<string>();

        private static NavigationData LoadEditor()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(NavigationData));
            if (guids.Length == 0) return null;
            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<NavigationData>(path);
        }
#endif
    }
}

// Самостоятельные dropdown-атрибуты (НЕ наследники BaseDropdownAttribute — иначе их
// перехватит и общий BaseDropdownDrawer). Глобальный namespace — как у [AudiosDropdown],
// чтобы вешались на поле без using. Рисует их только NavigationDropdownDrawer.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class PagesDropdownAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class PopupsDropdownAttribute : Attribute { }