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
        private readonly SortedSet<string> _prefixes = new(StringComparer.Ordinal);
        private readonly List<string> _skipped = new();
        private static readonly List<LocUsage> Empty = new();

        /// <summary>
        /// Префиксы ключей, которые код собирает из частей: <c>Tr("alchemy.why." + id)</c> — найденная строка
        /// кончается на «.», «_» или «-», и с неё начинается хотя бы один известный ключ. Ключ с таким префиксом
        /// считается употреблённым.
        /// </summary>
        public IReadOnlyCollection<string> Prefixes => _prefixes;

        /// <summary>Ключ собирается кодом по найденному префиксу.</summary>
        public bool IsPrefixed(string key)
        {
            if (key == null || _prefixes.Count == 0) return false;
            foreach (var prefix in _prefixes)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        internal void AddPrefix(string prefix) => _prefixes.Add(prefix);

        /// <summary>
        /// Обход прерван (отмена в прогрессе): индекс неполный. Им нельзя подменять прежний полный индекс,
        /// а «нет употреблений» по нему ничего не значит.
        /// </summary>
        public bool Cancelled { get; internal set; }

        /// <summary>Обход дошёл до конца, провайдеры опрошены.</summary>
        public bool Complete => !Cancelled;

        /// <summary>Файлы, которые обход не просмотрел (больше 32 МБ, не прочитались): путь и причина.</summary>
        public IReadOnlyList<string> Skipped => _skipped;

        internal void AddSkipped(string reason) => _skipped.Add(reason);

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

        // ---------------------------------------------------------------- употреблён / неизвестен

        /// <summary>Ключ собирается кодом: динамический префикс настроек или префикс, найденный в коде.</summary>
        public bool IsAssembled(string key, LocSettings settings) =>
            key != null && (LocKeyScanner.IsDynamic(key, settings) || IsPrefixed(key));

        /// <summary>
        /// Ключ употреблён: найден в проекте, собирается кодом (<see cref="IsAssembled"/>) или встроенный
        /// ключ AppCore (его берёт сам AppCore). Одно правило для таблицы, сверки, CLI и проверки перед сборкой.
        /// </summary>
        public bool IsUsed(string key, LocSettings settings) =>
            key != null && (Contains(key) || LocKeyScanner.IsBuiltin(key) || IsAssembled(key, settings));

        /// <summary>
        /// Употребляются, а в таблице нет — по порядку. Встроенные ключи AppCore и динамические (по префиксам
        /// настроек) не в счёт. <paramref name="table"/> — обычно сводная; <c>null</c> — все найденные ключи.
        /// </summary>
        public List<string> Unknown(LocTable table, LocSettings settings)
        {
            var list = new List<string>();
            foreach (var key in _byKey.Keys)
            {
                if (table != null && table.Contains(key)) continue;
                if (LocKeyScanner.IsBuiltin(key) || LocKeyScanner.IsDynamic(key, settings)) continue;
                list.Add(key);
            }

            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>Ключи таблицы, которые нигде не употреблены (<see cref="IsUsed"/>), по порядку.</summary>
        public List<string> Unused(LocTable table, LocSettings settings)
        {
            var list = new List<string>();
            if (table == null) return list;

            foreach (var entry in table.Entries)
                if (!IsUsed(entry.Key, settings)) list.Add(entry.Key);

            list.Sort(StringComparer.Ordinal);
            return list;
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
    /// «^KEY» в вёрстке засчитывается только в начале значения: после кавычки, пробела, <c>=</c>, <c>,</c>,
    /// <c>;</c>, <c>|</c>, <c>&gt;</c> или переноса <c>&amp;#10;</c> — иначе в счёт попали бы степени
    /// вроде «10^{mass}». В коде — только литерал целиком (<c>"^MENU_PLAY"</c>), в YAML ассетов, префабов и
    /// сцен — только значение поля целиком (<c>text: ^MENU_PLAY</c>); иначе в счёт попадают регулярки вида
    /// <c>^Tr\(</c>. Комментарии C# (<c>//</c>, <c>/* */</c>, XML-doc) не просматриваются и не переписываются.
    /// Константа <c>L.X</c> (и идентификатор шаблона с <see cref="LocUsageReplace.Identifier"/>) засчитывается
    /// только ключу, чьё имя константы однозначно (у «a.b» и «a_b» константа одна — такую не переименовываем).
    /// Встроенные правила смотрят в <c>Assets/</c> и во встроенные пакеты <c>Packages/&lt;папка&gt;/</c>, где
    /// лежат таблицы локализации. Файлы не в UTF-8 при переименовании не трогаются; BOM и переводы строк сохраняются.
    /// </remarks>
    public static class LocUsages
    {
        public const string CaretKind = "^";
        public const string ConstKind = "L";

        // Служебный вид: найденная строка — префикс ключа, собираемого кодом (в индекс как префикс, не употребление).
        internal const string PrefixKind = "prefix";

        private const long MaxFileSize = 32L * 1024 * 1024;

        private static readonly string[] DefaultExtensions = { ".uxml", ".cs", ".prefab", ".asset", ".unity" };

        // Текстовая сериализация Unity (YAML): «^KEY» — только значение поля целиком, файл без «%YAML» — бинарный.
        private static readonly HashSet<string> YamlExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".prefab", ".unity", ".mat", ".controller", ".overrideController", ".anim", ".playable",
            ".preset", ".spriteatlas", ".mixer", ".lighting",
        };

        // Деплой Nexus и кэши — не употребления.
        private static readonly string[] SkippedFragments = { "/NexusStates/", "/Editor/NexusPlugins/" };

        // Сгенерированные константы и сами настройки локализации (в них регулярки шаблонов вида «^Tr\(») — не употребления.
        private static readonly string[] SkippedFiles = { AppCorePaths.LocKeysPath, LocSettings.AssetPath };

        internal static readonly Regex CaretPattern = new(@"(?<=[""'\s=,;|>]|&#10;)\^([A-Za-z][A-Za-z0-9_.\-]*[A-Za-z0-9_]|[A-Za-z])", RegexOptions.Compiled);
        // В коде «^KEY» — только литерал целиком: "^MENU_PLAY". Иначе в счёт попадают регулярки вида "^(?<l>…)".
        internal static readonly Regex CaretCodePattern = new(@"(?<="")\^([A-Za-z][A-Za-z0-9_.\-]*[A-Za-z0-9_]|[A-Za-z])(?="")", RegexOptions.Compiled);
        // В YAML «^KEY» — только значение поля (или элемента списка) целиком: «text: ^MENU_PLAY», «- '^MENU_PLAY'».
        internal static readonly Regex CaretYamlPattern = new(
            @"^[ \t]*(?:-[ \t]+)?(?:[^\s:#'""{}\[\],][^:\r\n]*:[ \t]+)?(?<q>[""']?)\^(?<key>[A-Za-z][A-Za-z0-9_.\-]*[A-Za-z0-9_]|[A-Za-z])\k<q>[ \t]*\r?$",
            RegexOptions.Compiled | RegexOptions.Multiline);

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

        /// <summary>
        /// Ключ из ввода: без пробелов по краям и «^» в начале, пробелы внутри → «_». Один разбор для таблицы
        /// на странице и команд CLI.
        /// </summary>
        public static string NormalizeKey(string text)
        {
            var key = (text ?? string.Empty).Trim();
            while (key.Length > 0 && key[0] == Loc.Prefix) key = key.Substring(1);
            return key.Trim().Replace(' ', '_');
        }

        // ================================================================ правила

        /// <summary>Правило поиска, собранное из настроек: регулярка, где искать, как переименовывать.</summary>
        internal sealed class Rule
        {
            public string Kind;
            public Regex Regex;
            public int GroupIndex;
            public string GroupName;
            public HashSet<string> Extensions;
            public string[] Folders;
            public LocUsageReplace Replace;
            public bool IsConst;
            public bool KnownOnly;

            /// <summary>Ключ записан идентификатором (константа или шаблон с Identifier) — сводится к ключу по карте констант.</summary>
            public bool ByIdentifier => IsConst || Replace == LocUsageReplace.Identifier;

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

        /// <summary>Всё, что нужно разбору одного текста: правила, карта констант и известные ключи.</summary>
        internal sealed class ScanSetup
        {
            public List<Rule> Rules;

            /// <summary>Имя константы → ключ; null — имя общее для нескольких ключей.</summary>
            public Dictionary<string, string> Constants;

            public HashSet<string> Known;

            // известные ключи по порядку — проверка «с префикса начинается хотя бы один ключ»
            public string[] Sorted;

            public bool HasKeyWithPrefix(string prefix)
            {
                var i = Array.BinarySearch(Sorted, prefix, StringComparer.Ordinal);
                if (i < 0) i = ~i;
                return i < Sorted.Length && Sorted[i].StartsWith(prefix, StringComparison.Ordinal);
            }
        }

        /// <summary>Разбор по настройкам: встроенные правила смотрят в Assets и во встроенные пакеты с таблицами.</summary>
        private static ScanSetup Setup(LocSettings settings, ICollection<string> knownKeys, List<string> errors) =>
            Setup(settings != null ? settings.UsageExtensions : null, settings != null ? settings.UsagePatterns : null,
                  BuiltinFolders(settings), knownKeys, errors);

        /// <summary>
        /// Разбор без настроек и файловой системы: расширения встроенных правил (<c>null</c> — по умолчанию),
        /// свои шаблоны, папки встроенных правил (<c>null</c> — Assets) и известные ключи.
        /// </summary>
        internal static ScanSetup Setup(string[] extensions, IList<LocUsagePattern> patterns, string[] folders,
                                        ICollection<string> knownKeys, List<string> errors)
        {
            var known = knownKeys as HashSet<string> ?? new HashSet<string>(knownKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
            var sorted = new string[known.Count];
            known.CopyTo(sorted);
            Array.Sort(sorted, StringComparer.Ordinal);

            return new ScanSetup
            {
                Rules = Rules(extensions, patterns, folders ?? new[] { "Assets/" }, errors),
                Constants = ConstantMap(known),
                Known = known,
                Sorted = sorted,
            };
        }

        private static List<Rule> Rules(string[] extensions, IList<LocUsagePattern> patterns, string[] folders, List<string> errors)
        {
            var rules = new List<Rule>();

            var markup = ExtensionSet(extensions ?? DefaultExtensions);
            var code = markup.Remove(".cs");
            var yaml = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in markup)
                if (YamlExtensions.Contains(extension)) yaml.Add(extension);
            markup.ExceptWith(yaml);

            if (markup.Count > 0)
                rules.Add(new Rule
                {
                    Kind = CaretKind, Regex = CaretPattern, GroupIndex = 1, Extensions = markup,
                    Folders = folders, Replace = LocUsageReplace.Key,
                });

            if (yaml.Count > 0)
                rules.Add(new Rule
                {
                    Kind = CaretKind, Regex = CaretYamlPattern, GroupName = "key", Extensions = yaml,
                    Folders = folders, Replace = LocUsageReplace.Key,
                });

            if (code)
                rules.Add(new Rule
                {
                    Kind = CaretKind, Regex = CaretCodePattern, GroupIndex = 1, Extensions = ExtensionSet(new[] { ".cs" }),
                    Folders = folders, Replace = LocUsageReplace.Key,
                });

            rules.Add(new Rule
            {
                Kind = ConstKind, Regex = ConstPattern, GroupIndex = 1, Extensions = ExtensionSet(new[] { ".cs" }),
                Folders = folders, Replace = LocUsageReplace.Identifier, IsConst = true,
            });

            if (patterns == null) return rules;

            foreach (var pattern in patterns)
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
                    KnownOnly = pattern.knownOnly,
                });
            }

            return rules;
        }

        /// <summary>
        /// Папки встроенных правил: Assets и встроенные (embedded) пакеты <c>Packages/&lt;папка&gt;/</c>, где лежат
        /// таблицы каталога. Пакеты из Library/PackageCache не просматриваются — их на диске проекта нет.
        /// </summary>
        private static string[] BuiltinFolders(LocSettings settings)
        {
            var folders = new List<string> { "Assets/" };
            if (settings == null) return folders.ToArray();

            List<LocPackageFile> files;
            try { files = LocPackageCatalog.Collect(settings); }
            catch (Exception e)
            {
                Debug.LogException(e);
                return folders.ToArray();
            }

            const string packages = "Packages/";
            var root = LocPackageCatalog.ProjectRoot;
            foreach (var file in files)
            {
                var path = file?.Path;
                if (path == null || !path.StartsWith(packages, StringComparison.Ordinal)) continue;

                var slash = path.IndexOf('/', packages.Length);
                if (slash < 0) continue;

                var folder = path.Substring(0, slash + 1);
                if (!folders.Contains(folder) && Directory.Exists(root + "/" + folder.TrimEnd('/'))) folders.Add(folder);
            }

            return folders.ToArray();
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

        private static bool IsCode(string extension) => string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);

        // Текстовый YAML Unity начинается с «%YAML»; бинарная сериализация — не текст, в ней не ищем.
        private static bool IsBinaryAsset(string extension, string text) =>
            YamlExtensions.Contains(extension) && !text.StartsWith("%YAML", StringComparison.Ordinal);

        // ================================================================ обход

        /// <summary>
        /// Все употребления одним обходом. <paramref name="knownKeys"/> нужны для констант <c>L.X</c>
        /// (по ним имя константы сводится к ключу) и для префиксов; <c>null</c> — ключи таблиц каталога.
        /// <paramref name="progress"/>: доля 0…1 → true, чтобы прервать; прерванный индекс помечен
        /// <see cref="LocUsageIndex.Cancelled"/>. Непросмотренные файлы — в <paramref name="errors"/> и
        /// <see cref="LocUsageIndex.Skipped"/>.
        /// </summary>
        public static LocUsageIndex Scan(LocSettings settings = null, ICollection<string> knownKeys = null,
                                         Func<float, bool> progress = null, List<string> errors = null)
        {
            settings ??= LocSettings.Current;
            knownKeys ??= LocEditor.ReadAllKeys();

            var watch = Stopwatch.StartNew();
            var index = new LocUsageIndex();
            var setup = Setup(settings, knownKeys, errors);
            var excludes = settings != null ? settings.UsageExcludes : Array.Empty<string>();

            // Чтение и разбор — параллельно пачками (Unity API здесь нет), прогресс — между пачками на главном потоке.
            var files = Files(setup.Rules, excludes);
            const int chunk = 512;
            var size = Math.Min(chunk, Math.Max(1, files.Count));
            var found = new List<LocUsage>[size];
            var skipped = new string[size];
            var scanned = 0;

            for (var start = 0; start < files.Count; start += chunk)
            {
                if (progress != null && progress((float)start / files.Count))
                {
                    index.Cancelled = true;
                    break;
                }

                var count = Math.Min(chunk, files.Count - start);
                var offset = start;
                System.Threading.Tasks.Parallel.For(0, count, i =>
                {
                    var (full, relative) = files[offset + i];
                    found[i] = null;
                    skipped[i] = null;
                    string text;
                    try
                    {
                        if (new FileInfo(full).Length > MaxFileSize)
                        {
                            skipped[i] = $"{relative}: больше {MaxFileSize / (1024 * 1024)} МБ — не просмотрен";
                            return;
                        }

                        text = System.IO.File.ReadAllText(full);
                    }
                    catch (Exception e)
                    {
                        skipped[i] = $"{relative}: не прочитан — {e.Message}";
                        return;
                    }

                    System.Threading.Interlocked.Increment(ref scanned);
                    List<LocUsage> list = null;
                    ScanText(text, relative, setup, u => (list ??= new List<LocUsage>()).Add(u));
                    found[i] = list;
                });

                // Порядок файлов сохраняется — итог одинаковый от запуска к запуску.
                for (var i = 0; i < count; i++)
                {
                    if (skipped[i] != null)
                    {
                        index.AddSkipped(skipped[i]);
                        errors?.Add(skipped[i]);
                    }

                    if (found[i] == null) continue;
                    foreach (var usage in found[i])
                    {
                        if (usage.Kind == PrefixKind) index.AddPrefix(usage.Key);
                        else index.Add(usage);
                    }
                }
            }

            index.FilesScanned = scanned;

            // Прерванный обход неполон и так — провайдеров не опрашиваем.
            if (!index.Cancelled)
            {
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
            }

            index.Seconds = watch.Elapsed.TotalSeconds;
            index.Time = DateTime.Now;
            return index;
        }

        /// <summary>Употребления одного ключа (тот же обход, но собирается только он).</summary>
        public static IReadOnlyList<LocUsage> Find(string key, LocSettings settings = null)
        {
            var index = Scan(settings);
            return index.Of(NormalizeKey(key));
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
            foreach (var file in SkippedFiles)
                if (string.Equals(path, file, StringComparison.OrdinalIgnoreCase)) return true;
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

        /// <summary>Разбор одного текста (без файловой системы): употребления и найденные префиксы (<see cref="PrefixKind"/>).</summary>
        internal static List<LocUsage> ScanText(string text, string relative, ScanSetup setup)
        {
            var list = new List<LocUsage>();
            ScanText(text, relative, setup, list.Add);
            return list;
        }

        internal static void ScanText(string text, string relative, ScanSetup setup, Action<LocUsage> add)
        {
            var extension = Path.GetExtension(relative);
            if (IsBinaryAsset(extension, text)) return;

            // В .cs ищем по тексту без комментариев: смещения те же, сниппеты — из исходного.
            var source = IsCode(extension) ? StripComments(text) : text;
            List<int> lines = null;

            // Одно место, найденное двумя шаблонами (вызов и литерал), засчитывается один раз.
            HashSet<int> taken = null;

            foreach (var rule in setup.Rules)
            {
                if (!rule.Applies(relative, extension)) continue;
                if (rule.Kind == CaretKind && source.IndexOf('^') < 0) continue;
                if (rule.IsConst && source.IndexOf("L.", StringComparison.Ordinal) < 0) continue;

                foreach (Match match in rule.Regex.Matches(source))
                {
                    var group = rule.KeyGroup(match);
                    if (!group.Success) continue;

                    var key = group.Value;
                    if (rule.ByIdentifier)
                    {
                        // Константа сводится к ключу только однозначно; идентификатор шаблона — если сводится.
                        if (setup.Constants.TryGetValue(key, out var owner) && owner != null) key = owner;
                        else if (rule.IsConst) continue;
                    }

                    if (!rule.IsConst)
                    {
                        if (!LocSlots.IsValidKey(key)) continue;

                        // Строка на «.», «_», «-» — префикс собираемого ключа, только если с неё начинается известный ключ.
                        if (IsPrefix(key) && setup.HasKeyWithPrefix(key))
                        {
                            lines ??= LineStarts(text);
                            add(new LocUsage { Key = key, File = relative, Kind = PrefixKind, Line = LineOf(lines, group.Index) + 1 });
                            continue;
                        }

                        if (rule.KnownOnly && !setup.Known.Contains(key)) continue;
                    }

                    taken ??= new HashSet<int>();
                    if (!taken.Add(group.Index)) continue;

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

        /// <summary>Строка кончается на разделитель — кандидат в префикс собираемого ключа.</summary>
        private static bool IsPrefix(string key)
        {
            var last = key[key.Length - 1];
            return last == '.' || last == '_' || last == '-';
        }

        // ================================================================ комментарии C#

        /// <summary>
        /// Текст C# с комментариями (<c>//</c>, <c>/* */</c>, XML-doc), заменёнными пробелами: длина, смещения и
        /// переводы строк те же. Строковые литералы — обычные, <c>@"…"</c>, <c>$"…{…}"</c> (со строками в дырах),
        /// <c>"""…"""</c> — и символьные не трогаются. Без комментариев возвращается тот же объект.
        /// </summary>
        internal static string StripComments(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('/') < 0) return text;

            char[] buffer = null;
            var i = 0;
            SkipCode(text, ref i, ref buffer, false);
            return buffer == null ? text : new string(buffer);
        }

        private static void Blank(string text, int from, int to, ref char[] buffer)
        {
            buffer ??= text.ToCharArray();
            for (var k = from; k < to; k++)
                if (buffer[k] != '\r' && buffer[k] != '\n') buffer[k] = ' ';
        }

        // Код до конца текста, а в дыре интерполяции — до её закрывающей «}» (i остаётся на ней).
        private static void SkipCode(string t, ref int i, ref char[] buffer, bool hole)
        {
            var depth = 0;
            while (i < t.Length)
            {
                var c = t[i];

                if (c == '/' && i + 1 < t.Length && (t[i + 1] == '/' || t[i + 1] == '*'))
                {
                    int end;
                    if (t[i + 1] == '/')
                    {
                        end = t.IndexOf('\n', i);
                        if (end < 0) end = t.Length;
                    }
                    else
                    {
                        end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        end = end < 0 ? t.Length : end + 2;
                    }

                    Blank(t, i, end, ref buffer);
                    i = end;
                    continue;
                }

                if (c == '"' || c == '$' || c == '@')
                {
                    var j = i;
                    var dollars = 0;
                    var verbatim = false;
                    while (j < t.Length && (t[j] == '$' || t[j] == '@'))
                    {
                        if (t[j] == '$') dollars++;
                        else verbatim = true;
                        j++;
                    }

                    if (j < t.Length && t[j] == '"')
                    {
                        i = j;
                        SkipString(t, ref i, ref buffer, dollars, verbatim);
                    }
                    else i = j > i ? j : i + 1;

                    continue;
                }

                if (c == '\'')
                {
                    i++;
                    while (i < t.Length && t[i] != '\'' && t[i] != '\n')
                    {
                        if (t[i] == '\\') i++;
                        i++;
                    }

                    i++;
                    continue;
                }

                if (hole)
                {
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        if (depth == 0) return;
                        depth--;
                    }
                }

                i++;
            }
        }

        // Строковый литерал с открывающей кавычки t[i]; i — за закрывающей.
        private static void SkipString(string t, ref int i, ref char[] buffer, int dollars, bool verbatim)
        {
            var quotes = 0;
            while (i + quotes < t.Length && t[i + quotes] == '"') quotes++;

            // """raw""": три кавычки и больше, закрывается тем же числом; дыры — столько же «{», сколько «$».
            if (!verbatim && quotes >= 3)
            {
                i += quotes;
                while (i < t.Length)
                {
                    var c = t[i];
                    if (c == '"')
                    {
                        var run = 0;
                        while (i + run < t.Length && t[i + run] == '"') run++;
                        i += run;
                        if (run >= quotes) return;
                        continue;
                    }

                    if (dollars > 0 && c == '{')
                    {
                        var run = 0;
                        while (i + run < t.Length && t[i + run] == '{') run++;
                        i += run;
                        if (run < dollars) continue;

                        SkipCode(t, ref i, ref buffer, true);
                        var close = 0;
                        while (i < t.Length && t[i] == '}' && close < dollars)
                        {
                            i++;
                            close++;
                        }

                        continue;
                    }

                    i++;
                }

                return;
            }

            i++;
            while (i < t.Length)
            {
                var c = t[i];
                if (!verbatim)
                {
                    if (c == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (c == '\n') return; // незакрытая строка
                }

                if (c == '"')
                {
                    if (verbatim && i + 1 < t.Length && t[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    return;
                }

                if (dollars > 0 && c == '{')
                {
                    if (i + 1 < t.Length && t[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    SkipCode(t, ref i, ref buffer, true);
                    if (i < t.Length) i++; // «}» дыры
                    continue;
                }

                i++;
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
            Rewrite(from, to, settings, knownKeys, false, list, null, errors, errors);
            return list;
        }

        /// <summary>
        /// Что изменит переименование (без записи). Предупреждения — в <paramref name="warnings"/>, отдельно от
        /// отказов: ключ собирается кодом по префиксу (такое место заменой не переименовать), непросмотренные файлы.
        /// </summary>
        public static List<RenameChange> PlanRename(string from, string to, LocSettings settings, ICollection<string> knownKeys,
                                                    List<string> errors, List<string> warnings)
        {
            var list = new List<RenameChange>();
            Rewrite(from, to, settings, knownKeys, false, list, null, errors, warnings);
            return list;
        }

        /// <summary>
        /// Переименовывает ключ во всех употреблениях (и у провайдеров). Возвращает число замен;
        /// изменённые файлы — в <paramref name="changed"/>, отказы и предупреждения — в <paramref name="errors"/>.
        /// </summary>
        public static int ApplyRename(string from, string to, List<string> changed, List<string> errors, LocSettings settings = null,
                                      ICollection<string> knownKeys = null) =>
            ApplyRename(from, to, changed, errors, settings, knownKeys, errors);

        /// <summary>
        /// Переименовывает ключ во всех употреблениях (и у провайдеров). Возвращает число замен; изменённые
        /// файлы — в <paramref name="changed"/>, отказы — в <paramref name="errors"/>, предупреждения (префиксы,
        /// непросмотренные файлы) — в <paramref name="warnings"/>.
        /// </summary>
        public static int ApplyRename(string from, string to, List<string> changed, List<string> errors, LocSettings settings,
                                      ICollection<string> knownKeys, List<string> warnings)
        {
            changed ??= new List<string>();
            var plan = new List<RenameChange>();
            Rewrite(from, to, settings, knownKeys, true, plan, changed, errors, warnings);

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
            var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in changed)
            {
                imported = true;
                var asset = AssetPathOf(path, packages);
                if (asset != null) AssetDatabase.ImportAsset(asset);
            }

            if (imported) AssetDatabase.Refresh();
            return plan.Count;
        }

        [Serializable]
        private sealed class PackageJson
        {
            public string name;
        }

        // Путь в AssetDatabase: Assets — как есть; встроенный пакет лежит в Packages/<папка>, а виден как Packages/<имя>.
        private static string AssetPathOf(string relative, Dictionary<string, string> names)
        {
            if (relative == null) return null;
            if (relative.StartsWith("Assets/", StringComparison.Ordinal)) return relative;

            const string packages = "Packages/";
            if (!relative.StartsWith(packages, StringComparison.Ordinal)) return null;

            var slash = relative.IndexOf('/', packages.Length);
            if (slash < 0) return null;

            var folder = relative.Substring(packages.Length, slash - packages.Length);
            if (!names.TryGetValue(folder, out var name))
            {
                name = null;
                var manifest = LocPackageCatalog.ProjectRoot + "/" + packages + folder + "/package.json";
                try
                {
                    if (System.IO.File.Exists(manifest)) name = JsonUtility.FromJson<PackageJson>(System.IO.File.ReadAllText(manifest))?.name;
                }
                catch (Exception e)
                {
                    // без имени пакета файл подхватит общий Refresh
                    Debug.LogWarning($"[Loc] {manifest}: {e.Message}");
                }

                names[folder] = string.IsNullOrEmpty(name) ? null : name;
            }

            return names[folder] == null ? null : packages + names[folder] + relative.Substring(slash);
        }

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>Что и на что переименовывается — общее для всех файлов одного переименования.</summary>
        internal sealed class RenameSpec
        {
            public string From;
            public string To;
            public string FromConst;
            public string ToConst;

            /// <summary>Имя константы ключа однозначно — константу и идентификатор шаблона можно переписать.</summary>
            public bool ConstUnique;

            /// <summary>Начала ключа, кончающиеся на «.», «_», «-», — по ним код может собирать ключ.</summary>
            public string[] Prefixes;

            public ScanSetup Setup;
        }

        /// <summary>Переименование по готовому разбору; <paramref name="from"/> должен быть среди известных ключей разбора.</summary>
        internal static RenameSpec Spec(string from, string to, ScanSetup setup)
        {
            var prefixes = new List<string>();
            for (var p = 1; p < from.Length - 1; p++)
                if (from[p] == '.' || from[p] == '_' || from[p] == '-') prefixes.Add(from.Substring(0, p + 1));

            var fromConst = LocEditor.IdentifierOf(from);
            return new RenameSpec
            {
                From = from, To = to, FromConst = fromConst, ToConst = LocEditor.IdentifierOf(to),
                ConstUnique = setup.Constants.TryGetValue(fromConst, out var owner) && owner == from,
                Prefixes = prefixes.ToArray(),
                Setup = setup,
            };
        }

        private static void Rewrite(string from, string to, LocSettings settings, ICollection<string> knownKeys, bool write,
                                    List<RenameChange> plan, List<string> changed, List<string> errors, List<string> warnings)
        {
            settings ??= LocSettings.Current;
            from = NormalizeKey(from);
            to = NormalizeKey(to);
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;

            var keys = new HashSet<string>(knownKeys ?? LocEditor.ReadAllKeys(), StringComparer.Ordinal) { from };
            var setup = Setup(settings, keys, errors);
            var spec = Spec(from, to, setup);
            var excludes = settings != null ? settings.UsageExcludes : Array.Empty<string>();

            // Отсев параллельно: читаем всё и оставляем файлы, где есть байты ключа, константы или префикса (ключ — ASCII).
            var all = Files(setup.Rules, excludes);
            var contents = new byte[all.Count][];
            var notes = new string[all.Count];
            var needles = new List<byte[]> { Encoding.ASCII.GetBytes(from), Encoding.ASCII.GetBytes(spec.FromConst) };
            if (warnings != null)
                foreach (var prefix in spec.Prefixes) needles.Add(Encoding.ASCII.GetBytes(prefix));

            System.Threading.Tasks.Parallel.For(0, all.Count, i =>
            {
                try
                {
                    if (new FileInfo(all[i].full).Length > MaxFileSize)
                    {
                        notes[i] = $"{all[i].relative}: больше {MaxFileSize / (1024 * 1024)} МБ — не просмотрен";
                        return;
                    }

                    var data = System.IO.File.ReadAllBytes(all[i].full);
                    foreach (var needle in needles)
                    {
                        if (IndexOf(data, needle) < 0) continue;
                        contents[i] = data;
                        break;
                    }
                }
                catch (Exception e)
                {
                    notes[i] = $"{all[i].relative}: не прочитан — {e.Message}";
                }
            });

            for (var fileIndex = 0; fileIndex < all.Count; fileIndex++)
            {
                if (notes[fileIndex] != null) warnings?.Add(notes[fileIndex]);

                var bytes = contents[fileIndex];
                if (bytes == null) continue;
                var (full, relative) = all[fileIndex];

                var output = RenameInBytes(bytes, relative, spec, plan, errors, warnings);
                if (output == null || !write) continue;

                try
                {
                    System.IO.File.WriteAllBytes(full, output);
                    changed?.Add(relative);
                }
                catch (Exception e)
                {
                    errors?.Add($"{relative}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Переименование в содержимом одного файла (без файловой системы): замены — в <paramref name="plan"/>,
        /// итог — новые байты (BOM сохранён); <c>null</c> — менять нечего или файл не тронут (причина в errors).
        /// </summary>
        internal static byte[] RenameInBytes(byte[] bytes, string relative, RenameSpec spec, List<RenameChange> plan,
                                             List<string> errors, List<string> warnings)
        {
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text;
            try { text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
            catch (DecoderFallbackException)
            {
                errors?.Add($"{relative}: не UTF-8 — не тронут");
                return null;
            }

            var extension = Path.GetExtension(relative);
            if (IsBinaryAsset(extension, text)) return null;

            // Ключ, который код собирает по префиксу, заменой литералов не переименовать — предупреждаем.
            if (warnings != null && spec.Prefixes.Length > 0)
            {
                foreach (var usage in ScanText(text, relative, spec.Setup))
                {
                    if (usage.Kind != PrefixKind || !spec.From.StartsWith(usage.Key, StringComparison.Ordinal)) continue;
                    var warning = $"{relative}:{usage.Line}: ключ собирается кодом по префиксу «{usage.Key}» — это место не переименовать";
                    if (!warnings.Contains(warning)) warnings.Add(warning);
                }
            }

            var source = IsCode(extension) ? StripComments(text) : text;
            var spans = new List<(int index, int length, string value)>();

            foreach (var rule in spec.Setup.Rules)
            {
                if (!rule.Applies(relative, extension) || rule.Replace == LocUsageReplace.None) continue;

                foreach (Match match in rule.Regex.Matches(source))
                {
                    var group = rule.KeyGroup(match);
                    if (!group.Success) continue;

                    string value;
                    if (rule.ByIdentifier)
                    {
                        if (group.Value != spec.FromConst) continue;
                        if (!spec.ConstUnique)
                        {
                            var name = rule.IsConst ? "L." + spec.FromConst : $"{spec.FromConst} («{rule.Kind}»)";
                            var error = $"{relative}: {name} общая для нескольких ключей — не переименована";
                            if (errors != null && !errors.Contains(error)) errors.Add(error);
                            continue;
                        }

                        value = spec.ToConst;
                    }
                    else
                    {
                        if (group.Value != spec.From) continue;
                        value = spec.To;
                    }

                    if (!spans.Exists(s => s.index == group.Index)) spans.Add((group.Index, group.Length, value));
                }
            }

            if (spans.Count == 0) return null;

            spans.Sort((a, b) => b.index.CompareTo(a.index));
            var lines = LineStarts(text);
            var sb = new StringBuilder(text);
            foreach (var span in spans)
            {
                var line = LineOf(lines, span.index);
                var before = SnippetOf(text, lines, line);
                var lineStart = lines[line];
                var raw = text.Substring(lineStart, (line + 1 < lines.Count ? lines[line + 1] - 1 : text.Length) - lineStart);
                var after = (raw.Substring(0, span.index - lineStart) + span.value + raw.Substring(span.index - lineStart + span.length)).Trim();
                plan?.Add(new RenameChange(relative, line + 1, before, after.Length > 160 ? after.Substring(0, 160) + "…" : after));

                sb.Remove(span.index, span.length).Insert(span.index, span.value);
            }

            var output = Encoding.UTF8.GetBytes(sb.ToString());
            if (!bom) return output;

            var withBom = new byte[output.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Buffer.BlockCopy(output, 0, withBom, 3, output.Length);
            return withBom;
        }

        private static int IndexOf(byte[] haystack, byte[] needle) =>
            needle.Length == 0 ? -1 : ((ReadOnlySpan<byte>)haystack).IndexOf(needle);
    }
}
