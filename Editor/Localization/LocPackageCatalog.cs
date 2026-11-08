using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>Файл пакета локализации, как его видит редактор.</summary>
    public sealed class LocPackageFile
    {
        /// <summary>Id пакета: в рантайме — id слоя (история ключей). По умолчанию — путь от корня проекта.</summary>
        public string Id;

        /// <summary>Путь от корня проекта через «/» (или абсолютный — для файлов вне проекта).</summary>
        public string Path;

        /// <summary>Полный путь на диске.</summary>
        public string FullPath;

        /// <summary>Подпись в списках.</summary>
        public string Name;

        /// <summary>Группа для фильтра (пак, модуль). Пусто — папка файла.</summary>
        public string Group;

        /// <summary>Только чтение: файл вне проекта (скачанный мод) или так решил провайдер.</summary>
        public bool ReadOnly;

        /// <summary>Базовая таблица проекта (LocalizationSettings → Base Table).</summary>
        public bool IsBase;

        public LocSource Source = LocSource.Project;

        /// <summary>Кто нашёл: «маски», «база» или имя провайдера.</summary>
        public string Provider;

        /// <summary>
        /// Файл пакета по пути (от корня проекта или абсолютному). Вне папки проекта — только чтение.
        /// </summary>
        public static LocPackageFile Create(string path, string id = null, bool readOnly = false, LocSource source = LocSource.Project,
                                            string group = null, string name = null)
        {
            var full = LocPackageCatalog.ToFullPath(path);
            var project = LocPackageCatalog.ToProjectPath(full);
            var inside = project != null;

            return new LocPackageFile
            {
                Path = project ?? full.Replace('\\', '/'),
                FullPath = full,
                Id = string.IsNullOrEmpty(id) ? project ?? full.Replace('\\', '/') : id,
                Name = string.IsNullOrEmpty(name) ? System.IO.Path.GetFileNameWithoutExtension(full) : name,
                Group = group,
                ReadOnly = readOnly || !inside,
                Source = source,
            };
        }

        public override string ToString() => $"{Name} ({Path})";
    }

    /// <summary>
    /// Откуда редактор берёт пакеты, если маски не годятся: свой порядок (порядок загрузки паков),
    /// моды вне проекта, свои id. Реализация — класс с конструктором без параметров в редакторской
    /// сборке проекта, находится сам. Есть провайдер — маски из настроек не используются.
    /// </summary>
    public interface ILocPackageProvider
    {
        /// <summary>Подпись в списке пакетов.</summary>
        string Name { get; }

        /// <summary>Пакеты в порядке применения (поздний главнее). Базовую таблицу добавлять не нужно.</summary>
        void Collect(LocSettings settings, List<LocPackageFile> into);
    }

    /// <summary>
    /// Каталог пакетов локализации в редакторе: базовая таблица + пакеты из масок
    /// (<see cref="LocSettings.PackageMasks"/>) или из провайдеров проекта (<see cref="ILocPackageProvider"/>).
    /// По нему работают константы L, проверка ключей, проверка шрифта и вкладка Nexus.
    /// </summary>
    public static class LocPackageCatalog
    {
        public const string MasksProvider = "маски";
        public const string BaseProvider = "база";

        private static List<ILocPackageProvider> _providers;

        /// <summary>Корень проекта (папка над Assets) через «/».</summary>
        public static string ProjectRoot => (System.IO.Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath).Replace('\\', '/');

        /// <summary>Провайдеры проекта (кэшируются до перекомпиляции).</summary>
        public static IReadOnlyList<ILocPackageProvider> Providers
        {
            get
            {
                if (_providers != null) return _providers;

                _providers = new List<ILocPackageProvider>();
                foreach (var type in TypeCache.GetTypesDerivedFrom<ILocPackageProvider>())
                {
                    if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null) continue;

                    try { _providers.Add((ILocPackageProvider)Activator.CreateInstance(type)); }
                    catch (Exception e) { Debug.LogException(e); }
                }

                _providers.Sort((a, b) => string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName));
                return _providers;
            }
        }

        /// <summary>Пакеты задаёт провайдер проекта, а не маски.</summary>
        public static bool UsesProviders => Providers.Count > 0;

        /// <summary>
        /// Все файлы локализации по порядку применения: база первой, дальше пакеты. Дубли путей отброшены
        /// (остаётся первое вхождение), базовая таблица среди пакетов не повторяется.
        /// </summary>
        public static List<LocPackageFile> Collect(LocSettings settings = null)
        {
            settings ??= LocSettings.Current;
            var list = new List<LocPackageFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var basePath = LocEditor.BaseTablePath;
            if (!string.IsNullOrEmpty(basePath))
            {
                var file = LocPackageFile.Create(basePath, Loc.BaseLayerId, false, LocSource.Base, "base", "base");
                file.IsBase = true;
                file.Provider = BaseProvider;
                list.Add(file);
                seen.Add(file.FullPath);
            }

            if (settings == null) return list;

            var found = new List<LocPackageFile>();
            if (UsesProviders)
            {
                foreach (var provider in Providers)
                {
                    var start = found.Count;
                    try { provider.Collect(settings, found); }
                    catch (Exception e)
                    {
                        Debug.LogError($"[Loc] Провайдер пакетов {provider.Name} упал: {e.Message}");
                        Debug.LogException(e);
                    }

                    for (var i = start; i < found.Count; i++)
                        if (found[i] != null) found[i].Provider ??= provider.Name;
                }
            }
            else
            {
                foreach (var path in MatchMasks(settings.PackageMasks, settings.PackageExcludes))
                {
                    var file = LocPackageFile.Create(path);
                    file.Provider = MasksProvider;
                    found.Add(file);
                }
            }

            foreach (var file in found)
            {
                if (file == null || string.IsNullOrEmpty(file.FullPath)) continue;
                if (!seen.Add(file.FullPath)) continue;
                file.Group ??= GroupOf(file.Path);
                list.Add(file);
            }

            DisambiguateNames(list);
            return list;
        }

        /// <summary>Подходит ли путь (от корня проекта) под маски пакетов из настроек.</summary>
        public static bool MatchesMasks(string projectPath, LocSettings settings = null)
        {
            settings ??= LocSettings.Current;
            if (settings == null || string.IsNullOrEmpty(projectPath)) return false;

            projectPath = projectPath.Replace('\\', '/');
            var included = false;
            foreach (var mask in settings.PackageMasks)
                if (!string.IsNullOrWhiteSpace(mask) && GlobRegex(mask).IsMatch(projectPath)) included = true;
            if (!included) return false;

            foreach (var mask in settings.PackageExcludes)
                if (!string.IsNullOrWhiteSpace(mask) && GlobRegex(mask).IsMatch(projectPath)) return false;
            return true;
        }

        /// <summary>Файлы проекта под масками (пути от корня через «/»), отсортированы по пути.</summary>
        public static List<string> MatchMasks(IEnumerable<string> masks, IEnumerable<string> excludes = null)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            var root = ProjectRoot;

            var exclude = new List<Regex>();
            if (excludes != null)
                foreach (var mask in excludes)
                    if (!string.IsNullOrWhiteSpace(mask)) exclude.Add(GlobRegex(mask.Trim()));

            foreach (var raw in masks)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var mask = raw.Trim().Replace('\\', '/').TrimStart('/');
                var regex = GlobRegex(mask);
                var start = FixedPrefix(mask);
                var dir = string.IsNullOrEmpty(start) ? root : root + "/" + start;
                if (!Directory.Exists(dir)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*" + LocFormat.Extension, SearchOption.AllDirectories); }
                catch (Exception) { continue; }

                foreach (var full in files)
                {
                    var relative = full.Substring(root.Length + 1).Replace('\\', '/');
                    if (relative.StartsWith("Library/", StringComparison.Ordinal) || relative.Contains("/NexusStates/")) continue;
                    if (!regex.IsMatch(relative)) continue;
                    if (exclude.Exists(r => r.IsMatch(relative))) continue;
                    result.Add(relative);
                }
            }

            return new List<string>(result);
        }

        /// <summary>Маска → регулярка по пути через «/»: ** — любые папки, * — часть имени, ? — один знак.</summary>
        public static Regex GlobRegex(string mask)
        {
            mask = (mask ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
            var sb = new StringBuilder("^");

            for (var i = 0; i < mask.Length; i++)
            {
                var ch = mask[i];
                if (ch == '*')
                {
                    if (i + 1 < mask.Length && mask[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < mask.Length && mask[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                }
                else if (ch == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(ch.ToString()));
            }

            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>Папка маски до первого подстановочного знака: с неё начинается обход.</summary>
        private static string FixedPrefix(string mask)
        {
            var wildcard = mask.IndexOfAny(new[] { '*', '?' });
            var head = wildcard < 0 ? mask : mask.Substring(0, wildcard);
            var slash = head.LastIndexOf('/');
            return slash < 0 ? string.Empty : head.Substring(0, slash);
        }

        public static string ToFullPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace('\\', '/');
            var full = System.IO.Path.IsPathRooted(path) ? path : ProjectRoot + "/" + path.TrimStart('/');
            return System.IO.Path.GetFullPath(full).Replace('\\', '/');
        }

        /// <summary>Путь от корня проекта. <c>null</c> — файл вне проекта.</summary>
        public static string ToProjectPath(string full)
        {
            if (string.IsNullOrEmpty(full)) return null;
            full = full.Replace('\\', '/');
            var root = ProjectRoot + "/";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : null;
        }

        /// <summary>Группа по пути: имя папки над файлом, а для папок phrases/Localization — ещё выше.</summary>
        private static string GroupOf(string path)
        {
            var dir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
            var name = System.IO.Path.GetFileName(dir);
            if (string.Equals(name, "phrases", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Localization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "loc", StringComparison.OrdinalIgnoreCase))
            {
                var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir) ?? string.Empty);
                if (!string.IsNullOrEmpty(parent)) return parent;
            }

            return string.IsNullOrEmpty(name) ? "—" : name;
        }

        /// <summary>Одинаковые имена файлов — к имени добавляется группа.</summary>
        private static void DisambiguateNames(List<LocPackageFile> list)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in list) counts[file.Name] = counts.TryGetValue(file.Name, out var n) ? n + 1 : 1;

            foreach (var file in list)
                if (counts[file.Name] > 1 && !file.IsBase && !string.IsNullOrEmpty(file.Group))
                    file.Name = file.Group + "/" + file.Name;
        }

        // ---------------------------------------------------------------- рантайм-список (Auto)

        /// <summary>
        /// Запекает пакеты каталога в настройки для <see cref="LocPackageLoading.Auto"/>: ассет .loc вне
        /// StreamingAssets — ссылкой, файл в StreamingAssets — путём. В режиме Project список очищается.
        /// true — настройки изменены.
        /// </summary>
        public static bool BakeRuntimeList(LocSettings settings = null)
        {
            settings ??= LocSettings.Current;
            if (settings == null) return false;

            var refs = new List<LocPackageRef>();
            if (settings.Enabled && settings.PackageLoading == LocPackageLoading.Auto)
            {
                const string streaming = "Assets/StreamingAssets/";
                foreach (var file in Collect(settings))
                {
                    if (file.IsBase) continue;

                    if (file.Path.StartsWith(streaming, StringComparison.OrdinalIgnoreCase))
                    {
                        refs.Add(new LocPackageRef { id = file.Id, streamingPath = file.Path.Substring(streaming.Length) });
                        continue;
                    }

                    var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(file.Path);
                    if (asset == null)
                    {
                        Debug.LogWarning($"[Loc] Пакет {file.Path} не попадёт в сборку: он не ассет проекта и не в StreamingAssets. " +
                                         "Грузите такие пакеты проектом (Package Loading → Project).");
                        continue;
                    }

                    refs.Add(new LocPackageRef { id = file.Id, asset = asset });
                }
            }

            if (SameRefs(settings.Packages, refs)) return false;

            var so = new SerializedObject(settings);
            var list = so.FindProperty("packages");
            list.arraySize = refs.Count;
            for (var i = 0; i < refs.Count; i++)
            {
                var item = list.GetArrayElementAtIndex(i);
                item.FindPropertyRelative("id").stringValue = refs[i].id;
                item.FindPropertyRelative("asset").objectReferenceValue = refs[i].asset;
                item.FindPropertyRelative("streamingPath").stringValue = refs[i].streamingPath ?? string.Empty;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(settings);
            return true;
        }

        private static bool SameRefs(IReadOnlyList<LocPackageRef> a, List<LocPackageRef> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
            {
                if (a[i] == null) return false;
                if (a[i].id != b[i].id || a[i].asset != b[i].asset || (a[i].streamingPath ?? string.Empty) != (b[i].streamingPath ?? string.Empty)) return false;
            }

            return true;
        }

        /// <summary>Перечитать провайдеры (после компиляции — само).</summary>
        [InitializeOnLoadMethod]
        private static void ResetProviders() => _providers = null;
    }
}
