using System;
using System.Collections.Generic;
using UnityEngine;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Navigation
{
    /// <summary>
    /// Единственный реестр навигации приложения: id страниц, попапов и фрагментов плюс
    /// навигационные связи «USS-класс → цель».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Экземпляр ровно один и лежит строго по пути <see cref="AppCorePaths.NavigationSettingsPath"/>.
    /// <c>AppRunner</c> получает на него ссылку автоматически (редактор проставляет поле сам),
    /// поэтому ни искать, ни назначать ассет руками не нужно. Второй экземпляр в проекте —
    /// ошибка валидатора.
    /// </para>
    /// <para>
    /// Списки id переписывает страница App в Nexus на Apply. Из них же генерируются
    /// <c>NavTargets</c> (PageId/PopupId/FragmentId) и выпадающие списки в инспекторах вью.
    /// </para>
    /// </remarks>
    public class NavigationSettings : ScriptableObject
    {
        [Serializable]
        public struct NavigationEntry
        {
            public string ClassName;
            public bool Bound;
            public string Page;
            public EntryKind Kind;
        }

        public enum EntryKind { Page, Popup }

        [SerializeField] private List<string> pages = new();
        [SerializeField] private List<string> popups = new();
        [SerializeField] private List<string> fragments = new();

        [SerializeField] private List<NavigationEntry> entries = new();

        // Аффиксы навигационных классов. Читает и пишет страница App в Nexus; пусто — дефолт.
        [SerializeField] private string prefix = "to-";
        [SerializeField] private string postfix = "__navigation";
        [SerializeField] private string popupPrefix = "open-";
        [SerializeField] private string popupPostfix = "__navigation";

        public IReadOnlyList<NavigationEntry> Entries => entries;

        public IReadOnlyList<string> Pages => pages;
        public IReadOnlyList<string> Popups => popups;
        public IReadOnlyList<string> Fragments => fragments;

        /// <summary>Зарегистрированные id указанного вида.</summary>
        public IReadOnlyList<string> GetIds(ViewKind kind) => kind switch
        {
            ViewKind.Page => pages,
            ViewKind.Popup => popups,
            ViewKind.Fragment => fragments,
            _ => Array.Empty<string>(),
        };

        public string PagePrefix => prefix;
        public string PagePostfix => postfix;
        public string PopupPrefix => popupPrefix;
        public string PopupPostfix => popupPostfix;

#if UNITY_EDITOR
        // Редакторные точки мутации: страница App в Nexus пересобирает списки целиком на Apply.

        public void EditorSetEntries(IEnumerable<NavigationEntry> newEntries)
        {
            entries.Clear();
            entries.AddRange(newEntries);
        }

        public void EditorSetIds(ViewKind kind, IEnumerable<string> ids)
        {
            var list = kind switch
            {
                ViewKind.Page => pages,
                ViewKind.Popup => popups,
                _ => fragments,
            };

            list.Clear();
            list.AddRange(ids);
        }

        public void EditorSetAffixes(ViewKind kind, string newPrefix, string newPostfix)
        {
            if (kind == ViewKind.Popup)
            {
                popupPrefix = newPrefix ?? string.Empty;
                popupPostfix = newPostfix ?? string.Empty;
            }
            else
            {
                prefix = newPrefix ?? string.Empty;
                postfix = newPostfix ?? string.Empty;
            }
        }
#endif
    }
}
