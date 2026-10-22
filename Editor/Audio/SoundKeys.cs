using System;
using System.Collections.Generic;
using System.Text;

namespace Exerussus.AppCore.Audio.Editor
{
    /// <summary>
    /// Правила ключей звуков и генерация констант <c>AppSounds</c>.
    /// Ключ — путь по точкам, 1–5 уровней; уровень — идентификатор C#. Константа события —
    /// <c>&lt;последний уровень&gt;_S</c> в классе родителя: <c>UI.Effects.Click</c> → <c>AppSounds.UI.Effects.Click_S</c>.
    /// </summary>
    public static class SoundKeys
    {
        public const string RootClass = "AppSounds";
        public const string Namespace = "App";
        public const string Suffix = "_S";

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

        /// <summary>Ошибка в имени уровня или <c>null</c>. <paramref name="parent"/> — предыдущий уровень (или null).</summary>
        public static string ValidateLevel(string level, string parent)
        {
            if (string.IsNullOrEmpty(level)) return "пустой уровень";
            if (level.IndexOf('.') >= 0) return "точка в имени уровня запрещена — она разделитель";

            char first = level[0];
            if (!(IsLatin(first) || first == '_')) return "уровень начинается с латинской буквы или «_»";
            for (int i = 1; i < level.Length; i++)
            {
                char c = level[i];
                if (!(IsLatin(c) || c == '_' || (c >= '0' && c <= '9')))
                    return $"недопустимый символ «{c}»: только латиница, цифры и «_»";
            }

            if (CSharpKeywords.Contains(level)) return $"«{level}» — ключевое слово C#";
            if (level.EndsWith(Suffix, StringComparison.Ordinal)) return $"окончание «{Suffix}» зарезервировано под константы событий";
            if (level == RootClass) return $"«{RootClass}» — имя корня";
            if (parent == null && level == "Count") return "«Count» в корне занят константой числа ключей";
            if (parent != null && level == parent) return $"уровень не может называться как родитель («{parent}.{level}»)";
            return null;
        }

        /// <summary>Ошибка в ключе целиком или <c>null</c>.</summary>
        public static string ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "пустой ключ";
            string[] levels = key.Split('.');
            if (levels.Length > SoundSettings.MaxDepth) return $"больше {SoundSettings.MaxDepth} уровней";

            for (int i = 0; i < levels.Length; i++)
            {
                string error = ValidateLevel(levels[i], i > 0 ? levels[i - 1] : null);
                if (error != null) return $"уровень {i + 1} «{levels[i]}»: {error}";
            }
            return null;
        }

        /// <summary>Константа события: <c>AppSounds.UI.Effects.Click_S</c>.</summary>
        public static string ConstantOf(string key) => RootClass + "." + key + Suffix;

        /// <summary>Папка клипов ключа: <c>Assets/AppSounds/UI/Effects/Click</c>.</summary>
        public static string FolderOf(string key) => SoundSettings.ClipsRoot + "/" + key.Replace('.', '/');

        /// <summary>Имя файла клипа: <c>Click_01.wav</c>.</summary>
        public static string ClipFileName(string key, int number, string extension) =>
            LastLevel(key) + "_" + number.ToString("00") + extension.ToLowerInvariant();

        public static string LastLevel(string key)
        {
            int dot = key.LastIndexOf('.');
            return dot < 0 ? key : key.Substring(dot + 1);
        }

        private static bool IsLatin(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        // ---------------------------------------------------------------- генерация

        /// <summary>
        /// Текст <c>AppSounds.cs</c>. <paramref name="entries"/> — каталог, отсортированный по ключу (ordinal):
        /// номер константы = индекс записи. Невалидные ключи пропускаются (их отлавливает проверка каталога).
        /// </summary>
        public static string Generate(SoundEntry[] entries)
        {
            var root = new Node(RootClass);
            for (int i = 0; i < entries.Length; i++)
            {
                string key = entries[i].key;
                if (ValidateKey(key) != null) continue;

                string[] levels = key.Split('.');
                Node node = root;
                for (int l = 0; l < levels.Length - 1; l++) node = node.Child(levels[l]);
                node.Events.Add((levels[^1], i, entries[i]));
            }

            var sb = new StringBuilder(4096);
            sb.Append("// <auto-generated>\n");
            sb.Append("// AppCore: ключи звуков. Не править: генерируется из ").Append(SoundSettings.AssetPath)
              .Append(" (Nexus → App → Звуки).\n");
            sb.Append("// </auto-generated>\n\n");
            sb.Append("namespace ").Append(Namespace).Append('\n').Append("{\n");
            sb.Append("    /// <summary>Номера звуковых событий для <c>AppSound.Play</c>. Константа события — последний уровень + «")
              .Append(Suffix).Append("».</summary>\n");
            Write(sb, root, 1, entries.Length);
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, Node node, int depth, int count)
        {
            string pad = new(' ', depth * 4);
            sb.Append(pad).Append("public static class ").Append(node.Name).Append('\n').Append(pad).Append("{\n");

            string inner = new(' ', (depth + 1) * 4);
            if (depth == 1) sb.Append(inner).Append("public const int Count = ").Append(count).Append(";\n\n");

            foreach ((string name, int index, SoundEntry entry) in node.Events)
            {
                string doc = Doc(entry);
                if (doc.Length > 0) sb.Append(inner).Append("/// <summary>").Append(doc).Append("</summary>\n");
                sb.Append(inner).Append("public const int ").Append(name).Append(Suffix).Append(" = ").Append(index).Append(";\n");
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                if (i > 0 || node.Events.Count > 0) sb.Append('\n');
                Write(sb, node.Children[i], depth + 1, count);
            }

            sb.Append(pad).Append("}\n");
        }

        private static string Doc(SoundEntry entry)
        {
            string title = (entry.title ?? string.Empty).Trim();
            string when = (entry.when ?? string.Empty).Trim().Replace('\n', ' ').Replace('\r', ' ');
            string text = title.Length > 0 && when.Length > 0 ? title + ". " + when : title + when;
            if (entry.isDefault) text = text.Length > 0 ? "Дефолт. " + text : "Дефолт.";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private sealed class Node
        {
            public readonly string Name;
            public readonly List<Node> Children = new();
            public readonly List<(string, int, SoundEntry)> Events = new();

            public Node(string name) => Name = name;

            public Node Child(string name)
            {
                foreach (Node child in Children)
                    if (child.Name == name) return child;
                var node = new Node(name);
                Children.Add(node);
                return node;
            }
        }
    }
}
