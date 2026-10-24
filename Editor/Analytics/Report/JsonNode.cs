using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Exerussus.AppCore.Analytics.Reports
{
    public enum JsonNodeKind : byte
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
        Array = 4,
        Object = 5,
    }

    /// <summary>
    /// Дерево JSON для файла представлений и аргументов CLI: объекты (порядок ключей сохраняется), массивы, значения.
    /// Пакеты аналитики читает плоский <see cref="JsonLine"/> — это для конфигурации, не для потока событий.
    /// </summary>
    public sealed class JsonNode
    {
        public JsonNodeKind Kind;
        public double Number;
        public string Text;
        public bool Flag;
        public readonly List<JsonNode> Items = new List<JsonNode>();
        public readonly List<KeyValuePair<string, JsonNode>> Props = new List<KeyValuePair<string, JsonNode>>();

        public static JsonNode Obj() => new JsonNode { Kind = JsonNodeKind.Object };
        public static JsonNode Arr() => new JsonNode { Kind = JsonNodeKind.Array };
        public static JsonNode Of(string s) => s == null ? new JsonNode() : new JsonNode { Kind = JsonNodeKind.String, Text = s };
        public static JsonNode Of(double v) => new JsonNode { Kind = JsonNodeKind.Number, Number = v };
        public static JsonNode Of(bool v) => new JsonNode { Kind = JsonNodeKind.Bool, Flag = v };

        public bool IsObject => Kind == JsonNodeKind.Object;
        public bool IsArray => Kind == JsonNodeKind.Array;

        public JsonNode this[string key]
        {
            get
            {
                if (Kind != JsonNodeKind.Object) return null;
                foreach (var p in Props)
                    if (p.Key == key) return p.Value;
                return null;
            }
        }

        // ------------------------------------------------------------ чтение полей

        public string Str(string key, string fallback = null)
        {
            var n = this[key];
            if (n == null) return fallback;
            switch (n.Kind)
            {
                case JsonNodeKind.String: return n.Text;
                case JsonNodeKind.Number: return ReportFormat.Number(n.Number);
                case JsonNodeKind.Bool: return n.Flag ? "true" : "false";
                default: return fallback;
            }
        }

        public double Num(string key, double fallback = double.NaN)
        {
            var n = this[key];
            if (n == null) return fallback;
            if (n.Kind == JsonNodeKind.Number) return n.Number;
            if (n.Kind == JsonNodeKind.String && double.TryParse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            return fallback;
        }

        public int Int(string key, int fallback = 0)
        {
            var v = Num(key);
            return double.IsNaN(v) ? fallback : (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(v)));
        }

        public bool Bool(string key, bool fallback = false)
        {
            var n = this[key];
            if (n == null) return fallback;
            if (n.Kind == JsonNodeKind.Bool) return n.Flag;
            if (n.Kind == JsonNodeKind.String) return n.Text == "true" || n.Text == "1";
            if (n.Kind == JsonNodeKind.Number) return n.Number != 0;
            return fallback;
        }

        /// <summary>Массив строк; одиночная строка — список из неё (удобно писать руками).</summary>
        public List<string> Strings(string key)
        {
            var list = new List<string>();
            var n = this[key];
            if (n == null) return list;
            if (n.Kind == JsonNodeKind.String)
            {
                if (!string.IsNullOrWhiteSpace(n.Text)) list.Add(n.Text.Trim());
                return list;
            }

            foreach (var item in n.Items)
                if (item.Kind == JsonNodeKind.String && !string.IsNullOrWhiteSpace(item.Text)) list.Add(item.Text.Trim());
                else if (item.Kind == JsonNodeKind.Number) list.Add(ReportFormat.Number(item.Number));
            return list;
        }

        // ------------------------------------------------------------ сборка

        public JsonNode Set(string key, JsonNode value)
        {
            for (var i = 0; i < Props.Count; i++)
                if (Props[i].Key == key)
                {
                    Props[i] = new KeyValuePair<string, JsonNode>(key, value);
                    return this;
                }

            Props.Add(new KeyValuePair<string, JsonNode>(key, value));
            return this;
        }

        public JsonNode Set(string key, string value) => Set(key, Of(value));
        public JsonNode Set(string key, double value) => Set(key, Of(value));
        public JsonNode Set(string key, bool value) => Set(key, Of(value));

        /// <summary>Записать, только если строка не пустая (файл остаётся коротким).</summary>
        public JsonNode SetIf(string key, string value) => string.IsNullOrEmpty(value) ? this : Set(key, value);

        public JsonNode SetIf(string key, List<string> values)
        {
            if (values == null || values.Count == 0) return this;
            var arr = Arr();
            foreach (var v in values) arr.Items.Add(Of(v));
            return Set(key, arr);
        }

        public JsonNode Add(JsonNode item)
        {
            Items.Add(item);
            return this;
        }

        // ------------------------------------------------------------ разбор

        /// <summary>Разобрать JSON. Ошибка — <see cref="FormatException"/> с позицией.</summary>
        public static JsonNode Parse(string text)
        {
            if (text == null) throw new FormatException("пустой JSON");
            var i = 0;
            var node = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i < text.Length) throw Error(text, i, "лишние символы после JSON");
            return node;
        }

        private const int MaxDepth = 64;

        private static JsonNode ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw Error(s, i, "слишком глубокая вложенность");
            SkipWs(s, ref i);
            if (i >= s.Length) throw Error(s, i, "неожиданный конец");

            var c = s[i];
            switch (c)
            {
                case '{':
                {
                    i++;
                    var obj = Obj();
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == '}')
                    {
                        i++;
                        return obj;
                    }

                    while (true)
                    {
                        SkipWs(s, ref i);
                        if (i >= s.Length || s[i] != '"') throw Error(s, i, "ожидалось имя свойства");
                        var key = ParseString(s, ref i);
                        SkipWs(s, ref i);
                        if (i >= s.Length || s[i] != ':') throw Error(s, i, "ожидалось «:»");
                        i++;
                        obj.Set(key, ParseValue(s, ref i, depth + 1));
                        SkipWs(s, ref i);
                        if (i < s.Length && s[i] == ',')
                        {
                            i++;
                            continue;
                        }

                        if (i < s.Length && s[i] == '}')
                        {
                            i++;
                            return obj;
                        }

                        throw Error(s, i, "ожидалось «,» или «}»");
                    }
                }
                case '[':
                {
                    i++;
                    var arr = Arr();
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ']')
                    {
                        i++;
                        return arr;
                    }

                    while (true)
                    {
                        arr.Items.Add(ParseValue(s, ref i, depth + 1));
                        SkipWs(s, ref i);
                        if (i < s.Length && s[i] == ',')
                        {
                            i++;
                            continue;
                        }

                        if (i < s.Length && s[i] == ']')
                        {
                            i++;
                            return arr;
                        }

                        throw Error(s, i, "ожидалось «,» или «]»");
                    }
                }
                case '"':
                    return Of(ParseString(s, ref i));
                case 't':
                    Expect(s, ref i, "true");
                    return Of(true);
                case 'f':
                    Expect(s, ref i, "false");
                    return Of(false);
                case 'n':
                    Expect(s, ref i, "null");
                    return new JsonNode();
                default:
                {
                    var start = i;
                    if (s[i] == '-') i++;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
                    if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        throw Error(s, start, "не значение JSON");
                    return Of(v);
                }
            }
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Error(s, i, "не значение JSON");
            i += word.Length;
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // "
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= s.Length) break;
                var e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw Error(s, i, "битая \\u-последовательность");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw Error(s, i, "неизвестная escape-последовательность");
                }
            }

            throw Error(s, i, "незакрытая строка");
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') i++;
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    // комментарии // — файл пишут руками
                    while (i < s.Length && s[i] != '\n') i++;
                }
                else break;
            }
        }

        private static FormatException Error(string s, int at, string what)
        {
            var line = 1;
            var col = 1;
            for (var k = 0; k < at && k < s.Length; k++)
            {
                if (s[k] == '\n')
                {
                    line++;
                    col = 1;
                }
                else col++;
            }

            return new FormatException($"JSON, строка {line}, столбец {col}: {what}");
        }

        // ------------------------------------------------------------ запись

        /// <summary>JSON с отступами (файл в git: читаемые диффы). Короткие массивы значений — в строку.</summary>
        public string ToPretty()
        {
            var sb = new StringBuilder();
            Write(sb, this, 0, true);
            sb.Append('\n');
            return sb.ToString();
        }

        public string ToCompact()
        {
            var sb = new StringBuilder();
            Write(sb, this, 0, false);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, JsonNode n, int indent, bool pretty)
        {
            if (n == null)
            {
                sb.Append("null");
                return;
            }

            switch (n.Kind)
            {
                case JsonNodeKind.Null: sb.Append("null"); break;
                case JsonNodeKind.Bool: sb.Append(n.Flag ? "true" : "false"); break;
                case JsonNodeKind.Number: sb.Append(double.IsNaN(n.Number) || double.IsInfinity(n.Number) ? "null" : ReportFormat.Number(n.Number)); break;
                case JsonNodeKind.String: Quote(sb, n.Text ?? string.Empty); break;
                case JsonNodeKind.Array:
                {
                    if (n.Items.Count == 0)
                    {
                        sb.Append("[]");
                        break;
                    }

                    var inline = !pretty || IsFlat(n);
                    sb.Append('[');
                    for (var k = 0; k < n.Items.Count; k++)
                    {
                        if (k > 0) sb.Append(inline && pretty ? ", " : ",");
                        if (!inline) NewLine(sb, indent + 1);
                        Write(sb, n.Items[k], indent + 1, pretty);
                    }

                    if (!inline) NewLine(sb, indent);
                    sb.Append(']');
                    break;
                }
                case JsonNodeKind.Object:
                {
                    if (n.Props.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }

                    sb.Append('{');
                    for (var k = 0; k < n.Props.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        if (pretty) NewLine(sb, indent + 1);
                        Quote(sb, n.Props[k].Key);
                        sb.Append(pretty ? ": " : ":");
                        Write(sb, n.Props[k].Value, indent + 1, pretty);
                    }

                    if (pretty) NewLine(sb, indent);
                    sb.Append('}');
                    break;
                }
            }
        }

        /// <summary>Строка JSON без экранирования «&lt;», «&gt;» (файл читают люди: «duration > 5»).</summary>
        private static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static bool IsFlat(JsonNode arr)
        {
            var length = 0;
            foreach (var item in arr.Items)
            {
                if (item == null) continue;
                if (item.Kind == JsonNodeKind.Array || item.Kind == JsonNodeKind.Object) return false;
                length += item.Kind == JsonNodeKind.String ? (item.Text?.Length ?? 0) + 4 : 8;
            }

            return length <= 100;
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append('\n');
            sb.Append(' ', indent * 2);
        }
    }
}
