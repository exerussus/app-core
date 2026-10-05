using System;
using System.Text;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Токен строки команды — диапазон в исходной строке, без копии.
    /// <see cref="Start"/>/<see cref="Length"/> — содержимое (без кавычек),
    /// <see cref="OuterStart"/>/<see cref="OuterEnd"/> — вместе с кавычками (для замены на Tab).
    /// </summary>
    public readonly struct TokenRange
    {
        public readonly int Start;
        public readonly int Length;
        public readonly int OuterStart;
        public readonly int OuterEnd;
        public readonly bool Quoted;

        /// <summary>Внутри кавычек были экранированные символы — значение нужно раскрывать, а не просто вырезать.</summary>
        public readonly bool Escaped;

        public TokenRange(int start, int length, int outerStart, int outerEnd, bool quoted, bool escaped)
        {
            Start = start;
            Length = length;
            OuterStart = outerStart;
            OuterEnd = outerEnd;
            Quoted = quoted;
            Escaped = escaped;
        }

        public int End => Start + Length;

        public ReadOnlySpan<char> Span(string text) => text.AsSpan(Start, Length);
    }

    /// <summary>
    /// Разбор строки команды без аллокаций: токены — диапазоны в исходной строке.
    /// Разделители: пробелы; <c>;</c> вне кавычек — конец команды (несколько команд в строке);
    /// <c>"..."</c> — токен с пробелами, внутри <c>\"</c> и <c>\\</c>.
    /// Чистые функции, состояния нет.
    /// </summary>
    public static class CommandLine
    {
        /// <summary>Больше токенов в одной команде не бывает — хватает с запасом, и буфер можно держать на стеке.</summary>
        public const int MaxTokens = 32;

        /// <summary>
        /// Найти следующую команду строки, начиная с <paramref name="from"/>.
        /// Возвращает false, когда команд больше нет. <paramref name="next"/> — откуда искать следующую.
        /// </summary>
        public static bool NextStatement(string text, int from, out int start, out int end, out int next)
        {
            start = end = next = text?.Length ?? 0;
            if (text == null || from >= text.Length) return false;

            var inQuotes = false;
            int i = from;

            for (; i < text.Length; i++)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '\\' && i + 1 < text.Length) { i++; continue; }
                    if (c == '"') inQuotes = false;
                    continue;
                }

                if (c == '"') { inQuotes = true; continue; }
                if (c == ';') break;
            }

            start = from;
            end = i;
            next = i < text.Length ? i + 1 : i;
            return true;
        }

        /// <summary>Разбить отрезок [start, end) на токены. Возвращает число токенов (не больше размера буфера).</summary>
        public static int Tokenize(string text, int start, int end, Span<TokenRange> tokens)
        {
            var count = 0;
            int i = start;

            while (i < end && count < tokens.Length)
            {
                while (i < end && char.IsWhiteSpace(text[i])) i++;
                if (i >= end) break;

                if (text[i] == '"')
                {
                    int outer = i;
                    i++;
                    int contentStart = i;
                    var escaped = false;

                    while (i < end && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < end) { escaped = true; i++; }
                        i++;
                    }

                    int contentEnd = i;
                    if (i < end) i++; // закрывающая кавычка

                    tokens[count++] = new TokenRange(contentStart, contentEnd - contentStart, outer, i, true, escaped);
                }
                else
                {
                    int s = i;
                    while (i < end && !char.IsWhiteSpace(text[i])) i++;
                    tokens[count++] = new TokenRange(s, i - s, s, i, false, false);
                }
            }

            return count;
        }

        /// <summary>Строка токена: подстрока, а для кавычек с экранированием — раскрытая.</summary>
        public static string Value(string text, in TokenRange token)
        {
            if (token.Length == 0) return string.Empty;
            if (!token.Escaped) return text.Substring(token.Start, token.Length);

            var sb = new StringBuilder(token.Length);
            for (int i = token.Start; i < token.End; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < token.End) { i++; c = text[i]; }
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// В каком токене стоит каретка. Каретка сразу за токеном считается в нём (дописываем слово).
        /// Если каретка между токенами — возвращает индекс, который получит новый токен,
        /// и <paramref name="inside"/> = false.
        /// </summary>
        public static int TokenAt(ReadOnlySpan<TokenRange> tokens, int caret, out bool inside)
        {
            for (var i = 0; i < tokens.Length; i++)
            {
                if (caret < tokens[i].OuterStart)
                {
                    inside = false;
                    return i;
                }

                if (caret <= tokens[i].OuterEnd)
                {
                    inside = true;
                    return i;
                }
            }

            inside = false;
            return tokens.Length;
        }

        /// <summary>Нужно ли заключить значение в кавычки при подстановке.</summary>
        public static bool NeedsQuotes(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;

            for (var i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsWhiteSpace(c) || c == '"' || c == ';') return true;
            }

            return false;
        }

        /// <summary>Значение в виде токена: как есть или в кавычках с экранированием.</summary>
        public static string Quote(string value)
        {
            if (!NeedsQuotes(value)) return value;

            var sb = new StringBuilder(value.Length + 2).Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }

            return sb.Append('"').ToString();
        }
    }
}
