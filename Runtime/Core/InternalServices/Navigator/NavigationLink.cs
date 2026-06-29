using System.Collections.Generic;
using App.Core;
using App.UIToolkit.Manipulators;
using Exerussus.Payloads;
using UnityEngine;
using UnityEngine.UIElements;

namespace App.Services.Navigator
{
    internal static class NavigationLink
    {
        private static readonly Dictionary<string, PageUID> _links = new();
        
        public static readonly long NavigateToKey = Payload.Uid("navigate-to");

        public static IReadOnlyDictionary<string, PageUID> Links => _links;

        public static void Initialize(NavigationSetting navigationSetting)
        {
            _links.Clear();

            if (navigationSetting == null)
                return;

            foreach (var entry in navigationSetting.Entries)
            {
                if (!entry.Bound) continue;
                if (string.IsNullOrEmpty(entry.ClassName)) continue;

                // One-to-one is enforced by the editor; last write wins as a safety net.
                _links[entry.ClassName] = new PageUID(entry.Page);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterFactory()
        {
            UiSignal.AddPayloadFactory(OnBuildPayload);
        }
        
#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterDebug() => Payload.RegisterDebugFormatter(NavigateToKey, v => PageUID.FromRaw(v).ToString());
#endif
        
        private static void OnBuildPayload(VisualElement visualElement, Payload payload)
        {
            foreach (var (className, pageUid) in _links)
            {
                if (visualElement.ClassListContains(className))
                {
                    payload.Set(NavigateToKey, pageUid.Id);
                }
            }
        }
    }
}
