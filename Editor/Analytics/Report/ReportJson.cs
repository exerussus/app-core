using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Exerussus.AppCore.Analytics.Reports
{
    public enum JsonKind : byte
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
    }

    /// <summary>Значение плоского JSON: пакеты аналитики пишут только числа, строки, логические и null.</summary>
    public readonly struct JsonValue
    {
        public readonly JsonKind Kind;
        public readonly double Number;
        public readonly string Text;
        public readonly bool Flag;

        private JsonValue(JsonKind kind, double number, string text, bool flag)
        {
            Kind = kind;
            Number = number;
            Text = text;
            Flag = flag;
        }

        public static readonly JsonValue Null = default;
        public static JsonValue Of(double v) => new JsonValue(JsonKind.Number, v, null, false);
        public static JsonValue Of(string v) => v == null ? Null : new JsonValue(JsonKind.String, 0, v, false);
        public static JsonValue Of(bool v) => new JsonValue(JsonKind.Bool, 0, null, v);

        public bool IsNumber => Kind == JsonKind.Number;
        public bool IsNull => Kind == JsonKind.Null;

        /// <summary>Число или NaN.</summary>
        public double AsNumber() => Kind == JsonKind.Number ? Number : double.NaN;

        /// <summary>Строковое представление для таблиц: строка как есть, число инвариантно.</summary>
        public string AsText()
        {
            switch (Kind)
            {
                case JsonKind.String: return Text;
                case JsonKind.Number: return ReportFormat.Number(Number);
                case JsonKind.Bool: return Flag ? "true" : "false";
                default: return null;
            }
        }
    }

    /// <summary>
    /// Разбор одной строки JSONL пакета: плоский объект без вложенности. Битая строка — <c>false</c>
    /// (пакет мог оборваться при падении — читаем, сколько читается).
    /// </summary>
    public static class JsonLine
    {
        public static bool TryParse(string line, List<KeyValuePair<string, JsonValue>> into)
        {
            into.Clear();
            if (string.IsNullOrEmpty(line)) return false;

            var i = 0;
            SkipWs(line, ref i);
            if (i >= line.Length || line[i] != '{') return false;
            i++;

            SkipWs(line, ref i);
            if (i < line.Length && line[i] == '}') return true;

            while (i < line.Length)
            {
                SkipWs(line, ref i);
                if (!ReadString(line, ref i, out var name)) return false;
                SkipWs(line, ref i);
                if (i >= line.Length || line[i] != ':') return false;
                i++;
                SkipWs(line, ref i);
                if (!ReadValue(line, ref i, out var value)) return false;
                into.Add(new KeyValuePair<string, JsonValue>(name, value));
                SkipWs(line, ref i);
                if (i >= line.Length) return false;
                if (line[i] == ',')
                {
                    i++;
                    continue;
                }

                return line[i] == '}';
            }

            return false;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static bool ReadValue(string s, ref int i, out JsonValue value)
        {
            value = JsonValue.Null;
            if (i >= s.Length) return false;
            var c = s[i];

            if (c == '"')
            {
                if (!ReadString(s, ref i, out var text)) return false;
                value = JsonValue.Of(text);
                return true;
            }

            if (Match(s, ref i, "null")) return true;
            if (Match(s, ref i, "true"))
            {
                value = JsonValue.Of(true);
                return true;
            }

            if (Match(s, ref i, "false"))
            {
                value = JsonValue.Of(false);
                return true;
            }

            var start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (i == start) return false;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
            value = JsonValue.Of(number);
            return true;
        }

        private static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static bool ReadString(string s, ref int i, out string text)
        {
            text = null;
            if (i >= s.Length || s[i] != '"') return false;
            i++;

            StringBuilder sb = null;
            var start = i;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"')
                {
                    if (sb == null) text = s.Substring(start, i - start);
                    else
                    {
                        sb.Append(s, start, i - start);
                        text = sb.ToString();
                    }

                    i++;
                    return true;
                }

                if (c != '\\')
                {
                    i++;
                    continue;
                }

                sb ??= new StringBuilder();
                sb.Append(s, start, i - start);
                i++;
                if (i >= s.Length) return false;
                var e = s[i];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 >= s.Length) return false;
                        if (!int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) return false;
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: return false;
                }

                i++;
                start = i;
            }

            return false;
        }
    }

    /// <summary>Писатель JSON для отчёта: инвариантные числа, экранирование для вставки в &lt;script&gt;.</summary>
    public sealed class JsonOut
    {
        private readonly StringBuilder _sb;
        private readonly Stack<bool> _first = new Stack<bool>();
        private bool _afterName;

        public JsonOut(StringBuilder sb) => _sb = sb;

        public StringBuilder Builder => _sb;

        private void Separator()
        {
            if (_afterName)
            {
                _afterName = false;
                return;
            }

            if (_first.Count == 0) return;
            if (_first.Peek()) _first.Pop();
            else
            {
                _sb.Append(',');
                return;
            }

            _first.Push(false);
        }

        public JsonOut BeginObject()
        {
            Separator();
            _sb.Append('{');
            _first.Push(true);
            return this;
        }

        public JsonOut EndObject()
        {
            _first.Pop();
            _sb.Append('}');
            return this;
        }

        public JsonOut BeginArray()
        {
            Separator();
            _sb.Append('[');
            _first.Push(true);
            return this;
        }

        public JsonOut EndArray()
        {
            _first.Pop();
            _sb.Append(']');
            return this;
        }

        public JsonOut Name(string name)
        {
            Separator();
            WriteString(_sb, name);
            _sb.Append(':');
            _afterName = true;
            return this;
        }

        public JsonOut Value(string v)
        {
            Separator();
            if (v == null) _sb.Append("null");
            else WriteString(_sb, v);
            return this;
        }

        public JsonOut Value(double v)
        {
            Separator();
            if (double.IsNaN(v) || double.IsInfinity(v)) _sb.Append("null");
            else _sb.Append(ReportFormat.Number(v));
            return this;
        }

        public JsonOut Value(long v)
        {
            Separator();
            _sb.Append(v.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonOut Value(bool v)
        {
            Separator();
            _sb.Append(v ? "true" : "false");
            return this;
        }

        public JsonOut Value(JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.String: return Value(v.Text);
                case JsonKind.Number: return Value(v.Number);
                case JsonKind.Bool: return Value(v.Flag);
                default:
                    Separator();
                    _sb.Append("null");
                    return this;
            }
        }

        public JsonOut Prop(string name, string v) => Name(name).Value(v);
        public JsonOut Prop(string name, double v) => Name(name).Value(v);
        public JsonOut Prop(string name, long v) => Name(name).Value(v);
        public JsonOut Prop(string name, bool v) => Name(name).Value(v);

        /// <summary>Строка JSON; «&lt;», «&gt;», «&amp;» и U+2028/2029 экранируются — безопасно внутри &lt;script&gt;.</summary>
        public static void WriteString(StringBuilder sb, string s)
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
                    case '<': sb.Append("\\u003c"); break;
                    case '>': sb.Append("\\u003e"); break;
                    case '&': sb.Append("\\u0026"); break;
                    case '\u2028': sb.Append("\\u2028"); break;
                    case '\u2029': sb.Append("\\u2029"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    public static class ReportFormat
    {
        /// <summary>Число без экспоненты и лишних нулей, точка — разделитель.</summary>
        public static string Number(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return string.Empty;
            if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
