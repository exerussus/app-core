using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>
    /// Ищет ключи в проекте: <c>^KEY</c> в вёрстке, коде, префабах, ассетах и сценах и <c>L.KEY</c> в коде.
    /// По нему видно ключи без использования и ключи, которых нет в таблице.
    /// </summary>
    /// <remarks>
    /// <c>^KEY</c> засчитывается только в начале значения: после кавычки, пробела, <c>=</c>, <c>,</c>,
    /// <c>;</c>, <c>|</c>, <c>&gt;</c> или переноса <c>&amp;#10;</c> — иначе в счёт попали бы степени
    /// вроде «10^{mass}» и XOR в коде. Символы ключа — те же, что допускает формат (<see cref="LocSlots.IsValidKey"/>).
    /// </remarks>
    public static class LocKeyScanner
    {
        private static readonly string[] Extensions = { ".uxml", ".cs", ".prefab", ".asset", ".unity" };

        // Деплой Nexus и сгенерированные константы ключей — не использования.
        private static readonly string[] SkippedFragments = { "/NexusStates/", "/Editor/NexusPlugins/", "/" + Path.GetFileName(AppCorePaths.LocKeysPath) };

        private static readonly Regex KeyPattern = new(@"(?<=[""'\s=,;|>]|&#10;)\^([A-Za-z][A-Za-z0-9_.\-]*[A-Za-z0-9_]|[A-Za-z])", RegexOptions.Compiled);
        private static readonly Regex ConstPattern = new(@"\bL\.([A-Z][A-Z0-9_]*)\b", RegexOptions.Compiled);

        /// <summary>Ключ → файлы (пути от корня проекта), где он встречается.</summary>
        public static Dictionary<string, List<string>> Scan()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var root = Application.dataPath;
            var projectRoot = Path.GetDirectoryName(root) ?? root;

            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (Array.IndexOf(Extensions, extension) < 0) continue;

                var relative = file.Substring(projectRoot.Length + 1).Replace('\\', '/');
                if (IsSkipped(relative)) continue;

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                if (text.IndexOf('^') >= 0)
                    foreach (Match match in KeyPattern.Matches(text))
                        Add(result, match.Groups[1].Value, relative);

                if (extension == ".cs" && text.IndexOf("L.", StringComparison.Ordinal) >= 0)
                    foreach (Match match in ConstPattern.Matches(text))
                        Add(result, match.Groups[1].Value, relative);
            }

            return result;
        }

        private static void Add(Dictionary<string, List<string>> result, string key, string file)
        {
            if (!result.TryGetValue(key, out var files)) result[key] = files = new List<string>();
            if (!files.Contains(file)) files.Add(file);
        }

        private static bool IsSkipped(string path)
        {
            foreach (var fragment in SkippedFragments)
                if (path.IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            return false;
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

            /// <summary>Есть в таблице, а нигде не используются (кроме динамических).</summary>
            public readonly List<string> Unused = new();

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

        public static Report Check(LocTable table, LocSettings settings)
        {
            var report = new Report();
            var usages = Scan();

            foreach (var pair in usages)
            {
                if (table.Contains(pair.Key) || IsBuiltin(pair.Key) || IsDynamic(pair.Key, settings)) continue;
                report.Unknown[pair.Key] = pair.Value;
            }

            foreach (var entry in table.Entries)
                if (!usages.ContainsKey(entry.Key) && !IsDynamic(entry.Key, settings))
                    report.Unused.Add(entry.Key);

            report.Unused.Sort(StringComparer.Ordinal);
            return report;
        }
    }
}
