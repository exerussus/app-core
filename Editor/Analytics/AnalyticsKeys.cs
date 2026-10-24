using System;
using System.Collections.Generic;
using System.Text;

namespace Exerussus.AppCore.Analytics.Editor
{
    /// <summary>
    /// Правила ключей и полей событий и генерация <c>AppEvents</c>.
    /// Каждый уровень ключа — вложенный класс; у события — <c>Id</c>, <c>Key</c> и типизированный <c>Raise</c>:
    /// <c>Game.Module.Focus</c> с полем <c>module:string</c> → <c>AppEvents.Game.Module.Focus.Raise(string module)</c>.
    /// </summary>
    public static class AnalyticsKeys
    {
        public const string RootClass = "AppEvents";
        public const string Namespace = "App";

        /// <summary>Первый уровень, занятый системными событиями ядра (<c>app.session.start</c> и т.п.).</summary>
        public const string SystemRoot = "app";

        private static readonly HashSet<string> ReservedMembers = new(StringComparer.Ordinal) { "Id", "Key", "Raise", "Count" };

        /// <summary>Имена полей, занятые служебными ключами строки события.</summary>
        private static readonly HashSet<string> ReservedFields = new(StringComparer.Ordinal) { "s", "t", "g", "e", "type", "__w" };

        private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
            "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
            "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
            "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
            "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        };

        // ---------------------------------------------------------------- проверка

        public static string ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "пустой ключ";
            var levels = key.Split('.');
            if (levels.Length > AnalyticsSettings.MaxDepth) return $"больше {AnalyticsSettings.MaxDepth} уровней";
            if (levels[0] == SystemRoot) return $"первый уровень «{SystemRoot}» занят системными событиями ядра";

            for (int i = 0; i < levels.Length; i++)
            {
                var level = levels[i];
                var error = ValidateIdentifier(level);
                if (error != null) return $"уровень {i + 1} «{level}»: {error}";
                if (ReservedMembers.Contains(level)) return $"уровень {i + 1} «{level}»: имя занято членом класса события";
                if (level == RootClass) return $"«{RootClass}» — имя корня";
                if (i > 0 && level == levels[i - 1]) return $"уровень не может называться как родитель («{levels[i - 1]}.{level}»)";
            }

            return null;
        }

        public static string ValidateField(string name)
        {
            var error = ValidateIdentifier(name);
            if (error != null) return error;
            if (ReservedFields.Contains(name)) return $"«{name}» занято служебным ключом строки события";
            return null;
        }

        private static string ValidateIdentifier(string id)
        {
            if (string.IsNullOrEmpty(id)) return "пустое имя";
            var first = id[0];
            if (!(IsLatin(first) || first == '_')) return "начинается с латинской буквы или «_»";
            for (int i = 1; i < id.Length; i++)
            {
                var c = id[i];
                if (!(IsLatin(c) || c == '_' || (c >= '0' && c <= '9'))) return $"недопустимый символ «{c}»: только латиница, цифры и «_»";
            }
            if (CSharpKeywords.Contains(id)) return $"«{id}» — ключевое слово C#";
            return null;
        }

        private static bool IsLatin(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        // ---------------------------------------------------------------- генерация

        /// <summary>
        /// Текст <c>AppEvents.cs</c>. <paramref name="entries"/> — каталог, отсортированный по ключу (ordinal):
        /// <c>Id</c> = индекс записи. Невалидные ключи и поля пропускаются (их отлавливает проверка каталога).
        /// </summary>
        public static string Generate(AnalyticsEventEntry[] entries)
        {
            var root = new Node(RootClass);
            for (int i = 0; i < entries.Length; i++)
            {
                if (ValidateKey(entries[i].key) != null) continue;
                var node = root;
                foreach (var level in entries[i].key.Split('.')) node = node.Child(level);
                node.Index = i;
                node.Entry = entries[i];
            }

            var sb = new StringBuilder(8192);
            sb.Append("// <auto-generated>\n");
            sb.Append("// AppCore: события аналитики. Не править: генерируется из ").Append(AnalyticsSettings.AssetPath).Append(".\n");
            sb.Append("// </auto-generated>\n\n");
            sb.Append("namespace ").Append(Namespace).Append("\n{\n");
            sb.Append("    /// <summary>События аналитики: <c>AppEvents.Уровень.Событие.Raise(...)</c>.</summary>\n");
            Write(sb, root, 1, entries.Length);
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, Node node, int depth, int count)
        {
            var pad = new string(' ', depth * 4);
            var inner = new string(' ', (depth + 1) * 4);

            var doc = node.Index >= 0 ? Doc(node.Entry) : string.Empty;
            if (doc.Length > 0) sb.Append(pad).Append("/// <summary>").Append(doc).Append("</summary>\n");
            sb.Append(pad).Append("public static class ").Append(node.Name).Append('\n').Append(pad).Append("{\n");

            if (depth == 1) sb.Append(inner).Append("public const int Count = ").Append(count).Append(";\n");

            if (node.Index >= 0) WriteEvent(sb, node, inner);

            // пустая строка — только между членами класса, не сразу после «{»
            var separate = depth == 1 || node.Index >= 0;
            foreach (var child in node.Children)
            {
                if (separate) sb.Append('\n');
                separate = true;
                Write(sb, child, depth + 1, count);
            }

            sb.Append(pad).Append("}\n");
        }

        private static void WriteEvent(StringBuilder sb, Node node, string pad)
        {
            var entry = node.Entry;
            var fields = new List<AnalyticsField>();
            if (entry.fields != null)
                foreach (var f in entry.fields)
                    if (ValidateField(f.name) == null && !fields.Exists(x => x.name == f.name)) fields.Add(f);

            sb.Append(pad).Append("public const int Id = ").Append(node.Index).Append(";\n");
            sb.Append(pad).Append("public const string Key = \"").Append(entry.key).Append("\";\n\n");

            foreach (var f in fields)
                if (!string.IsNullOrWhiteSpace(f.note))
                    sb.Append(pad).Append("/// <param name=\"").Append(f.name).Append("\">").Append(Escape(f.note)).Append("</param>\n");

            sb.Append(pad).Append("public static void Raise(");
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TypeOf(fields[i].type)).Append(' ').Append(fields[i].name);
            }
            sb.Append(")\n").Append(pad).Append("{\n");

            var body = pad + "    ";
            sb.Append(body).Append("var __w = global::Exerussus.AppCore.Analytics.AppAnalytics.Begin(Id);\n");
            if (fields.Count > 0)
            {
                sb.Append(body).Append("if (!__w.Active) return;\n");
                foreach (var f in fields)
                    sb.Append(body).Append("__w.Field(\"").Append(f.name).Append("\", ").Append(f.name).Append(");\n");
            }
            sb.Append(body).Append("__w.End();\n");
            sb.Append(pad).Append("}\n");
        }

        private static string TypeOf(AnalyticsFieldType type) => type switch
        {
            AnalyticsFieldType.Int => "long",
            AnalyticsFieldType.Float => "double",
            AnalyticsFieldType.Bool => "bool",
            _ => "string",
        };

        private static string Doc(AnalyticsEventEntry entry)
        {
            var title = (entry.title ?? string.Empty).Trim();
            var when = (entry.when ?? string.Empty).Trim().Replace('\n', ' ').Replace('\r', ' ');
            var text = title.Length > 0 && when.Length > 0 ? title + ". " + when : title + when;
            if (entry.disabled) text = text.Length > 0 ? "Выключено. " + text : "Выключено.";
            return Escape(text);
        }

        private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private sealed class Node
        {
            public readonly string Name;
            public readonly List<Node> Children = new();
            public int Index = -1;
            public AnalyticsEventEntry Entry;

            public Node(string name) => Name = name;

            public Node Child(string name)
            {
                foreach (var child in Children)
                    if (child.Name == name) return child;
                var node = new Node(name);
                Children.Add(node);
                return node;
            }
        }
    }
}
