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

        public static IReadOnlyDictionary<string, PageId> LinksPages => _linksPages;
        public static IReadOnlyDictionary<string, PopupId> LinksPopups => _linksPopups;

        public static void Initialize(NavigationSettings navigationSettings)
        {
            _linksPages.Clear();
            _linksPopups.Clear();

            if (navigationSettings == null) return;
            
            foreach (var entry in navigationSettings.Entries)
            {
                if (!entry.Bound) continue;
                if (string.IsNullOrEmpty(entry.ClassName)) continue;

                // One-to-one is enforced by the editor; last write wins as a safety net.
                if (entry.Kind == NavigationSettings.EntryKind.Page) _linksPages.Add(entry.ClassName, new PageId(entry.Page));
                else if (entry.Kind == NavigationSettings.EntryKind.Popup) _linksPopups.Add(entry.ClassName, new PopupId(entry.Page));
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
