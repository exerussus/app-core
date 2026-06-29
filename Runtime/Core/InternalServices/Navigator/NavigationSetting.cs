using System;
using System.Collections.Generic;
using UnityEngine;

namespace App.Services.Navigator
{
    public class NavigationSetting : ScriptableObject
    {
        [Serializable]
        public struct NavigationEntry
        {
            public string ClassName;
            public bool Bound;
            public string  Page;
            public EntryKind Kind;
        }

        [SerializeField] private List<NavigationEntry> entries = new();
        public enum EntryKind { Page, Popup }
        public IReadOnlyList<NavigationEntry> Entries => entries;

#if UNITY_EDITOR
        // Editor-only mutation entry point. The Navigation page (Project Hub)
        // rebuilds the whole list on Apply.
        public void EditorSetEntries(IEnumerable<NavigationEntry> newEntries)
        {
            entries.Clear();
            entries.AddRange(newEntries);
        }
#endif
    }
}
