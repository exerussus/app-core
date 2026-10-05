using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Сигнатура аргументов одной строкой — для тех, кто описывает команды данными (DSL, конфиги):
    /// <code>
    /// "item:string@item count:int=1 mode:fast|slow=fast who:target note:rest"
    /// </code>
    /// Элемент: <c>имя[?]:тип[@источник][=умолчание]</c>.
    /// Типы: <c>int</c>, <c>float</c>, <c>bool</c>, <c>string</c>, <c>target</c>, <c>rest</c>;
    /// варианты через <c>|</c> — выбор. <c>?</c> после имени или типа — необязательный;
    /// умолчание делает необязательным само. Пробелов внутри элемента нет.
    /// </summary>
    public static class CommandSignature
    {
        /// <summary>Разобрать сигнатуру. Ошибка формата — исключение с понятным текстом.</summary>
        public static ArgSpec[] Parse(string signature)
        {
            if (string.IsNullOrWhiteSpace(signature)) return Array.Empty<ArgSpec>();

            var result = new List<ArgSpec>();
            var i = 0;

            while (i < signature.Length)
            {
                while (i < signature.Length && char.IsWhiteSpace(signature[i])) i++;
                if (i >= signature.Length) break;

                int start = i;
                while (i < signature.Length && !char.IsWhiteSpace(signature[i])) i++;

                result.Add(ParseOne(signature.Substring(start, i - start)));
            }

            return result.ToArray();
        }

        /// <summary>Разобрать сигнатуру без исключения. false — <paramref name="error"/> объясняет, что не так.</summary>
        public static bool TryParse(string signature, out ArgSpec[] args, out string error)
        {
            try
            {
                args = Parse(signature);
                error = null;
                return true;
            }
            catch (ArgumentException e)
            {
                args = Array.Empty<ArgSpec>();
                error = e.Message;
                return false;
            }
        }

        private static ArgSpec ParseOne(string item)
        {
            int colon = item.IndexOf(':');
            if (colon <= 0) throw new ArgumentException($"Сигнатура: «{item}» — нужен вид «имя:тип».");

            string name = item.Substring(0, colon);
            string rest = item.Substring(colon + 1);
            var optional = false;

            if (name.EndsWith("?", StringComparison.Ordinal))
            {
                optional = true;
                name = name.Substring(0, name.Length - 1);
            }

            string defaultText = null;
            int eq = rest.IndexOf('=');
            if (eq >= 0)
            {
                defaultText = rest.Substring(eq + 1);
                rest = rest.Substring(0, eq);
            }

            string source = null;
            int at = rest.IndexOf('@');
            if (at >= 0)
            {
                source = rest.Substring(at + 1);
                rest = rest.Substring(0, at);
                if (source.Length == 0) throw new ArgumentException($"Сигнатура: «{item}» — пустое имя источника после @.");
            }

            if (rest.EndsWith("?", StringComparison.Ordinal))
            {
                optional = true;
                rest = rest.Substring(0, rest.Length - 1);
            }

            if (rest.IndexOf('|') >= 0)
            {
                string[] choices = rest.Split('|');
                for (var c = 0; c < choices.Length; c++)
                {
                    if (choices[c].Length == 0) throw new ArgumentException($"Сигнатура: «{item}» — пустой вариант выбора.");
                }

                return new ArgSpec(name, ArgKind.Choice, optional, defaultText, null, choices, source);
            }

            ArgKind kind;
            switch (rest.ToLowerInvariant())
            {
                case "int":
                case "long":
                    kind = ArgKind.Int;
                    break;
                case "float":
                case "double":
                case "number":
                    kind = ArgKind.Float;
                    break;
                case "bool":
                    kind = ArgKind.Bool;
                    break;
                case "":
                case "str":
                case "string":
                    kind = ArgKind.String;
                    break;
                case "target":
                    kind = ArgKind.Target;
                    break;
                case "rest":
                case "text":
                    kind = ArgKind.Rest;
                    break;
                default:
                    throw new ArgumentException($"Сигнатура: «{item}» — неизвестный тип «{rest}». " +
                                                "Есть int, float, bool, string, target, rest или варианты через |.");
            }

            return new ArgSpec(name, kind, optional, defaultText, null, null, source);
        }
    }
}
