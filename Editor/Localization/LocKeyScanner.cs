using System;
using System.Collections.Generic;
using System.Text;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>
    /// Сверка проекта с таблицами: ключи без использования и ключи, которых нет ни в базе, ни в пакетах.
    /// Поиск употреблений — <see cref="LocUsages"/> («^KEY», <c>L.KEY</c>, шаблоны настроек, провайдеры).
    /// </summary>
    public static class LocKeyScanner
    {
        /// <summary>Ключ → файлы (пути от корня проекта), где он встречается.</summary>
        public static Dictionary<string, List<string>> Scan() => ToFiles(LocUsages.Scan());

        /// <summary>Ключ → файлы по готовому индексу.</summary>
        public static Dictionary<string, List<string>> ToFiles(LocUsageIndex index)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var key in index.Keys)
            {
                var files = new List<string>();
                foreach (var usage in index.Of(key))
                    if (!files.Contains(usage.File)) files.Add(usage.File);
                result[key] = files;
            }

            return result;
        }

        /// <summary>Ключ собирается в коде (<see cref="LocSettings.DynamicPrefixes"/>).</summary>
        public static bool IsDynamic(string key, LocSettings settings)
        {
            if (settings == null) return false;
            foreach (var prefix in settings.DynamicPrefixes)
                if (!string.IsNullOrEmpty(prefix) && key.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool IsBuiltin(string key) => key.StartsWith("APPCORE_", StringComparison.Ordinal);

        /// <summary>Итог сверки проекта с таблицей.</summary>
        public sealed class Report
        {
            /// <summary>Используются, а в таблице нет: ключ → файлы.</summary>
            public readonly SortedDictionary<string, List<string>> Unknown = new(StringComparer.Ordinal);

            /// <summary>Есть в таблице, а нигде не используются (<see cref="LocUsageIndex.IsUsed"/>).</summary>
            public readonly List<string> Unused = new();

            /// <summary>Индекс полный. false — обход прерывали: «нет употреблений» по нему ничего не значит.</summary>
            public bool Complete = true;

            public string Describe(int max)
            {
                var sb = new StringBuilder();
                var shown = 0;
                foreach (var pair in Unknown)
                {
                    if (shown++ >= max)
                    {
                        sb.Append($"… и ещё {Unknown.Count - max}\n");
                        break;
                    }

                    sb.Append(pair.Key).Append(" — ").Append(string.Join(", ", pair.Value)).Append('\n');
                }

                return sb.ToString();
            }
        }

        /// <summary>Сверка таблицы (обычно сводной — <see cref="LocEditor.ReadMerged"/>) с проектом.</summary>
        public static Report Check(LocTable table, LocSettings settings, LocUsageIndex index = null)
        {
            var keys = new HashSet<string>(LocEditor.BuiltinKeys, StringComparer.Ordinal);
            foreach (var entry in table.Entries) keys.Add(entry.Key);

            index ??= LocUsages.Scan(settings, keys);
            return Check(table, settings, ToFiles(index), index);
        }

        private static Report Check(LocTable table, LocSettings settings, Dictionary<string, List<string>> usages, LocUsageIndex index)
        {
            var report = new Report { Complete = index.Complete };

            // Правила «употреблён / неизвестен» — одни для сверки, таблицы на странице и CLI: LocUsageIndex.
            foreach (var key in index.Unknown(table, settings)) report.Unknown[key] = usages[key];
            report.Unused.AddRange(index.Unused(table, settings));
            return report;
        }
    }
}
