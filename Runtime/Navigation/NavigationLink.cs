using System.Collections.Generic;
using Exerussus.Payloads;
using UnityEngine;

namespace Exerussus.AppCore.Navigation
{
    internal static class NavigationLink
    {
        private static readonly Dictionary<string, PageId> _linksPages = new();
        private static readonly Dictionary<string, PopupId> _linksPopups = new();
        
        public static readonly long NavigateToKey = Payload.Uid("navigate-to");

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
                // а не Add: дубликат класса — повод перезаписать, а не уронить приложение
                // на старте (Add бросил бы исключение вопреки задуманной страховке).
                if (entry.Kind == NavigationSettings.EntryKind.Page) _linksPages[entry.ClassName] = new PageId(entry.Page);
                else if (entry.Kind == NavigationSettings.EntryKind.Popup) _linksPopups[entry.ClassName] = new PopupId(entry.Page);
            }
        }
        
#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterDebug()
        {
            Payload.RegisterDebugFormatter(NavigateToKey, v =>
            {
                if (v == 0) return "invalid";
                if (v > 0) return PageId.FromRaw(v).ToString();
                return PopupId.FromRaw(v).ToString();
            });
        }
#endif
    }
}
