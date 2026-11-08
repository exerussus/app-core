using System;
using System.Collections.Generic;
using UnityEngine;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>Сводка таблиц для полей выбора ключа — читается лениво, сбрасывается при открытии поиска и записи .loc.</summary>
    internal static class LocKeyCache
    {
        private static LocTable _merged;
        private static List<LocEntry> _sorted;
        private static Dictionary<string, int> _texts;

        public static LocTable Merged
        {
            get
            {
                if (_merged != null) return _merged;
                try { _merged = LocWorkspace.Load().Merged; }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    _merged = new LocTable();
                }

                return _merged;
            }
        }

        public static List<LocEntry> Sorted
        {
            get
            {
                if (_sorted != null) return _sorted;
                _sorted = new List<LocEntry>(Merged.Entries);
                _sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                return _sorted;
            }
        }

        /// <summary>Тот же текст основного языка у другого ключа — тогда в подписи нужен ключ.</summary>
        public static bool IsDuplicateText(string text, string key)
        {
            if (_texts == null)
            {
                _texts = new Dictionary<string, int>(StringComparer.Ordinal);
                var primary = Merged.Languages.Count > 0 ? Merged.Languages[0].Code : null;
                foreach (var entry in Merged.Entries)
                {
                    var t = primary != null ? entry.Find(primary)?.Text : null;
                    if (t != null) _texts[t] = _texts.TryGetValue(t, out var n) ? n + 1 : 1;
                }
            }

            return text != null && _texts.TryGetValue(text, out var count) && count > 1;
        }

        /// <summary>Пакет для нового ключа с префиксом: последний записываемый, где уже есть ключи префикса.</summary>
        public static LocWorkspace.File PackageFor(string prefix, LocWorkspace workspace)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                for (var i = workspace.Files.Count - 1; i >= 0; i--)
                {
                    var file = workspace.Files[i];
                    if (!file.Writable) continue;
                    foreach (var entry in file.Table.Entries)
                        if (entry.Key.StartsWith(prefix, StringComparison.Ordinal)) return file;
                }
            }

            return workspace.DefaultFile;
        }

        public static void Invalidate()
        {
            _merged = null;
            _sorted = null;
            _texts = null;
        }
    }
}
