using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Exerussus.AppCore.Localization;
using Debug = UnityEngine.Debug;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>Одно употребление ключа в проекте.</summary>
    public sealed class LocUsage
    {
        public string Key;

        /// <summary>Путь от корня проекта через «/».</summary>
        public string File;

        /// <summary>Строка (с 1) и столбец (с 1).</summary>
        public int Line;
        public int Column;

        /// <summary>Чем найдено: «^», «L», имя шаблона или провайдера.</summary>
        public string Kind;

        /// <summary>Строка файла вокруг употребления.</summary>
        public string Snippet;

        public override string ToString() => $"{File}:{Line}";
    }

    /// <summary>
    /// Употребления, которые не взять регулярным выражением (константы, собранные генератором,
    /// данные в своём формате). Реализация — класс с конструктором без параметров в редакторской
    /// сборке проекта, находится сам.
    /// </summary>
    public interface ILocUsageProvider
    {
        string Name { get; }

        /// <summary>Сложить употребления. <paramref name="knownKeys"/> — ключи таблиц (для сопоставления констант).</summary>
        void Collect(ICollection<string> knownKeys, List<LocUsage> into);

        /// <summary>Переименовать ключ в своих употреблениях. Пути изменённых файлов — в <paramref name="changed"/>.</summary>
        void Rename(string from, string to, List<string> changed, List<string> errors);
    }

    /// <summary>Употребления всех ключей — итог одного обхода проекта.</summary>
    public sealed class LocUsageIndex
    {
        private readonly Dictionary<string, List<LocUsage>> _byKey = new(StringComparer.Ordinal);
        private static readonly List<LocUsage> Empty = new();

        public int FilesScanned { get; internal set; }
        public double Seconds { get; internal set; }
        public DateTime Time { get; internal set; } = DateTime.Now;

        public IEnumerable<string> Keys => _byKey.Keys;

        public bool Contains(string key) => key != null && _byKey.ContainsKey(key);

        public IReadOnlyList<LocUsage> Of(string key) => key != null && _byKey.TryGetValue(key, out var list) ? list : Empty;

        /// <summary>Сколько разных файлов.</summary>
        public int FileCount(string key)
        {
            var list = Of(key);
            if (list.Count < 2) return list.Count;

            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var usage in list) files.Add(usage.File);
            return files.Count;
        }

        internal void Add(LocUsage usage)
        {
            if (!_byKey.TryGetValue(usage.Key, out var list)) _byKey[usage.Key] = list = new List<LocUsage>(1);
            list.Add(usage);
        }
    }

    /// <summary>
    /// Поиск употреблений ключей в проекте и переименование в них. Встроенные записи — «^KEY» в
    /// начале значения и константа <c>L.KEY</c>; ключи без «^» — шаблоны из настроек
    /// (<see cref="LocSettings.UsagePatterns"/>) и провайдеры проекта (<see cref="ILocUsageProvider"/>).
    /// </summary>
    /// <remarks>
    /// «^KEY» засчитывается только в начале значения: после кавычки, пробела, <c>=</c>, <c>,</c>,
    /// <c>;</c>, <c>|</c>, <c>&gt;</c> или переноса <c>&amp;#10;</c> — иначе в счёт попали бы степени
    /// вроде «10^{mass}» и XOR в коде. Константа <c>L.X</c> засчитывается только ключу, чьё имя константы
    /// однозначно (у «a.b» и «a_b» константа одна — такую не переименовываем).
    /// Файлы не в UTF-8 при переименовании не трогаются; BOM и переводы строк сохраняются.
    /// </remarks>
    public static class LocUsages
    {
        public const string CaretKind = "^";
        public const string ConstKind = "L";

        private const long MaxFileSize = 32L * 1024 * 1024;

        // Деплой Nexus, кэши и сгенерированные константы — не употребления.
        private static readonly string[] SkippedFragments = { "/NexusStates/", "/Editor/NexusPlugins/", "/" + Path.GetFileName(AppCorePaths.LocKeysPath) };

        internal static readonly Regex CaretPattern = new(@"(?<=[""'\s=,;|>]|&#10;)\^([A-Za-z][A-Za-z0-9_.\-]*[A-Za-z0-9_]|[A-Za-z])", RegexOptions.Compiled);
        internal static readonly Regex ConstPattern = new(@"\bL\.([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Compiled);

        private static List<ILocUsageProvider> _providers;

        public static IReadOnlyList<ILocUsageProvider> Providers
        {
            get
            {
                if (_providers != null) return _providers;

                _providers = new List<ILocUsageProvider>();
                foreach (var type in TypeCache.GetTypesDerivedFrom<ILocUsageProvider>())
                {
                    if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null) continue;

                    try { _providers.Add((ILocUsageProvider)Activator.CreateInstance(type)); }
                    catch (Exception e) { Debug.LogException(e); }
                }

                return _providers;
            }
        }

        [InitializeOnLoadMethod]
        private static void ResetProviders() => _providers = null;

        // ================================================================ правила

        /// <summary>Правило поиска, собранное из настроек: регулярка, где искать, как переименовывать.</summary>
        private sealed class Rule
        {
            public string Kind;
            public Regex Regex;
            public int GroupIndex;
            public string GroupName;
            public HashSet<string> Extensions;
            public string[] Folders;
            public LocUsageReplace Replace;
            public bool IsConst;

            public bool Applies(string relative, string extension)
            {
                if (!Extensions.Contains(extension)) return false;
                if (Folders.Length == 0) return true;
                foreach (var folder in Folders)
                    if (relative.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            public Group KeyGroup(Match match) => GroupName != null ? match.Groups[GroupName] : match.Groups[GroupIndex];
        }

        private static List<Rule> Rules(LocSettings settings, List<string> errors)
        {
            var rules = new List<Rule>();
            var extensions = settings != null ? settings.UsageExtensions : new[] { ".uxml", ".cs", ".prefab", ".asset", ".unity" };

            rules.Add(new Rule
            {
                Kind = CaretKind, Regex = CaretPattern, GroupIndex = 1, Extensions = ExtensionSet(extensions),
                Folders = new[] { "Assets/" }, Replace = LocUsageReplace.Key,
            });

            rules.Add(new Rule
            {
                Kind = ConstKind, Regex = ConstPattern, GroupIndex = 1, Extensions = ExtensionSet(new[] { ".cs" }),
                Folders = new[] { "Assets/" }, Replace = LocUsageReplace.Identifier, IsConst = true,
            });

            if (settings == null) return rules;

            foreach (var pattern in settings.UsagePatterns)
            {
                if (pattern == null || string.IsNullOrWhiteSpace(pattern.regex)) continue;

                Regex regex;
                try { regex = new Regex(pattern.regex, RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant); }
                catch (ArgumentException e)
                {
                    errors?.Add($"шаблон «{pattern.name}»: {e.Message}");
                    continue;
                }

                var named = Array.IndexOf(regex.GetGroupNames(), "key") >= 0;
                if (!named && regex.GetGroupNumbers().Length < 2)
                {
                    errors?.Add($"шаблон «{pattern.name}»: нет группы ключа — (?<key>…) или (…)");
                    continue;
                }

                rules.Add(new Rule
                {
                    Kind = string.IsNullOrWhiteSpace(pattern.name) ? pattern.regex : pattern.name,
                    Regex = regex,
                    GroupIndex = 1,
                    GroupName = named ? "key" : null,
                    Extensions = ExtensionSet(Split(pattern.extensions, ".cs")),
                    Folders = Folders(Split(pattern.folders, null)),
                    Replace = pattern.replace,
                });
            }

            return rules;
        }

        private static string[] Split(string text, string fallback)
        {
            var parts = new List<string>();
            foreach (var part in (text ?? string.Empty).Split(';', ',', '\n'))
                if (!string.IsNullOrWhiteSpace(part)) parts.Add(part.Trim());
            if (parts.Count == 0 && fallback != null) parts.Add(fallback);
            return parts.ToArray();
        }

        private static string[] Folders(string[] folders)
        {
            if (folders.Length == 0) return new[] { "Assets/" };
            for (var i = 0; i < folders.Length; i++)
                folders[i] = folders[i].Replace('\\', '/').TrimStart('/').TrimEnd('/') + "/";
            return folders;
        }

        private static HashSet<string> ExtensionSet(IEnumerable<string> extensions)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in extensions)
            {
                if (string.IsNullOrWhiteSpace(extension)) continue;
                var e = extension.Trim();
                set.Add(e[0] == '.' ? e : "." + e);
            }

            return set;
        }

        // ================================================================ обход

        /// <summary>
        /// Все употребления одним обходом. <paramref name="knownKeys"/> нужны для констант <c>L.X</c>
        /// (по ним имя константы сводится к ключу); <c>null</c> — ключи таблиц каталога.
        /// <paramref name="progress"/>: доля 0…1 → true, чтобы прервать.
        /// </summary>
        public static LocUsageIndex Scan(LocSettings settings = null, ICollection<string> knownKeys = null,
                                         Func<float, bool> progress = null, List<string> errors = null)
        {
            settings ??= LocSettings.Current;
            knownKeys ??= LocEditor.ReadAllKeys();

            var watch = Stopwatch.StartNew();
            var index = new LocUsageIndex();
            var rules = Rules(settings, errors);
            var constants = ConstantMap(knownKeys);
            var excludes = settings != null ? settings.UsageExcludes : Array.Empty<string>();

            var files = Files(rules, excludes);
            for (var i = 0; i < files.Count; i++)
            {
                if (progress != null && (i & 63) == 0 && progress(files.Count == 0 ? 1f : (float)i / files.Count)) break;

                var (full, relative) = files[i];
                string text;
                try
                {
                    if (new FileInfo(full).Length > MaxFileSize) continue;
                    text = System.IO.File.ReadAllText(full);
                }
                catch (Exception)
                {
                    continue;
                }

                index.FilesScanned++;
                ScanText(text, relative, rules, constants, index.Add);
            }

            foreach (var provider in Providers)
            {
                var list = new List<LocUsage>();
                try { provider.Collect(knownKeys, list); }
                catch (Exception e)
                {
                    errors?.Add($"провайдер {provider.Name}: {e.Message}");
                    Debug.LogException(e);
                }

                foreach (var usage in list)
                {
                    if (usage == null || string.IsNullOrEmpty(usage.Key)) continue;
                    usage.Kind ??= provider.Name;
                    index.Add(usage);
                }
            }

            index.Seconds = watch.Elapsed.TotalSeconds;
            index.Time = DateTime.Now;
            return index;
        }

        /// <summary>Употребления одного ключа (тот же обход, но собирается только он).</summary>
        public static IReadOnlyList<LocUsage> Find(string key, LocSettings settings = null)
        {
            var index = Scan(settings);
            return index.Of(Loc.KeyOf(key));
        }

        private static List<(string full, string relative)> Files(List<Rule> rules, string[] excludes)
        {
            var root = LocPackageCatalog.ProjectRoot;
            var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                foreach (var folder in rule.Folders) folders.Add(folder);
                extensions.UnionWith(rule.Extensions);
            }

            // вложенные папки не обходим дважды
            var roots = new List<string>();
            foreach (var folder in folders)
                if (!roots.Exists(r => folder.StartsWith(r, StringComparison.OrdinalIgnoreCase))) roots.Add(folder);

            var list = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in roots)
            {
                var dir = root + "/" + folder.TrimEnd('/');
                if (!Directory.Exists(dir)) continue;

                foreach (var full in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(full);
                    if (!extensions.Contains(extension)) continue;

                    var relative = full.Substring(root.Length + 1).Replace('\\', '/');
                    if (IsSkipped(relative, excludes) || !seen.Add(relative)) continue;
                    list.Add((full, relative));
                }
            }

            return list;
        }

        private static bool IsSkipped(string path, string[] excludes)
        {
            foreach (var fragment in SkippedFragments)
                if (path.IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            foreach (var fragment in excludes)
                if (!string.IsNullOrEmpty(fragment) && path.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Имя константы → ключ; имена, общие для двух ключей, — null (неоднозначны).</summary>
        private static Dictionary<string, string> ConstantMap(ICollection<string> keys)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (keys == null) return map;

            foreach (var key in keys)
            {
                var name = LocEditor.IdentifierOf(key);
                map[name] = map.ContainsKey(name) && map[name] != key ? null : key;
            }

            return map;
        }

        private static void ScanText(string text, string relative, List<Rule> rules, Dictionary<string, string> constants, Action<LocUsage> add)
        {
            var extension = Path.GetExtension(relative);
            List<int> lines = null;

            foreach (var rule in rules)
            {
                if (!rule.Applies(relative, extension)) continue;
                if (rule.Kind == CaretKind && text.IndexOf('^') < 0) continue;
                if (rule.IsConst && text.IndexOf("L.", StringComparison.Ordinal) < 0) continue;

                foreach (Match match in rule.Regex.Matches(text))
                {
                    var group = rule.KeyGroup(match);
                    if (!group.Success) continue;

                    var key = group.Value;
                    if (rule.IsConst)
                    {
                        if (!constants.TryGetValue(key, out key) || key == null) continue;
                    }
                    else if (!LocSlots.IsValidKey(key)) continue;

                    lines ??= LineStarts(text);
                    var line = LineOf(lines, group.Index);
                    add(new LocUsage
                    {
                        Key = key, File = relative, Kind = rule.Kind,
                        Line = line + 1, Column = group.Index - lines[line] + 1,
                        Snippet = SnippetOf(text, lines, line),
                    });
                }
            }
        }

        private static List<int> LineStarts(string text)
        {
            var list = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n') list.Add(i + 1);
            return list;
        }

        private static int LineOf(List<int> starts, int index)
        {
            var lo = 0;
            var hi = starts.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) >> 1;
                if (starts[mid] <= index) lo = mid;
                else hi = mid - 1;
            }

            return lo;
        }

        private static string SnippetOf(string text, List<int> starts, int line)
        {
            var start = starts[line];
            var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
            var snippet = text.Substring(start, Math.Max(0, end - start)).Trim();
            return snippet.Length > 160 ? snippet.Substring(0, 160) + "…" : snippet;
        }

        // ================================================================ переименование

        /// <summary>Одна замена: файл, строка, как было и как станет.</summary>
        public readonly struct RenameChange
        {
            public readonly string File;
            public readonly int Line;
            public readonly string Before;
            public readonly string After;

            public RenameChange(string file, int line, string before, string after)
            {
                File = file;
                Line = line;
                Before = before;
                After = after;
            }
        }

        /// <summary>Что изменит переименование (без записи). Провайдеры в план не входят — только отмечены.</summary>
        public static List<RenameChange> PlanRename(string from, string to, LocSettings settings = null, ICollection<string> knownKeys = null,
                                                    List<string> errors = null)
        {
            var list = new List<RenameChange>();
            Rewrite(from, to, settings, knownKeys, false, list, null, errors);
            return list;
        }

        /// <summary>
        /// Переименовывает ключ во всех употреблениях (и у провайдеров). Возвращает число замен;
        /// изменённые файлы — в <paramref name="changed"/>, отказы — в <paramref name="errors"/>.
        /// </summary>
        public static int ApplyRename(string from, string to, List<string> changed, List<string> errors, LocSettings settings = null,
                                      ICollection<string> knownKeys = null)
        {
            var plan = new List<RenameChange>();
            Rewrite(from, to, settings, knownKeys, true, plan, changed, errors);

            foreach (var provider in Providers)
            {
                try { provider.Rename(from, to, changed, errors); }
                catch (Exception e)
                {
                    errors?.Add($"провайдер {provider.Name}: {e.Message}");
                    Debug.LogException(e);
                }
            }

            var imported = false;
            if (changed != null)
            {
                foreach (var path in changed)
                {
                    if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                    AssetDatabase.ImportAsset(path);
                    imported = true;
                }
            }

            if (imported) AssetDatabase.Refresh();
            return plan.Count;
        }

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private static void Rewrite(string from, string to, LocSettings settings, ICollection<string> knownKeys, bool write,
                                    List<RenameChange> plan, List<string> changed, List<string> errors)
        {
            settings ??= LocSettings.Current;
            from = Loc.KeyOf(from);
            to = Loc.KeyOf(to);
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;

            var keys = new List<string>(knownKeys ?? LocEditor.ReadAllKeys());
            if (!keys.Contains(from)) keys.Add(from);
            var constants = ConstantMap(keys);

            var fromConst = LocEditor.IdentifierOf(from);
            var constUnique = constants.TryGetValue(fromConst, out var owner) && owner == from;

            var rules = Rules(settings, errors);
            var excludes = settings != null ? settings.UsageExcludes : Array.Empty<string>();

            foreach (var (full, relative) in Files(rules, excludes))
            {
                byte[] bytes;
                try
                {
                    if (new FileInfo(full).Length > MaxFileSize) continue;
                    bytes = System.IO.File.ReadAllBytes(full);
                }
                catch (Exception)
                {
                    continue;
                }

                // Быстрый отсев по байтам ключа (ключ — ASCII).
                if (IndexOf(bytes, Encoding.ASCII.GetBytes(from)) < 0 && IndexOf(bytes, Encoding.ASCII.GetBytes(fromConst)) < 0) continue;

                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text;
                try { text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
                catch (DecoderFallbackException)
                {
                    errors?.Add($"{relative}: не UTF-8 — не тронут");
                    continue;
                }

                var spans = new List<(int index, int length, string value)>();
                var extension = Path.GetExtension(relative);

                foreach (var rule in rules)
                {
                    if (!rule.Applies(relative, extension) || rule.Replace == LocUsageReplace.None) continue;

                    foreach (Match match in rule.Regex.Matches(text))
                    {
                        var group = rule.KeyGroup(match);
                        if (!group.Success) continue;

                        if (rule.IsConst)
                        {
                            if (group.Value != fromConst) continue;
                            if (!constUnique)
                            {
                                errors?.Add($"{relative}: L.{fromConst} общая для нескольких ключей — не переименована");
                                continue;
                            }
                        }
                        else if (rule.Replace == LocUsageReplace.Identifier)
                        {
                            if (group.Value != fromConst) continue;
                        }
                        else if (group.Value != from) continue;

                        var value = rule.Replace == LocUsageReplace.Identifier ? LocEditor.IdentifierOf(to) : to;
                        if (!spans.Exists(s => s.index == group.Index)) spans.Add((group.Index, group.Length, value));
                    }
                }

                if (spans.Count == 0) continue;

                spans.Sort((a, b) => b.index.CompareTo(a.index));
                var lines = LineStarts(text);
                var sb = new StringBuilder(text);
                foreach (var span in spans)
                {
                    var line = LineOf(lines, span.index);
                    var before = SnippetOf(text, lines, line);
                    var lineStart = lines[line];
                    var lineText = before;
                    var raw = text.Substring(lineStart, (line + 1 < lines.Count ? lines[line + 1] - 1 : text.Length) - lineStart);
                    var after = (raw.Substring(0, span.index - lineStart) + span.value + raw.Substring(span.index - lineStart + span.length)).Trim();
                    plan.Add(new RenameChange(relative, line + 1, lineText, after.Length > 160 ? after.Substring(0, 160) + "…" : after));

                    sb.Remove(span.index, span.length).Insert(span.index, span.value);
                }

                if (!write) continue;

                try
                {
                    var output = Encoding.UTF8.GetBytes(sb.ToString());
                    if (bom)
                    {
                        var withBom = new byte[output.Length + 3];
                        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
                        Buffer.BlockCopy(output, 0, withBom, 3, output.Length);
                        output = withBom;
                    }

                    System.IO.File.WriteAllBytes(full, output);
                    changed?.Add(relative);
                }
                catch (Exception e)
                {
                    errors?.Add($"{relative}: {e.Message}");
                }
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0) return -1;
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] == needle[j]) continue;
                    ok = false;
                    break;
                }

                if (ok) return i;
            }

            return -1;
        }
    }
}
