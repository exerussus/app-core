using System;
using System.Collections.Generic;
using System.Text;

namespace Exerussus.AppCore.Localization
{
    /// <summary>Результат разбора <c>.loc</c>: таблица и ошибки с номерами строк.</summary>
    public sealed class LocParseResult
    {
        public LocTable Table;
        public readonly List<LocError> Errors = new();

        public bool Ok => Errors.Count == 0;

        /// <summary>Ошибки одной строкой — для лога.</summary>
        public string ErrorText(string source = null, int max = 10)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Errors.Count && i < max; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                if (!string.IsNullOrEmpty(source)) sb.Append(source).Append(", ");
                sb.Append(Errors[i]);
            }

            if (Errors.Count > max) sb.Append($"\n… и ещё {Errors.Count - max}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Чтение и запись строкового формата <c>.loc</c>.
    /// </summary>
    /// <remarks>
    /// <code>
    /// @language ru Русский plural=ru
    /// @language en English fallback=ru
    ///
    /// # комментарий над ключом
    /// MENU_PLAY
    ///     ru: Играть
    ///     en: Play
    ///
    /// SCENARIO_COUNT
    ///     ru.one: {0} сценарий
    ///     ru.few: {0} сценария
    ///     ru: {0} сценариев
    ///     en.one: {0} scenario
    ///     en: {0} scenarios
    ///
    /// LONG_TEXT
    ///     ru: Первая строка
    ///         вторая строка
    ///
    /// OLD_KEY ~          (патч: удалить ключ)
    /// OTHER
    ///     pl: ~          (патч: удалить значение на языке)
    /// </code>
    /// Ключ — с нулевого отступа, значения — с отступом. Строка с отступом глубже значения
    /// продолжает его через перевод строки. Экранирование: <c>\n</c>, <c>\\</c>, <c>\s</c> (пробел),
    /// <c>\~</c>, <c>\#</c>, <c>\"</c>. Значение в кавычках сохраняет пробелы по краям.
    /// <c>#</c> — комментарий, только первым символом строки (после отступа). Табуляция
    /// равна четырём пробелам. BOM, LF и CRLF допустимы.
    /// </remarks>
    public static class LocFormat
    {
        public const string Extension = ".loc";

        private const int TabWidth = 4;

        // ================================================================ чтение

        public static LocParseResult Parse(string text)
        {
            var result = new LocParseResult { Table = new LocTable() };
            var table = result.Table;

            if (string.IsNullOrEmpty(text)) return result;
            if (text[0] == '﻿') text = text.Substring(1);

            var lines = text.Split('\n');
            var comments = new List<string>();
            var seenKey = false;

            LocEntry entry = null;
            LocValue value = null;
            var valueIndent = 0;
            StringBuilder continuation = null;

            void FlushValue()
            {
                if (value == null) return;

                if (continuation != null)
                {
                    value.Text = continuation.ToString();
                    continuation = null;
                }

                if (value.Text != null && value.Text.Length == 0)
                    result.Errors.Add(new LocError(value.Line, $"пустое значение {entry.Key} / {value.Slot}"));

                value = null;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNo = i + 1;
                var raw = lines[i];
                if (raw.Length > 0 && raw[raw.Length - 1] == '\r') raw = raw.Substring(0, raw.Length - 1);

                var indent = Indent(raw, out var start);
                var body = raw.Substring(start).TrimEnd();

                if (body.Length == 0) continue;

                // Продолжение многострочного значения: глубже строки значения.
                if (value != null && indent > valueIndent && value.Text != null)
                {
                    continuation ??= new StringBuilder(value.Text);
                    if (continuation.Length > 0) continuation.Append('\n');
                    continuation.Append(Unescape(body));
                    continue;
                }

                FlushValue();

                if (body[0] == '#')
                {
                    comments.Add(body);
                    continue;
                }

                if (indent == 0)
                {
                    if (body[0] == '@')
                    {
                        ParseDirective(body, lineNo, table, result);
                        if (!seenKey) table.HeaderComments.AddRange(comments);
                        comments.Clear();
                        continue;
                    }

                    entry = ParseKeyLine(body, lineNo, result);
                    if (entry == null) continue;

                    seenKey = true;
                    entry.Comments.AddRange(comments);
                    comments.Clear();

                    if (!table.AddEntry(entry))
                    {
                        result.Errors.Add(new LocError(lineNo, $"ключ {entry.Key} уже есть в файле"));
                        entry = null;
                    }

                    continue;
                }

                // Значение.
                if (entry == null)
                {
                    result.Errors.Add(new LocError(lineNo, "значение без ключа: ключ пишется с начала строки, значения — с отступом"));
                    continue;
                }

                if (entry.RemoveKey)
                {
                    result.Errors.Add(new LocError(lineNo, $"у удаляемого ключа {entry.Key} не может быть значений"));
                    continue;
                }

                value = ParseValueLine(body, lineNo, entry, result);
                if (value == null) continue;

                // Комментарии между значениями не теряем: уходят к ключу.
                entry.Comments.AddRange(comments);
                comments.Clear();

                valueIndent = indent;
                if (entry.Find(value.Language, value.Form) != null)
                {
                    result.Errors.Add(new LocError(lineNo, $"значение {entry.Key} / {value.Slot} уже задано выше"));
                    value = null;
                    continue;
                }

                entry.Values.Add(value);

                // Пустое после двоеточия — текст целиком в строках продолжения.
                if (value.Text != null && value.Text.Length == 0) continuation = new StringBuilder();
            }

            FlushValue();

            if (seenKey) table.TrailingComments.AddRange(comments);
            else table.HeaderComments.AddRange(comments);

            return result;
        }

        private static int Indent(string line, out int start)
        {
            var indent = 0;
            start = 0;

            while (start < line.Length)
            {
                var ch = line[start];
                if (ch == ' ') indent++;
                else if (ch == '\t') indent += TabWidth;
                else break;
                start++;
            }

            return indent;
        }

        private static void ParseDirective(string body, int lineNo, LocTable table, LocParseResult result)
        {
            var parts = SplitWords(body);

            if (parts[0] != "@language")
            {
                result.Errors.Add(new LocError(lineNo, $"неизвестная директива {parts[0]} (есть только @language)"));
                return;
            }

            if (parts.Count < 2 || !LocSlots.IsValidLanguage(parts[1]))
            {
                result.Errors.Add(new LocError(lineNo, "@language: нужен код языка (латиница, цифры, -)"));
                return;
            }

            var language = new LocLanguage { Code = parts[1], Line = lineNo };
            var name = new StringBuilder();

            for (var i = 2; i < parts.Count; i++)
            {
                var part = parts[i];
                var eq = part.IndexOf('=');

                if (eq <= 0)
                {
                    if (name.Length > 0) name.Append(' ');
                    name.Append(part);
                    continue;
                }

                var option = part.Substring(0, eq);
                var optionValue = part.Substring(eq + 1);

                switch (option)
                {
                    case "plural":
                        if (!PluralRules.Has(optionValue))
                            result.Errors.Add(new LocError(lineNo, $"неизвестное правило окончаний «{optionValue}» (есть: {string.Join(", ", PluralRules.Names)})"));
                        language.Plural = optionValue;
                        break;
                    case "fallback":
                        language.Fallback = optionValue;
                        break;
                    default:
                        result.Errors.Add(new LocError(lineNo, $"@language: неизвестный параметр {option} (есть plural=, fallback=)"));
                        break;
                }
            }

            language.Name = name.Length > 0 ? name.ToString() : null;

            if (table.GetLanguage(language.Code) != null)
            {
                result.Errors.Add(new LocError(lineNo, $"язык {language.Code} уже объявлен"));
                return;
            }

            table.AddLanguageHeader(language);
        }

        /// <summary>Слова через пробел; кавычки объединяют слова с пробелами.</summary>
        private static List<string> SplitWords(string body)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;

            foreach (var ch in body)
            {
                if (ch == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (!quoted && (ch == ' ' || ch == '\t'))
                {
                    if (sb.Length > 0) parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }

                sb.Append(ch);
            }

            if (sb.Length > 0) parts.Add(sb.ToString());
            return parts;
        }

        private static LocEntry ParseKeyLine(string body, int lineNo, LocParseResult result)
        {
            var key = body;
            var remove = false;

            var space = body.IndexOfAny(new[] { ' ', '\t' });
            if (space > 0)
            {
                key = body.Substring(0, space);
                var rest = body.Substring(space).Trim();

                if (rest == "~") remove = true;
                else
                {
                    result.Errors.Add(new LocError(lineNo, $"после ключа {key} ожидается конец строки или ~, а не «{rest}». Значения пишутся строками ниже, с отступом"));
                    return null;
                }
            }

            if (key.Length > 0 && key[0] == '^') key = key.Substring(1);

            if (!LocSlots.IsValidKey(key))
            {
                result.Errors.Add(new LocError(lineNo, $"недопустимый ключ «{key}»: латиница, цифры, _ . -"));
                return null;
            }

            return new LocEntry { Key = key, RemoveKey = remove, Line = lineNo };
        }

        private static LocValue ParseValueLine(string body, int lineNo, LocEntry entry, LocParseResult result)
        {
            var colon = body.IndexOf(':');
            if (colon <= 0)
            {
                result.Errors.Add(new LocError(lineNo, $"значение {entry.Key}: ожидается «язык: текст»"));
                return null;
            }

            var slot = body.Substring(0, colon).Trim();
            var text = body.Substring(colon + 1).Trim();

            string language = slot, form = null;
            var dot = slot.IndexOf('.');
            if (dot > 0)
            {
                language = slot.Substring(0, dot);
                form = slot.Substring(dot + 1).ToLowerInvariant();

                if (!LocSlots.IsForm(form))
                {
                    result.Errors.Add(new LocError(lineNo, $"неизвестная форма «{form}» (есть: {string.Join(", ", LocSlots.Forms)})"));
                    return null;
                }

                if (form == "other") form = null;   // other — это и есть основное значение
            }

            if (!LocSlots.IsValidLanguage(language))
            {
                result.Errors.Add(new LocError(lineNo, $"недопустимый код языка «{language}»"));
                return null;
            }

            var value = new LocValue { Language = language, Form = form, Line = lineNo };

            if (text == "~")
            {
                value.Text = null;
                return value;
            }

            // Кавычки по краям сохраняют пробелы: снимаем их до экранирования.
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"' && !EscapedAt(text, text.Length - 1))
                text = text.Substring(1, text.Length - 2);

            value.Text = Unescape(text);
            return value;
        }

        /// <summary>Символ экранирован: перед ним нечётное число обратных косых.</summary>
        private static bool EscapedAt(string text, int index)
        {
            var slashes = 0;
            for (var i = index - 1; i >= 0 && text[i] == '\\'; i--) slashes++;
            return slashes % 2 == 1;
        }

        /// <summary>Раскрывает экранирование. Неизвестная пара остаётся как есть.</summary>
        public static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text;

            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch != '\\' || i == text.Length - 1)
                {
                    sb.Append(ch);
                    continue;
                }

                var next = text[++i];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 's': sb.Append(' '); break;
                    case '\\': sb.Append('\\'); break;
                    case '~': sb.Append('~'); break;
                    case '#': sb.Append('#'); break;
                    case '"': sb.Append('"'); break;
                    default: sb.Append('\\').Append(next); break;
                }
            }

            return sb.ToString();
        }

        // ================================================================ запись

        /// <summary>
        /// Пишет таблицу. Ключи — по алфавиту, значения — в порядке языков таблицы, формы — основная
        /// первой. Комментарии над ключами сохраняются. Перевод строки — LF.
        /// </summary>
        public static string Write(LocTable table)
        {
            var sb = new StringBuilder();

            foreach (var comment in table.HeaderComments) sb.Append(comment).Append('\n');

            foreach (var language in table.Languages)
            {
                sb.Append("@language ").Append(language.Code);
                if (!string.IsNullOrEmpty(language.Name))
                    sb.Append(' ').Append(language.Name.IndexOf(' ') >= 0 ? "\"" + language.Name + "\"" : language.Name);
                if (!string.IsNullOrEmpty(language.Plural)) sb.Append(" plural=").Append(language.Plural);
                if (!string.IsNullOrEmpty(language.Fallback)) sb.Append(" fallback=").Append(language.Fallback);
                sb.Append('\n');
            }

            var entries = new List<LocEntry>(table.Entries);
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var entry in entries)
            {
                sb.Append('\n');
                foreach (var comment in entry.Comments) sb.Append(comment).Append('\n');

                sb.Append(entry.Key);
                if (entry.RemoveKey)
                {
                    sb.Append(" ~\n");
                    continue;
                }

                sb.Append('\n');

                var values = new List<LocValue>(entry.Values);
                values.Sort((a, b) =>
                {
                    var la = LanguageOrder(table, a.Language);
                    var lb = LanguageOrder(table, b.Language);
                    if (la != lb) return la.CompareTo(lb);
                    if (la == int.MaxValue)
                    {
                        var byCode = string.CompareOrdinal(a.Language, b.Language);
                        if (byCode != 0) return byCode;
                    }

                    return LocSlots.FormOrder(a.Form).CompareTo(LocSlots.FormOrder(b.Form));
                });

                foreach (var value in values) WriteValue(sb, value);
            }

            if (table.TrailingComments.Count > 0)
            {
                sb.Append('\n');
                foreach (var comment in table.TrailingComments) sb.Append(comment).Append('\n');
            }

            return sb.ToString();
        }

        private static int LanguageOrder(LocTable table, string code)
        {
            for (var i = 0; i < table.Languages.Count; i++)
                if (LocSlots.Same(table.Languages[i].Code, code)) return i;
            return int.MaxValue;
        }

        private static void WriteValue(StringBuilder sb, LocValue value)
        {
            sb.Append("    ").Append(value.Slot).Append(':');

            if (value.IsRemove)
            {
                sb.Append(" ~\n");
                return;
            }

            var text = value.Text;
            var lines = text.Split('\n');

            // Многострочный текст — продолжениями, если каждая строка переживёт обрезку краёв.
            var multiline = lines.Length > 1;
            if (multiline)
            {
                foreach (var line in lines)
                {
                    if (line.Length == 0 || line.Trim().Length != line.Length)
                    {
                        multiline = false;
                        break;
                    }
                }
            }

            if (multiline)
            {
                sb.Append(' ').Append(EscapeLine(lines[0], true)).Append('\n');
                for (var i = 1; i < lines.Length; i++)
                    sb.Append("        ").Append(EscapeLine(lines[i], true)).Append('\n');
                return;
            }

            sb.Append(' ').Append(EscapeLine(text, false)).Append('\n');
        }

        /// <summary>Экранирует одну строку значения так, чтобы чтение вернуло её же.</summary>
        private static string EscapeLine(string text, bool continuationSafe)
        {
            var sb = new StringBuilder(text.Length + 8);

            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    default: sb.Append(ch); break;
                }
            }

            var result = sb.ToString();

            // Значение, которое иначе прочиталось бы как служебное.
            if (result == "~") return "\\~";
            if (result.Length > 0 && result[0] == '#') result = "\\" + result;

            if (result.Length > 0 && result[0] == '"') result = "\\" + result;

            var needsQuotes = !continuationSafe && result.Length > 0 &&
                              (result[0] == ' ' || result[result.Length - 1] == ' ' || result[0] == '\t' ||
                               (result[result.Length - 1] == '"' && result[0] == '"'));

            return needsQuotes ? "\"" + result + "\"" : result;
        }
    }
}
