using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Views;
using UnityEditor;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Реестр id вью и всё, что из него генерируется: записи навигации в <see cref="NavigationSettings"/>,
    /// <c>Navigation.uss</c>, <c>NavTargets.cs</c>. Без окон и диалогов — для инструментов (CLI, автоматизация).
    /// </summary>
    /// <remarks>
    /// Формат сгенерированных файлов совпадает со страницей App в Nexus: оба пути пишут одно и то же, файлы
    /// не «дёргаются» между ними.
    /// </remarks>
    public static class AppNavigation
    {
        /// <summary>Стандартная цель «назад» — класс есть всегда.</summary>
        public const string BackTargetId = "back-page";

        public const string DefaultPostfix = "__navigation";

        public static readonly Regex ValidClassRx = new(@"^[A-Za-z_][A-Za-z0-9_\-]*$", RegexOptions.Compiled);

        /// <summary>Допустимый id: латиница, цифры, «_» и «-», не пустой, без пробелов.</summary>
        public static readonly Regex ValidIdRx = new(@"^[A-Za-z0-9_\-]+$", RegexOptions.Compiled);

        public static string DefaultPrefix(ViewKind kind) => kind == ViewKind.Popup ? "open-" : "to-";

        public static string Compose(string prefix, string postfix, string id)
            => $"{prefix}{(id ?? string.Empty).Trim().ToLowerInvariant()}{postfix}";

        /// <summary>Аффиксы вида из NavigationSettings; пусто — дефолт.</summary>
        public static (string prefix, string postfix) Affixes(NavigationSettings s, ViewKind kind)
        {
            string p = null, q = null;
            if (s != null)
            {
                p = kind == ViewKind.Popup ? s.PopupPrefix : s.PagePrefix;
                q = kind == ViewKind.Popup ? s.PopupPostfix : s.PagePostfix;
            }

            return (string.IsNullOrEmpty(p) ? DefaultPrefix(kind) : p, string.IsNullOrEmpty(q) ? DefaultPostfix : q);
        }

        /// <summary>Навигационный класс вью (у фрагментов классов нет — null).</summary>
        public static string ClassOf(NavigationSettings s, ViewKind kind, string id)
        {
            if (kind == ViewKind.Fragment) return null;
            var (prefix, postfix) = Affixes(s, kind);
            return Compose(prefix, postfix, id);
        }

        public static long HashOf(ViewKind kind, string id) => kind switch
        {
            ViewKind.Popup => new PopupId(id).Id,
            ViewKind.Fragment => new FragmentId(id).Id,
            _ => new PageId(id).Id,
        };

        // ============================================================ реестр

        /// <summary>Проблема id для вида (null — id годится): формат, повтор, совпадение класса.</summary>
        public static string CheckNewId(ViewKind kind, string id)
        {
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0) return "пустой id";
            if (!ValidIdRx.IsMatch(id)) return $"id «{id}»: только латиница, цифры, «_» и «-»";

            var s = AppCoreAssets.FindNavigationSettings();
            if (s == null) return null;
            if (s.GetIds(kind).Contains(id)) return $"id «{id}» уже зарегистрирован ({kind})";

            var cls = ClassOf(s, kind, id);
            if (cls == null) return null;
            if (!ValidClassRx.IsMatch(cls)) return $"класс «{cls}» недопустим";
            foreach (var other in new[] { ViewKind.Page, ViewKind.Popup })
                foreach (var existing in s.GetIds(other))
                    if (string.Equals(ClassOf(s, other, existing), cls, StringComparison.Ordinal))
                        return $"класс «{cls}» уже занят ({other} «{existing}»)";
            return null;
        }

        /// <summary>Добавить id в реестр и перегенерировать навигацию. <c>false</c> — уже был.</summary>
        public static bool Register(ViewKind kind, string id)
        {
            id = (id ?? string.Empty).Trim();
            var s = AppCoreAssets.EnsureNavigationSettings();
            if (s.GetIds(kind).Contains(id)) return false;

            var problem = CheckNewId(kind, id);
            if (problem != null) throw new InvalidOperationException(problem);

            var ids = new List<string>(s.GetIds(kind)) { id };
            Undo.RecordObject(s, "Register App View Id");
            s.EditorSetIds(kind, ids);
            Regenerate(s);
            return true;
        }

        /// <summary>
        /// Убрать id из реестра и перегенерировать навигацию. <paramref name="stripClasses"/> — снять его
        /// навигационный класс со всех uxml проекта (число правок — в <paramref name="uxmlChanged"/>).
        /// </summary>
        public static bool Unregister(ViewKind kind, string id, bool stripClasses, out int uxmlChanged)
        {
            uxmlChanged = 0;
            var s = AppCoreAssets.FindNavigationSettings();
            if (s == null || !s.GetIds(kind).Contains(id)) return false;

            var cls = ClassOf(s, kind, id);
            var ids = new List<string>(s.GetIds(kind));
            ids.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal));
            Undo.RecordObject(s, "Unregister App View Id");
            s.EditorSetIds(kind, ids);
            Regenerate(s);

            if (stripClasses && cls != null)
                uxmlChanged = RewriteClassInAllUxml(new Dictionary<string, string>(StringComparer.Ordinal) { [cls] = null });
            return true;
        }

        /// <summary>Перезаписать записи навигации, <c>Navigation.uss</c> и <c>NavTargets.cs</c> из реестра.</summary>
        public static string Regenerate(NavigationSettings s = null)
        {
            s ??= AppCoreAssets.EnsureNavigationSettings();
            s.EditorSetEntries(BuildEntries(s));
            EditorUtility.SetDirty(s);
            AssetDatabase.SaveAssetIfDirty(s);

            WriteNavigationUss(s);
            WriteNavTargets(s, out var report);
            return report;
        }

        // ============================================================ Navigation.uss

        /// <summary>Полностью перезаписывает Navigation.uss: класс на каждую страницу и попап + стандартные.</summary>
        public static bool WriteNavigationUss(NavigationSettings s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("/* ============================================================");
            sb.AppendLine("   Navigation.uss — AUTO-GENERATED by Nexus / App.");
            sb.AppendLine("   One class per id: prefix + id.ToLower() + postfix.");
            sb.AppendLine("   Rebuilt from scratch on every Apply. Do not hand-edit.");
            sb.AppendLine("   ============================================================ */");
            sb.AppendLine();

            AppendSection(sb, s, ViewKind.Page, "Pages (PageId)", "PageId");
            AppendSection(sb, s, ViewKind.Popup, "Popups (PopupId)", "PopupId");

            var (prefix, postfix) = Affixes(s, ViewKind.Page);
            sb.AppendLine("/* ----- standard navigation classes (always present) ----- */");
            sb.Append('.').Append(Compose(prefix, postfix, BackTargetId)).AppendLine(" {");
            sb.AppendLine("}");

            var changed = AppCoreAssets.WriteTextIfChanged(AppCorePaths.NavigationUssPath, sb.ToString());
            if (changed) AssetDatabase.ImportAsset(AppCorePaths.NavigationUssPath);
            return changed;
        }

        private static void AppendSection(StringBuilder sb, NavigationSettings s, ViewKind kind, string title, string uidType)
        {
            var ids = s != null ? s.GetIds(kind) : Array.Empty<string>();
            if (ids.Count == 0) return;

            var (prefix, postfix) = Affixes(s, kind);
            sb.AppendLine($"/* ---- {title} ---- */");
            sb.AppendLine();

            foreach (var id in ids)
            {
                sb.AppendLine($".{Compose(prefix, postfix, id)} {{");
                sb.AppendLine($"    /* -> {uidType} \"{id}\" 0x{HashOf(kind, id):X16} */");
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        /// <summary>Записи «класс → цель» для рантайма (NavigationLink).</summary>
        public static List<NavigationSettings.NavigationEntry> BuildEntries(NavigationSettings s)
        {
            var entries = new List<NavigationSettings.NavigationEntry>();
            foreach (var kind in new[] { ViewKind.Page, ViewKind.Popup })
            {
                var (prefix, postfix) = Affixes(s, kind);
                foreach (var id in s.GetIds(kind))
                    entries.Add(new NavigationSettings.NavigationEntry
                    {
                        ClassName = Compose(prefix, postfix, id),
                        Bound = true,
                        Kind = kind == ViewKind.Popup ? NavigationSettings.EntryKind.Popup : NavigationSettings.EntryKind.Page,
                        Page = id,
                    });
            }

            return entries;
        }

        // ============================================================ NavTargets.cs

        /// <summary>Перезаписывает NavTargets.cs: PageId, PopupId и FragmentId на каждый id.</summary>
        public static bool WriteNavTargets(NavigationSettings s, out string report)
        {
            var pages = s != null ? s.Pages : Array.Empty<string>();
            var popups = s != null ? s.Popups : Array.Empty<string>();
            var fragments = s != null ? s.Fragments : Array.Empty<string>();

            if (pages.Count + popups.Count + fragments.Count == 0)
            {
                report = "Id нет — NavTargets.cs не тронут.";
                return false;
            }

            var body = new StringBuilder();
            body.AppendLine("// <auto-generated> Generated by App (Nexus) editor. Do not edit by hand. </auto-generated>");
            body.AppendLine("using Exerussus.AppCore.Navigation;");
            body.AppendLine("using Exerussus.AppCore.Views;");
            body.AppendLine();
            body.AppendLine($"namespace {AppCorePaths.GeneratedNamespace}");
            body.AppendLine("{");
            body.AppendLine("    public static partial class NavTargets");
            body.AppendLine("    {");

            var used = new HashSet<string>(StringComparer.Ordinal);
            var blocks = 0;
            blocks += Emit(body, used, pages, "PageId", "_Page", blocks);
            blocks += Emit(body, used, popups, "PopupId", "_Popup", blocks);
            blocks += Emit(body, used, fragments, "FragmentId", "_Fragment", blocks);

            body.AppendLine("    }");
            body.AppendLine("}");

            var changed = AppCoreAssets.WriteTextIfChanged(AppCorePaths.NavTargetsPath, body.ToString());
            if (changed) AssetDatabase.ImportAsset(AppCorePaths.NavTargetsPath);

            report = $"NavTargets: {pages.Count} PageId, {popups.Count} PopupId, {fragments.Count} FragmentId" +
                     (changed ? $" → {AppCorePaths.NavTargetsPath}" : " (без изменений)");
            return changed;
        }

        /// <summary>Имя поля NavTargets для id: <c>main_menu</c> → <c>MainMenu_Page</c> (без учёта дублей).</summary>
        public static string NavTargetName(ViewKind kind, string id) => AppNaming.TypeName(id) + kind switch
        {
            ViewKind.Popup => "_Popup",
            ViewKind.Fragment => "_Fragment",
            _ => "_Page",
        };

        private static int Emit(StringBuilder sb, HashSet<string> used, IReadOnlyList<string> ids, string type, string suffix, int blocksBefore)
        {
            if (ids.Count == 0) return 0;
            if (blocksBefore > 0) sb.AppendLine();

            foreach (var id in ids)
            {
                var ident = Unique(used, AppNaming.TypeName(id) + suffix);
                sb.AppendLine($"        public static readonly {type} {ident} = new {type}(\"{Escape(id)}\");");
            }

            return 1;
        }

        private static string Unique(HashSet<string> used, string ident)
        {
            var baseId = ident;
            var i = 2;
            while (!used.Add(ident)) ident = baseId + "_" + i++;
            return ident;
        }

        private static string Escape(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        // ============================================================ uxml: классы

        private static readonly Regex ClassAttrRx =
            new(@"class\s*=\s*(?<q>[""'])(?<val>.*?)\k<q>", RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly char[] WS = { ' ', '\t', '\n', '\r' };

        /// <summary>
        /// Переименовать (значение — новый класс) или снять (null) классы во всех uxml проекта одним проходом.
        /// Возвращает число изменённых файлов.
        /// </summary>
        public static int RewriteClassInAllUxml(Dictionary<string, string> map)
        {
            var changedFiles = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)) continue;

                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    continue;
                }

                var result = RewriteTokens(text, map, out var changed);
                if (!changed) continue;
                File.WriteAllText(path, result);
                AssetDatabase.ImportAsset(path);
                changedFiles++;
            }

            return changedFiles;
        }

        /// <summary>Сколько раз класс стоит в uxml проекта.</summary>
        public static int CountClassUses(string cls)
        {
            var count = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    foreach (Match m in ClassAttrRx.Matches(File.ReadAllText(path)))
                        foreach (var t in m.Groups["val"].Value.Split(WS, StringSplitOptions.RemoveEmptyEntries))
                            if (string.Equals(t, cls, StringComparison.Ordinal))
                                count++;
                }
                catch (IOException)
                {
                    // нечитаемый файл — пропускаем
                }
            }

            return count;
        }

        /// <summary>Единый проход: каждый токен ищется в map ровно один раз → цепочки X→Y→Z не возникают.</summary>
        public static string RewriteTokens(string text, Dictionary<string, string> map, out bool changed)
        {
            var any = false;
            var result = ClassAttrRx.Replace(text, m =>
            {
                var grp = m.Groups["val"];
                var parts = grp.Value.Split(WS, StringSplitOptions.RemoveEmptyEntries);
                var list = new List<string>(parts.Length);
                var local = false;

                foreach (var t in parts)
                {
                    if (map.TryGetValue(t, out var repl))
                    {
                        local = true;
                        if (repl == null) continue;
                        if (!list.Contains(repl)) list.Add(repl);
                    }
                    else if (!list.Contains(t))
                    {
                        list.Add(t);
                    }
                }

                if (!local) return m.Value;
                any = true;

                var rel = grp.Index - m.Index;
                return m.Value.Substring(0, rel) + string.Join(" ", list) + m.Value.Substring(rel + grp.Length);
            });

            changed = any;
            return result;
        }
    }
}
