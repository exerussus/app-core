using System.Collections.Generic;

namespace Exerussus.AppCore.Navigation
{
    /// <summary>
    /// Рантайм-таблица навигационных классов: «USS-класс кнопки → страница или попап».
    /// Собирается один раз из <see cref="NavigationSettings"/> при старте <see cref="AppRunner"/>.
    /// </summary>
    internal static class NavigationLink
    {
        /// <summary>Класс «назад»: возврат на предыдущую страницу (или закрытие попапа).</summary>
        public const string BackClass = "to-back-page__navigation";

        private static readonly Dictionary<string, PageId> _linksPages = new();
        private static readonly Dictionary<string, PopupId> _linksPopups = new();

        // Отдаём конкретный Dictionary, а не IReadOnlyDictionary: через интерфейс foreach
        // боксит структурный энумератор, а эти словари обходятся на каждую кнопку каждой
        // страницы. Тип internal, так что защита от записи здесь стоит дешевле аллокаций.
        public static Dictionary<string, PageId> LinksPages => _linksPages;
        public static Dictionary<string, PopupId> LinksPopups => _linksPopups;

        public static void Initialize(NavigationSettings navigationSettings)
        {
            _linksPages.Clear();
            _linksPopups.Clear();

            if (navigationSettings == null) return;

            // Обход по индексу: Entries отдаётся как IReadOnlyList, и foreach по нему
            // забоксил бы энумератор.
            var entries = navigationSettings.Entries;

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!entry.Bound) continue;
                if (string.IsNullOrEmpty(entry.ClassName)) continue;

                // Один-к-одному гарантирует редактор. Здесь присваивание по индексатору,
                // а не Add: дубликат класса — повод перезаписать, а не уронить приложение.
                if (entry.Kind == NavigationSettings.EntryKind.Page) _linksPages[entry.ClassName] = new PageId(entry.Page);
                else if (entry.Kind == NavigationSettings.EntryKind.Popup) _linksPopups[entry.ClassName] = new PopupId(entry.Page);
            }
        }
    }
}
