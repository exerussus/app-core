using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Exerussus.AppCore.Localization
{
    internal enum LocSegmentKind : byte
    {
        Literal = 0,
        Positional = 1,
        Named = 2,
    }

    internal readonly struct LocSegment
    {
        public readonly LocSegmentKind Kind;

        /// <summary>Литерал — текст; именованный токен — имя.</summary>
        public readonly string Text;

        public readonly int Index;
        public readonly string Format;

        /// <summary>Токен как записан — остаётся в строке, если аргумента нет.</summary>
        public readonly string Raw;

        public LocSegment(LocSegmentKind kind, string text, int index, string format, string raw)
        {
            Kind = kind;
            Text = text;
            Index = index;
            Format = format;
            Raw = raw;
        }
    }

    /// <summary>
    /// Разобранный шаблон перевода: литералы и токены аргументов. Разбирается один раз и
    /// кэшируется по тексту шаблона.
    /// </summary>
    /// <remarks>
    /// <para>Токены:</para>
    /// <list type="bullet">
    /// <item><c>{0}</c>, <c>{0:fmt}</c> — позиционный аргумент;</item>
    /// <item><c>{имя}</c>, <c>{имя:fmt}</c> — именованный, имя — <c>[A-Za-z_][A-Za-z0-9_.]*</c>;</item>
    /// <item><c>{{</c>, <c>}}</c> — буквальные скобки;</item>
    /// <item>всё прочее в скобках — незнакомый токен, остаётся в строке как есть (его дорабатывают проходы и
    /// процессор проекта). Сюда же <c>{имя|…}</c> — зарезервировано под окончания.</item>
    /// </list>
    /// <para>
    /// Формат (часть после первого <c>:</c>) — только из белого списка: стандартный (одна буква и до двух
    /// цифр: <c>N2</c>, <c>P0</c>) или пользовательский из <c>0 # . , % ‰ ; E e + -</c> и цифр (<c>0.0</c>,
    /// <c>#,##0</c>). Иначе токен целиком незнакомый: <c>{max:damage}</c> принадлежит проекту.
    /// </para>
    /// </remarks>
    internal sealed class LocTemplate
    {
        private static readonly LocSegment[] NoSegments = new LocSegment[0];

        /// <summary>Счётчик разборов — для тестов кэша.</summary>
        internal static int Parses;

        public readonly string Source;
        public readonly LocSegment[] Segments;

        /// <summary>Есть токены аргументов.</summary>
        public readonly bool HasArguments;

        /// <summary>Текст без токенов аргументов, с раскрытыми <c>{{ }}</c>. Без них — <see cref="Source"/>.</summary>
        public readonly string Plain;

        /// <summary>Подстановка что-то меняет: есть токены аргументов или экранированные скобки.</summary>
        public bool NeedsRender => HasArguments || !ReferenceEquals(Plain, Source);

        private LocTemplate(string source, LocSegment[] segments, bool hasArguments)
        {
            Source = source;
            Segments = segments;
            HasArguments = hasArguments;

            // Только литералы: раскрытый текст считаем сразу (одна строка на шаблон, не на показ).
            if (hasArguments) Plain = source;
            else if (segments.Length == 1 && segments[0].Text != source) Plain = segments[0].Text;
            else Plain = source;
        }

        public static LocTemplate Parse(string pattern)
        {
            Parses++;
            if (string.IsNullOrEmpty(pattern) || (pattern.IndexOf('{') < 0 && pattern.IndexOf('}') < 0))
                return new LocTemplate(pattern ?? string.Empty, NoSegments, false);

            var segments = new List<LocSegment>();
            var literal = new StringBuilder(pattern.Length);
            var hasArguments = false;
            var length = pattern.Length;
            var i = 0;

            while (i < length)
            {
                var c = pattern[i];

                if (c == '{')
                {
                    if (i + 1 < length && pattern[i + 1] == '{')
                    {
                        literal.Append('{');
                        i += 2;
                        continue;
                    }

                    var close = pattern.IndexOf('}', i + 1);
                    if (close < 0)
                    {
                        literal.Append(pattern, i, length - i);
                        break;
                    }

                    // «{ текст {токен}» — первая скобка буквальная.
                    if (pattern.IndexOf('{', i + 1, close - i - 1) >= 0)
                    {
                        literal.Append('{');
                        i++;
                        continue;
                    }

                    var raw = pattern.Substring(i, close - i + 1);
                    if (TryToken(pattern, i + 1, close, out var kind, out var name, out var index, out var format))
                    {
                        if (literal.Length > 0)
                        {
                            segments.Add(new LocSegment(LocSegmentKind.Literal, literal.ToString(), 0, null, null));
                            literal.Clear();
                        }

                        segments.Add(new LocSegment(kind, name, index, format, raw));
                        hasArguments = true;
                    }
                    else
                    {
                        literal.Append(raw);
                    }

                    i = close + 1;
                    continue;
                }

                if (c == '}' && i + 1 < length && pattern[i + 1] == '}')
                {
                    literal.Append('}');
                    i += 2;
                    continue;
                }

                literal.Append(c);
                i++;
            }

            if (literal.Length > 0) segments.Add(new LocSegment(LocSegmentKind.Literal, literal.ToString(), 0, null, null));
            return new LocTemplate(pattern, segments.ToArray(), hasArguments);
        }

        private static bool TryToken(string s, int start, int end, out LocSegmentKind kind, out string name, out int index, out string format)
        {
            kind = LocSegmentKind.Literal;
            name = null;
            index = 0;
            format = null;

            if (end <= start) return false;
            if (s.IndexOf('|', start, end - start) >= 0) return false;   // окончания — зарезервировано

            var colon = s.IndexOf(':', start, end - start);
            var headEnd = colon < 0 ? end : colon;
            if (headEnd == start) return false;

            if (colon >= 0)
            {
                if (!IsFormat(s, colon + 1, end)) return false;
                format = s.Substring(colon + 1, end - colon - 1);
            }

            if (IsDigits(s, start, headEnd))
            {
                if (headEnd - start > 6) return false;
                index = int.Parse(s.Substring(start, headEnd - start), NumberStyles.None, CultureInfo.InvariantCulture);
                kind = LocSegmentKind.Positional;
                return true;
            }

            if (!IsName(s, start, headEnd)) return false;

            name = s.Substring(start, headEnd - start);
            kind = LocSegmentKind.Named;
            return true;
        }

        private static bool IsDigits(string s, int start, int end)
        {
            for (var i = start; i < end; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        private static bool IsName(string s, int start, int end)
        {
            var first = s[start];
            if (!IsAsciiLetter(first) && first != '_') return false;

            for (var i = start + 1; i < end; i++)
            {
                var c = s[i];
                if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9') && c != '_' && c != '.') return false;
            }

            return true;
        }

        /// <summary>Белый список форматов (см. описание класса).</summary>
        internal static bool IsFormat(string s, int start, int end)
        {
            var length = end - start;
            if (length <= 0) return false;

            // Стандартный: буква и до двух цифр.
            if (IsAsciiLetter(s[start]) && length <= 3)
            {
                var standard = true;
                for (var i = start + 1; i < end; i++)
                    if (s[i] < '0' || s[i] > '9') standard = false;
                if (standard) return true;
            }

            // Пользовательский: только знаки числового формата и цифры.
            for (var i = start; i < end; i++)
            {
                var c = s[i];
                if (c >= '0' && c <= '9') continue;
                switch (c)
                {
                    case '#': case '.': case ',': case '%': case '‰': case ';':
                    case 'E': case 'e': case '+': case '-':
                        continue;
                    default:
                        return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        // ---------------------------------------------------------------- подстановка

        private static readonly StringBuilder Shared = new(256);
        private static bool _sharedBusy;

        /// <summary>
        /// Подставляет аргументы. Нет аргумента для позиционного токена — он остаётся в строке и
        /// <paramref name="missingPositional"/> = true; для именованного — остаётся, первое такое имя —
        /// в <paramref name="unknownName"/>.
        /// </summary>
        public string Render<TArgs>(ref TArgs args, CultureInfo culture, out bool missingPositional, out string unknownName)
            where TArgs : struct, ILocArgs
        {
            missingPositional = false;
            unknownName = null;
            if (!HasArguments) return Plain;

            // Аргумент со своим ToString может снова позвать Loc — тогда берём свой буфер.
            var sb = _sharedBusy ? new StringBuilder(Source.Length + 16) : Shared;
            var own = ReferenceEquals(sb, Shared);
            if (own)
            {
                _sharedBusy = true;
                sb.Clear();
            }

            try
            {
                for (var i = 0; i < Segments.Length; i++)
                {
                    var segment = Segments[i];
                    switch (segment.Kind)
                    {
                        case LocSegmentKind.Literal:
                            sb.Append(segment.Text);
                            break;

                        case LocSegmentKind.Positional:
                            if (!args.Positional(segment.Index, sb, segment.Format, culture))
                            {
                                sb.Append(segment.Raw);
                                missingPositional = true;
                            }
                            break;

                        default:
                            if (!args.Named(segment.Text, sb, segment.Format, culture))
                            {
                                sb.Append(segment.Raw);
                                unknownName ??= segment.Text;
                            }
                            break;
                    }
                }

                return sb.ToString();
            }
            finally
            {
                if (own)
                {
                    sb.Clear();
                    _sharedBusy = false;
                }
            }
        }
    }

    // ==================================================================== аргументы

    /// <summary>Источник аргументов для подстановки. Структуры — чтобы звать без упаковки.</summary>
    internal interface ILocArgs
    {
        bool Positional(int index, StringBuilder sb, string format, CultureInfo culture);
        bool Named(string name, StringBuilder sb, string format, CultureInfo culture);
    }

    internal struct NoArgs : ILocArgs
    {
        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture) => false;
        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture) => false;
    }

    internal struct ObjectArg : ILocArgs
    {
        private readonly object _value;

        public ObjectArg(object value) => _value = value;

        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture)
        {
            if (index != 0) return false;
            LocWrite.Object(sb, _value, format, culture);
            return true;
        }

        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture) => false;
    }

    internal struct ObjectArgs : ILocArgs
    {
        private readonly object[] _values;

        public ObjectArgs(object[] values) => _values = values;

        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture)
        {
            if (_values == null || index >= _values.Length) return false;
            LocWrite.Object(sb, _values[index], format, culture);
            return true;
        }

        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture) => false;
    }

    internal struct NamedArgs : ILocArgs
    {
        private readonly (string name, object value)[] _values;

        public NamedArgs((string name, object value)[] values) => _values = values;

        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture)
        {
            if (_values == null) return false;
            for (var i = 0; i < _values.Length; i++)
            {
                if (!LocWrite.IsIndex(_values[i].name, index)) continue;
                LocWrite.Object(sb, _values[i].value, format, culture);
                return true;
            }

            return false;
        }

        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture)
        {
            if (_values == null) return false;
            for (var i = 0; i < _values.Length; i++)
            {
                if (!string.Equals(_values[i].name, name, StringComparison.Ordinal)) continue;
                LocWrite.Object(sb, _values[i].value, format, culture);
                return true;
            }

            return false;
        }
    }

    internal struct NamedArg<T0> : ILocArgs
    {
        private readonly (string name, T0 value) _a0;

        public NamedArg((string name, T0 value) a0) => _a0 = a0;

        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture) =>
            Named(_a0.name, index, sb, format, culture);

        private bool Named(string name, int index, StringBuilder sb, string format, CultureInfo culture) =>
            LocWrite.IsIndex(name, index) && Named(name, sb, format, culture);

        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture)
        {
            if (!string.Equals(_a0.name, name, StringComparison.Ordinal)) return false;
            LocWrite<T0>.Write(sb, _a0.value, format, culture);
            return true;
        }
    }

    internal struct NamedArg<T0, T1> : ILocArgs
    {
        private readonly (string name, T0 value) _a0;
        private readonly (string name, T1 value) _a1;

        public NamedArg((string name, T0 value) a0, (string name, T1 value) a1)
        {
            _a0 = a0;
            _a1 = a1;
        }

        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture)
        {
            if (LocWrite.IsIndex(_a0.name, index)) return Named(_a0.name, sb, format, culture);
            if (LocWrite.IsIndex(_a1.name, index)) return Named(_a1.name, sb, format, culture);
            return false;
        }

        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture)
        {
            if (string.Equals(_a0.name, name, StringComparison.Ordinal))
            {
                LocWrite<T0>.Write(sb, _a0.value, format, culture);
                return true;
            }

            if (string.Equals(_a1.name, name, StringComparison.Ordinal))
            {
                LocWrite<T1>.Write(sb, _a1.value, format, culture);
                return true;
            }

            return false;
        }
    }

    internal struct NamedArg<T0, T1, T2> : ILocArgs
    {
        private readonly (string name, T0 value) _a0;
        private readonly (string name, T1 value) _a1;
        private readonly (string name, T2 value) _a2;

        public NamedArg((string name, T0 value) a0, (string name, T1 value) a1, (string name, T2 value) a2)
        {
            _a0 = a0;
            _a1 = a1;
            _a2 = a2;
        }

        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture)
        {
            if (LocWrite.IsIndex(_a0.name, index)) return Named(_a0.name, sb, format, culture);
            if (LocWrite.IsIndex(_a1.name, index)) return Named(_a1.name, sb, format, culture);
            if (LocWrite.IsIndex(_a2.name, index)) return Named(_a2.name, sb, format, culture);
            return false;
        }

        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture)
        {
            if (string.Equals(_a0.name, name, StringComparison.Ordinal))
            {
                LocWrite<T0>.Write(sb, _a0.value, format, culture);
                return true;
            }

            if (string.Equals(_a1.name, name, StringComparison.Ordinal))
            {
                LocWrite<T1>.Write(sb, _a1.value, format, culture);
                return true;
            }

            if (string.Equals(_a2.name, name, StringComparison.Ordinal))
            {
                LocWrite<T2>.Write(sb, _a2.value, format, culture);
                return true;
            }

            return false;
        }
    }

    /// <summary>Позиционные и именованные вместе — для биндера.</summary>
    internal struct MixedArgs : ILocArgs
    {
        private ObjectArgs _positional;
        private NamedArgs _named;

        public MixedArgs(object[] positional, (string name, object value)[] named)
        {
            _positional = new ObjectArgs(positional);
            _named = new NamedArgs(named);
        }

        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture) =>
            _positional.Positional(index, sb, format, culture);

        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture) =>
            _named.Named(name, sb, format, culture);
    }

    /// <summary>Окончания: число — <c>{0}</c>, позиционные аргументы — с <c>{1}</c>, именованные — как есть.</summary>
    internal struct PluralArgs<TInner> : ILocArgs where TInner : struct, ILocArgs
    {
        private readonly double _count;
        private TInner _inner;

        public PluralArgs(double count, TInner inner)
        {
            _count = count;
            _inner = inner;
        }

        public bool Positional(int index, StringBuilder sb, string format, CultureInfo culture)
        {
            if (index == 0)
            {
                LocWrite<double>.Write(sb, _count, format, culture);
                return true;
            }

            return _inner.Positional(index - 1, sb, format, culture);
        }

        public bool Named(string name, StringBuilder sb, string format, CultureInfo culture) =>
            _inner.Named(name, sb, format, culture);
    }

    // ==================================================================== запись значений

    internal static class LocWrite
    {
        /// <summary>Имя аргумента — номер позиционного токена: <c>("0", value)</c> подставляется в <c>{0}</c>.</summary>
        public static bool IsIndex(string name, int index)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 6) return false;

            var value = 0;
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }

            return value == index;
        }

        public static void Object(StringBuilder sb, object value, string format, CultureInfo culture)
        {
            switch (value)
            {
                case null:
                    return;
                case string s:
                    sb.Append(s);
                    return;
                case IFormattable formattable:
                    try { sb.Append(formattable.ToString(format, culture)); }
                    catch (FormatException) { sb.Append(value); }
                    return;
                default:
                    sb.Append(value);
                    return;
            }
        }
    }

    /// <summary>
    /// Запись значения без упаковки: для чисел — <c>TryFormat</c> в стековый буфер. Делегат выбирается
    /// раз на тип; прочие типы — через <see cref="LocWrite.Object"/> (с упаковкой).
    /// </summary>
    internal static class LocWrite<T>
    {
        public static readonly Action<StringBuilder, T, string, CultureInfo> Write = Create();

        private static Action<StringBuilder, T, string, CultureInfo> Create()
        {
            var type = typeof(T);
            if (type == typeof(int)) return (Action<StringBuilder, T, string, CultureInfo>)(object)(Action<StringBuilder, int, string, CultureInfo>)WriteInt;
            if (type == typeof(long)) return (Action<StringBuilder, T, string, CultureInfo>)(object)(Action<StringBuilder, long, string, CultureInfo>)WriteLong;
            if (type == typeof(float)) return (Action<StringBuilder, T, string, CultureInfo>)(object)(Action<StringBuilder, float, string, CultureInfo>)WriteFloat;
            if (type == typeof(double)) return (Action<StringBuilder, T, string, CultureInfo>)(object)(Action<StringBuilder, double, string, CultureInfo>)WriteDouble;
            if (type == typeof(decimal)) return (Action<StringBuilder, T, string, CultureInfo>)(object)(Action<StringBuilder, decimal, string, CultureInfo>)WriteDecimal;
            if (type == typeof(string)) return (Action<StringBuilder, T, string, CultureInfo>)(object)(Action<StringBuilder, string, string, CultureInfo>)WriteString;
            return (sb, value, format, culture) => LocWrite.Object(sb, value, format, culture);
        }

        private static void WriteString(StringBuilder sb, string value, string format, CultureInfo culture) => sb.Append(value);

        private static void WriteInt(StringBuilder sb, int value, string format, CultureInfo culture)
        {
            Span<char> buffer = stackalloc char[64];
            try
            {
                if (value.TryFormat(buffer, out var written, format, culture))
                {
                    sb.Append(buffer.Slice(0, written));
                    return;
                }
            }
            catch (FormatException) { }

            sb.Append(value.ToString(culture));
        }

        private static void WriteLong(StringBuilder sb, long value, string format, CultureInfo culture)
        {
            Span<char> buffer = stackalloc char[64];
            try
            {
                if (value.TryFormat(buffer, out var written, format, culture))
                {
                    sb.Append(buffer.Slice(0, written));
                    return;
                }
            }
            catch (FormatException) { }

            sb.Append(value.ToString(culture));
        }

        private static void WriteFloat(StringBuilder sb, float value, string format, CultureInfo culture)
        {
            Span<char> buffer = stackalloc char[64];
            try
            {
                if (value.TryFormat(buffer, out var written, format, culture))
                {
                    sb.Append(buffer.Slice(0, written));
                    return;
                }
            }
            catch (FormatException) { }

            sb.Append(value.ToString(culture));
        }

        private static void WriteDouble(StringBuilder sb, double value, string format, CultureInfo culture)
        {
            Span<char> buffer = stackalloc char[64];
            try
            {
                if (value.TryFormat(buffer, out var written, format, culture))
                {
                    sb.Append(buffer.Slice(0, written));
                    return;
                }
            }
            catch (FormatException) { }

            sb.Append(value.ToString(culture));
        }

        private static void WriteDecimal(StringBuilder sb, decimal value, string format, CultureInfo culture)
        {
            Span<char> buffer = stackalloc char[64];
            try
            {
                if (value.TryFormat(buffer, out var written, format, culture))
                {
                    sb.Append(buffer.Slice(0, written));
                    return;
                }
            }
            catch (FormatException) { }

            sb.Append(value.ToString(culture));
        }
    }
}
