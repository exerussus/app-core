using System.Collections.Generic;
using System.Linq;
using App.Core;
using Exerussus.Payloads;
using UnityEngine;

namespace App.Services.Navigator
{
    internal static class NavigationLink
    {
        private static readonly Dictionary<string, PageUID> _linksPages = new();
        private static readonly Dictionary<string, PopupUID> _linksPopups = new();
        
        public static readonly long NavigateToKey = Payload.Uid("navigate-to");

        public static IReadOnlyDictionary<string, PageUID> LinksPages => _linksPages;
        public static IReadOnlyDictionary<string, PopupUID> LinksPopups => _linksPopups;

        public static void Initialize(NavigationSetting navigationSetting)
        {
            _linksPages.Clear();
            _linksPopups.Clear();

            if (navigationSetting == null) return;
            
            foreach (var entry in navigationSetting.Entries)
            {
                if (!entry.Bound) continue;
                if (string.IsNullOrEmpty(entry.ClassName)) continue;

                // One-to-one is enforced by the editor; last write wins as a safety net.
                if (entry.Kind == NavigationSetting.EntryKind.Page) _linksPages.Add(entry.ClassName, new PageUID(entry.Page));
                else if (entry.Kind == NavigationSetting.EntryKind.Popup) _linksPopups.Add(entry.ClassName, new PopupUID(entry.Page));
            }
        }
        
#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterDebug()
        {
            Payload.RegisterDebugFormatter(NavigateToKey, v =>
            {
                if (v == 0) return "invalid";
                if (v > 0) return PageUID.FromRaw(v).ToString();
                return PopupUID.FromRaw(v).ToString();
            });
        }
#endif
    }
}
