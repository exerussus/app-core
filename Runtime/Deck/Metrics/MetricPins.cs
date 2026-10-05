using System;
using System.Collections.Generic;
using UnityEngine;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Какие метрики закреплены в мини-HUD — по имени, чтобы закрепление переживало перезапуск
    /// и перерегистрацию метрики. Хранится в PlayerPrefs.
    /// </summary>
    internal static class MetricPins
    {
        private const string PrefsKey = "appdeck.metrics.pins";

        private static readonly HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        public static int Version { get; private set; }

        public static bool IsPinned(string name)
        {
            Load();
            return name != null && _names.Contains(name);
        }

        public static void Set(string name, bool pinned)
        {
            Load();
            bool changed = pinned ? _names.Add(name) : _names.Remove(name);
            if (!changed) return;

            Version++;
            PlayerPrefs.SetString(PrefsKey, string.Join("\n", _names));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _names.Clear();
            _loaded = false;
            Version++;
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            string saved = PlayerPrefs.GetString(PrefsKey, "fps");
            foreach (string name in saved.Split('\n'))
            {
                if (name.Length > 0) _names.Add(name);
            }
        }
    }
}
