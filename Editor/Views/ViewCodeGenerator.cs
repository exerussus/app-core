using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Кодогенерация вью: контроллер <c>&lt;Type&gt;&lt;Kind&gt;Controller</c> и его вторая половина
    /// <c>.Elements.cs</c> с типизированными полями всех именованных элементов uxml.
    /// </summary>
    /// <remarks>
    /// <para>
    /// В поля попадает КАЖДЫЙ элемент с атрибутом <c>name</c>: уникальное имя в вёрстке — это
    /// уже намерение к ней привязаться, отдельная пометка не нужна. Повтор имени — ошибка
    /// валидатора, и поле для такого имени не создаётся.
    /// </para>
    /// <para>
    /// Тип поля берётся из uxml как есть: namespace xml-тега в UXML — это namespace C#-типа
    /// (<c>ui:Button</c> → <c>UnityEngine.UIElements.Button</c>, <c>ac:FragmentHost</c> →
    /// <c>Exerussus.AppCore.Views.FragmentHost</c>).
    /// </para>
    /// </remarks>
    public static class ViewCodeGenerator
    {
        public sealed class NamedElement
        {
            public string Name;
            public string TypeName;
            public string Field;
        }

        private const string DefaultUiNamespace = "UnityEngine.UIElements";

        private static readonly HashSet<string> SkippedTags = new(StringComparer.Ordinal)
        {
            "UXML", "Template", "Style", "AttributeOverrides",
        };

        // ============================================================ разбор uxml

        /// <summary>Именованные элементы вёрстки. Повторы имён — в <paramref name="duplicates"/>.</summary>
        public static List<NamedElement> ReadNamedElements(string uxmlText, out List<string> duplicates,
                                                          out List<string> skipped)
        {
            duplicates = new List<string>();
            skipped = new List<string>();
            var result = new List<NamedElement>();
            if (string.IsNullOrWhiteSpace(uxmlText)) return result;

            var doc = XDocument.Parse(uxmlText, LoadOptions.None);
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var el in doc.Descendants())
            {
                var local = el.Name.LocalName;
                if (SkippedTags.Contains(local)) continue;

                var name = ((string)el.Attribute("name"))?.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                byName.TryGetValue(name, out var count);
                byName[name] = count + 1;
            }

            var usedFields = new HashSet<string>(StringComparer.Ordinal);
            var emitted = new HashSet<string>(StringComparer.Ordinal);

            foreach (var el in doc.Descendants())
            {
                var local = el.Name.LocalName;
                if (SkippedTags.Contains(local)) continue;

                var name = ((string)el.Attribute("name"))?.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                if (byName[name] > 1)
                {
                    if (!duplicates.Contains(name)) duplicates.Add(name);
                    continue;
                }

                var ns = el.Name.NamespaceName;
                if (string.IsNullOrEmpty(ns)) ns = DefaultUiNamespace;

                // Редакторные элементы не существуют в плеере — рантайм-поле под них не скомпилируется.
                if (ns.StartsWith("UnityEditor", StringComparison.Ordinal))
                {
                    skipped.Add(name);
                    continue;
                }

                var typeName = local == "Instance"
                    ? "global::UnityEngine.UIElements.TemplateContainer"
                    : $"global::{ns}.{local}";

                if (!emitted.Add(name)) continue;

                result.Add(new NamedElement
                {
                    Name = name,
                    TypeName = typeName,
                    Field = Unique(usedFields, "_" + ToCamel(name)),
                });
            }

            return result;
        }

        // ============================================================ контроллер

        /// <summary>Базовый класс контроллера для вида вью.</summary>
        public static string BaseTypeName(ViewKind kind) => kind switch
        {
            ViewKind.Popup => nameof(AppPopupController),
            ViewKind.Fragment => nameof(AppFragmentController),
            _ => nameof(AppPageController),
        };

        /// <summary>
        /// Создаёт контроллер (если его нет) и его половину с элементами.
        /// Существующий файл контроллера не перезаписывается — только помечается <c>partial</c>.
        /// </summary>
        /// <returns><c>true</c>, если на диске что-то изменилось (будет перекомпиляция).</returns>
        public static bool GenerateController(ViewKind kind, string id, out string message)
        {
            var controllerPath = AppCorePaths.ControllerPath(kind, id);
            var className = AppNaming.ControllerName(id, kind);
            var changed = false;

            if (!File.Exists(controllerPath))
            {
                var code = BuildControllerFile(AppCorePaths.ControllersNamespace(kind), className, BaseTypeName(kind));
                AppCoreAssets.WriteTextIfChanged(controllerPath, code);
                changed = true;
            }
            else
            {
                changed |= EnsurePartial(controllerPath, className);
            }

            changed |= RegenerateElements(kind, id, out var elementsMessage);

            if (changed) AssetDatabase.Refresh();
            message = $"{className}: {controllerPath}" + (string.IsNullOrEmpty(elementsMessage) ? "" : "; " + elementsMessage);
            return changed;
        }

        /// <summary>
        /// Перегенерирует <c>.Elements.cs</c>, если у вью есть контроллер. Пишет только при изменении.
        /// </summary>
        public static bool RegenerateElements(ViewKind kind, string id, out string message)
        {
            message = null;

            var controllerPath = AppCorePaths.ControllerPath(kind, id);
            if (!File.Exists(controllerPath)) return false;

            var uxmlPath = AppCorePaths.ViewUxmlPath(kind, id);
            var className = AppNaming.ControllerName(id, kind);

            List<NamedElement> elements;
            try
            {
                elements = File.Exists(uxmlPath)
                    ? ReadNamedElements(File.ReadAllText(uxmlPath), out _, out _)
                    : new List<NamedElement>();

                if (elements.Count == 0 && !File.Exists(uxmlPath))
                    message = "uxml нет — поля элементов пусты";
            }
            catch (Exception e)
            {
                message = $"uxml не разобран ({e.Message}) — поля не обновлены";
                return false;
            }

            var ns = ReadNamespace(File.ReadAllText(controllerPath)) ?? AppCorePaths.ControllersNamespace(kind);
            var code = BuildElementsFile(ns, className, Path.GetFileName(uxmlPath), elements);
            var path = AppCorePaths.ControllerElementsPath(kind, id);

            var changed = AppCoreAssets.WriteTextIfChanged(path, code);
            if (changed) AssetDatabase.ImportAsset(path);
            return changed;
        }

        public static string BuildControllerFile(string ns, string className, string baseType)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Cysharp.Threading.Tasks;");
            sb.AppendLine("using Exerussus.AppCore.Views;");
            sb.AppendLine("using UnityEngine.UIElements;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    public partial class {className} : {baseType}");
            sb.AppendLine("    {");
            sb.AppendLine("        public override void Initialize()");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public override UniTask OnActivate() => UniTask.CompletedTask;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string BuildElementsFile(string ns, string className, string uxmlName, List<NamedElement> elements)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine($"//   AppCore: поля именованных элементов из {uxmlName}.");
            sb.AppendLine("//   Перегенерируется при каждом импорте uxml. Руками не править.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#pragma warning disable CS0414");
            sb.AppendLine("using UnityEngine.UIElements;");
            sb.AppendLine();

            // File-scoped namespace основного файла здесь не нужен: блочный объявляет тот же namespace.
            var cleanNs = ns?.TrimEnd(';').Trim();
            var indent = "    ";

            if (!string.IsNullOrEmpty(cleanNs))
            {
                sb.AppendLine($"namespace {cleanNs}");
                sb.AppendLine("{");
            }
            else
            {
                indent = "";
            }

            sb.AppendLine($"{indent}public partial class {className}");
            sb.AppendLine($"{indent}{{");

            foreach (var el in elements)
                sb.AppendLine($"{indent}    private {el.TypeName} {el.Field};");

            if (elements.Count > 0) sb.AppendLine();

            sb.AppendLine($"{indent}    protected override void BindElements()");
            sb.AppendLine($"{indent}    {{");
            foreach (var el in elements)
                sb.AppendLine($"{indent}        {el.Field} = Root.Q<{el.TypeName}>(\"{Escape(el.Name)}\");");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");

            if (!string.IsNullOrEmpty(cleanNs)) sb.AppendLine("}");

            return sb.ToString();
        }

        // ============================================================ существующий код

        private static readonly Regex NamespaceRx = new(@"^\s*namespace\s+([A-Za-z_][\w\.]*)\s*(;)?", RegexOptions.Multiline);

        /// <summary>Namespace файла. File-scoped возвращается с точкой с запятой на конце.</summary>
        public static string ReadNamespace(string code)
        {
            var m = NamespaceRx.Match(code ?? string.Empty);
            if (!m.Success) return null;
            return m.Groups[1].Value + (m.Groups[2].Success ? ";" : "");
        }

        /// <summary>Дописывает <c>partial</c> в объявление класса. <c>true</c> — файл изменён.</summary>
        public static bool EnsurePartial(string path, string className)
        {
            var code = File.ReadAllText(path);
            var rx = new Regex(@"((?:public|internal|sealed|abstract|\s)*)\bclass\s+" + Regex.Escape(className) + @"\b");
            var m = rx.Match(code);
            if (!m.Success) return false;
            if (Regex.IsMatch(m.Value, @"\bpartial\b")) return false;

            var updated = code.Substring(0, m.Index) +
                          Regex.Replace(m.Value, @"\bclass\b", "partial class") +
                          code.Substring(m.Index + m.Length);

            File.WriteAllText(path, updated, new UTF8Encoding(HasBom(path)));
            return true;
        }

        private static bool HasBom(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        // ============================================================ имена

        private static string ToCamel(string raw)
        {
            var words = SplitWords(raw);
            if (words.Count == 0) return "element";

            var sb = new StringBuilder();
            for (var i = 0; i < words.Count; i++)
                sb.Append(i == 0 ? Decapitalize(words[i]) : Capitalize(words[i]));

            var s = sb.ToString();
            if (s.Length == 0) return "element";
            if (char.IsDigit(s[0])) s = "_" + s;
            return s;
        }

        // Дробим по любым не-буквенно-цифровым и по границам camelCase:
        //   projectList → [project, List]; project-list → [project, list]; HUDPanel → [HUD, Panel].
        private static List<string> SplitWords(string raw)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(raw)) return words;

            var cur = new StringBuilder();
            char prev = '\0', prev2 = '\0';

            foreach (var ch in raw)
            {
                if (!char.IsLetterOrDigit(ch))
                {
                    Flush(cur, words);
                    prev = prev2 = '\0';
                    continue;
                }

                if (cur.Length > 0 && char.IsUpper(ch) && (char.IsLower(prev) || char.IsDigit(prev)))
                {
                    Flush(cur, words);
                }
                else if (cur.Length > 0 && char.IsLower(ch) && char.IsUpper(prev) && char.IsUpper(prev2))
                {
                    var last = cur[cur.Length - 1];
                    cur.Remove(cur.Length - 1, 1);
                    Flush(cur, words);
                    cur.Append(last);
                }

                cur.Append(ch);
                prev2 = prev;
                prev = ch;
            }

            Flush(cur, words);
            return words;

            static void Flush(StringBuilder b, List<string> acc)
            {
                if (b.Length == 0) return;
                acc.Add(b.ToString());
                b.Clear();
            }
        }

        private static string Capitalize(string w) =>
            string.IsNullOrEmpty(w) ? w : char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant();

        private static string Decapitalize(string w)
        {
            if (string.IsNullOrEmpty(w)) return w;
            var allUpper = w.All(c => !char.IsLetter(c) || char.IsUpper(c));
            return allUpper ? w.ToLowerInvariant() : char.ToLowerInvariant(w[0]) + w.Substring(1);
        }

        private static string Unique(HashSet<string> used, string ident)
        {
            var baseId = ident;
            var i = 2;
            while (!used.Add(ident)) ident = baseId + "_" + i++;
            return ident;
        }

        private static string Escape(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Перегенерация полей элементов при импорте uxml вью. Трогает только вью, у которых уже
    /// есть контроллер, и пишет файл только при реальном изменении — лишней компиляции нет.
    /// </summary>
    internal sealed class ViewUxmlPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported)
            {
                if (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryResolveView(path, out var kind, out var id)) continue;

                try { ViewCodeGenerator.RegenerateElements(kind, id, out _); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        /// <summary>
        /// Вью по пути uxml: <c>Assets/App/&lt;Kind&gt;/&lt;Type&gt;/&lt;id&gt;.uxml</c>. Id — имя файла,
        /// и путь обязан совпадать с каноническим: чужие uxml в этих папках не трогаем.
        /// </summary>
        public static bool TryResolveView(string path, out ViewKind kind, out string id)
        {
            path = path.Replace('\\', '/');
            id = Path.GetFileNameWithoutExtension(path);

            foreach (ViewKind k in Enum.GetValues(typeof(ViewKind)))
            {
                if (!path.StartsWith(AppCorePaths.KindDir(k) + "/", StringComparison.Ordinal)) continue;

                kind = k;
                return string.Equals(AppCorePaths.ViewUxmlPath(k, id), path, StringComparison.Ordinal);
            }

            kind = ViewKind.Page;
            return false;
        }
    }
}
