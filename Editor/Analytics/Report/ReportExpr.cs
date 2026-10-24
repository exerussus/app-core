using System;
using System.Collections.Generic;
using System.Globalization;

namespace Exerussus.AppCore.Analytics.Reports
{
    /// <summary>
    /// Выражения представлений: фильтр <c>where</c> и вычисляемые поля. Значения — числа, строки, логические, null.
    /// </summary>
    /// <remarks>
    /// <para>Имена — поля события (<c>time</c>), измерения (<c>@tag</c>, <c>@prop.ab</c>), вычисляемые поля выше по списку.</para>
    /// <para>Операторы: <c>+ - * / %</c>, <c>= == != &lt; &gt; &lt;= &gt;=</c>, <c>~</c> (строка содержит),
    /// <c>&amp;&amp; || !</c>, <c>усл ? а : б</c>, скобки. Строки — в кавычках ' или ".</para>
    /// <para>Функции: <c>abs round min max len lower has if</c>.</para>
    /// <para>null в арифметике — null; деление на ноль — null; сравнение с null — ложь (кроме <c>= null</c>).</para>
    /// </remarks>
    public sealed class ReportExpr
    {
        public delegate JsonValue Resolver(string name);

        private delegate JsonValue Node(Resolver r);

        private readonly Node _root;
        public readonly string Source;
        /// <summary>Имена, на которые ссылается выражение (для проверки каталога).</summary>
        public readonly List<string> Names = new List<string>();

        private ReportExpr(string source)
        {
            Source = source;
            var p = new Parser(source, Names);
            _root = p.ParseAll();
        }

        /// <summary>Скомпилировать. Ошибка — <see cref="FormatException"/> с позицией.</summary>
        public static ReportExpr Compile(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new FormatException("пустое выражение");
            return new ReportExpr(source);
        }

        public static bool TryCompile(string source, out ReportExpr expr, out string error)
        {
            try
            {
                expr = Compile(source);
                error = null;
                return true;
            }
            catch (FormatException e)
            {
                expr = null;
                error = e.Message;
                return false;
            }
        }

        public JsonValue Eval(Resolver resolver) => _root(resolver);

        public bool Test(Resolver resolver) => Truthy(_root(resolver));

        public static bool Truthy(JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.Bool: return v.Flag;
                case JsonKind.Number: return v.Number != 0 && !double.IsNaN(v.Number);
                case JsonKind.String: return !string.IsNullOrEmpty(v.Text);
                default: return false;
            }
        }

        // ============================================================ разбор

        private enum Tok { End, Num, Str, Name, Op, LParen, RParen, Comma, Question, Colon }

        private sealed class Parser
        {
            private readonly string _s;
            private readonly List<string> _names;
            private int _i;
            private Tok _tok;
            private string _text;
            private double _num;
            private int _tokStart;
            private int _depth;
            private const int MaxDepth = 200;

            private void Enter()
            {
                if (++_depth > MaxDepth) throw Error("слишком глубокая вложенность");
            }

            public Parser(string s, List<string> names)
            {
                _s = s;
                _names = names;
                Next();
            }

            public Node ParseAll()
            {
                var n = Ternary();
                if (_tok != Tok.End) throw Error("лишнее: «" + _text + "»");
                return n;
            }

            private FormatException Error(string what) => new FormatException($"выражение «{_s}», позиция {_tokStart + 1}: {what}");

            private void Next()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
                _tokStart = _i;
                if (_i >= _s.Length)
                {
                    _tok = Tok.End;
                    _text = "конец";
                    return;
                }

                var c = _s[_i];
                if (char.IsDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
                {
                    var start = _i;
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                    {
                        _i++;
                        if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                        while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                    }

                    _text = _s.Substring(start, _i - start);
                    if (!double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out _num)) throw Error("не число «" + _text + "»");
                    _tok = Tok.Num;
                    return;
                }

                if (c == '"' || c == '\'')
                {
                    var quote = c;
                    _i++;
                    var sb = new System.Text.StringBuilder();
                    while (_i < _s.Length && _s[_i] != quote)
                    {
                        if (_s[_i] == '\\' && _i + 1 < _s.Length) _i++;
                        sb.Append(_s[_i++]);
                    }

                    if (_i >= _s.Length) throw Error("незакрытая строка");
                    _i++;
                    _text = sb.ToString();
                    _tok = Tok.Str;
                    return;
                }

                if (char.IsLetter(c) || c == '_' || c == '@')
                {
                    var start = _i++;
                    while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
                    _text = _s.Substring(start, _i - start);
                    _tok = Tok.Name;
                    return;
                }

                _i++;
                switch (c)
                {
                    case '(': _tok = Tok.LParen; _text = "("; return;
                    case ')': _tok = Tok.RParen; _text = ")"; return;
                    case ',': _tok = Tok.Comma; _text = ","; return;
                    case '?': _tok = Tok.Question; _text = "?"; return;
                    case ':': _tok = Tok.Colon; _text = ":"; return;
                }

                // операторы из двух символов
                if (_i < _s.Length)
                {
                    var two = _s.Substring(_i - 1, 2);
                    if (two == "==" || two == "!=" || two == "<=" || two == ">=" || two == "&&" || two == "||")
                    {
                        _i++;
                        _tok = Tok.Op;
                        _text = two;
                        return;
                    }
                }

                if ("+-*/%<>=!~".IndexOf(c) >= 0)
                {
                    _tok = Tok.Op;
                    _text = c.ToString();
                    return;
                }

                throw Error("непонятный символ «" + c + "»");
            }

            private bool IsOp(string op) => _tok == Tok.Op && _text == op;

            private Node Ternary()
            {
                Enter();
                try
                {
                    return TernaryCore();
                }
                finally
                {
                    _depth--;
                }
            }

            private Node TernaryCore()
            {
                var cond = Or();
                if (_tok != Tok.Question) return cond;
                Next();
                var a = Ternary();
                if (_tok != Tok.Colon) throw Error("ожидалось «:»");
                Next();
                var b = Ternary();
                return r => Truthy(cond(r)) ? a(r) : b(r);
            }

            private Node Or()
            {
                var left = And();
                while (IsOp("||"))
                {
                    Next();
                    var l = left;
                    var right = And();
                    left = r => JsonValue.Of(Truthy(l(r)) || Truthy(right(r)));
                }

                return left;
            }

            private Node And()
            {
                var left = Compare();
                while (IsOp("&&"))
                {
                    Next();
                    var l = left;
                    var right = Compare();
                    left = r => JsonValue.Of(Truthy(l(r)) && Truthy(right(r)));
                }

                return left;
            }

            private Node Compare()
            {
                var left = Add();
                if (_tok != Tok.Op) return left;
                var op = _text;
                if (op != "=" && op != "==" && op != "!=" && op != "<" && op != ">" && op != "<=" && op != ">=" && op != "~") return left;
                Next();
                var right = Add();
                switch (op)
                {
                    case "=":
                    case "==": return r => JsonValue.Of(Equal(left(r), right(r)));
                    case "!=": return r => JsonValue.Of(!Equal(left(r), right(r)));
                    case "~": return r => JsonValue.Of(Contains(left(r), right(r)));
                    default:
                        return r =>
                        {
                            var c = Order(left(r), right(r));
                            if (c == null) return JsonValue.Of(false);
                            switch (op)
                            {
                                case "<": return JsonValue.Of(c < 0);
                                case ">": return JsonValue.Of(c > 0);
                                case "<=": return JsonValue.Of(c <= 0);
                                default: return JsonValue.Of(c >= 0);
                            }
                        };
                }
            }

            private Node Add()
            {
                var left = Mul();
                while (IsOp("+") || IsOp("-"))
                {
                    var op = _text;
                    Next();
                    var l = left;
                    var right = Mul();
                    if (op == "+")
                        left = r =>
                        {
                            var a = l(r);
                            var b = right(r);
                            if (a.Kind == JsonKind.String || b.Kind == JsonKind.String)
                                return a.IsNull || b.IsNull ? JsonValue.Null : JsonValue.Of(a.AsText() + b.AsText());
                            return Arith(a, b, (x, y) => x + y);
                        };
                    else left = r => Arith(l(r), right(r), (x, y) => x - y);
                }

                return left;
            }

            private Node Mul()
            {
                var left = Unary();
                while (IsOp("*") || IsOp("/") || IsOp("%"))
                {
                    var op = _text;
                    Next();
                    var l = left;
                    var right = Unary();
                    switch (op)
                    {
                        case "*": left = r => Arith(l(r), right(r), (x, y) => x * y); break;
                        case "/": left = r => Arith(l(r), right(r), (x, y) => y == 0 ? double.NaN : x / y); break;
                        default: left = r => Arith(l(r), right(r), (x, y) => y == 0 ? double.NaN : x % y); break;
                    }
                }

                return left;
            }

            private Node Unary()
            {
                Enter();
                try
                {
                    return UnaryCore();
                }
                finally
                {
                    _depth--;
                }
            }

            private Node UnaryCore()
            {
                if (IsOp("!"))
                {
                    Next();
                    var a = Unary();
                    return r => JsonValue.Of(!Truthy(a(r)));
                }

                if (IsOp("-"))
                {
                    Next();
                    var a = Unary();
                    return r =>
                    {
                        var v = Num(a(r));
                        return double.IsNaN(v) ? JsonValue.Null : JsonValue.Of(-v);
                    };
                }

                return Primary();
            }

            private Node Primary()
            {
                switch (_tok)
                {
                    case Tok.Num:
                    {
                        var v = JsonValue.Of(_num);
                        Next();
                        return r => v;
                    }
                    case Tok.Str:
                    {
                        var v = JsonValue.Of(_text);
                        Next();
                        return r => v;
                    }
                    case Tok.LParen:
                    {
                        Next();
                        var n = Ternary();
                        if (_tok != Tok.RParen) throw Error("ожидалось «)»");
                        Next();
                        return n;
                    }
                    case Tok.Name:
                    {
                        var name = _text;
                        Next();
                        switch (name)
                        {
                            case "true": return r => JsonValue.Of(true);
                            case "false": return r => JsonValue.Of(false);
                            case "null": return r => JsonValue.Null;
                        }

                        if (_tok == Tok.LParen) return Call(name);
                        if (!_names.Contains(name)) _names.Add(name);
                        return r => r(name);
                    }
                    default:
                        throw Error("ожидалось значение, а не «" + _text + "»");
                }
            }

            private Node Call(string name)
            {
                Next(); // (
                var args = new List<Node>();
                if (_tok != Tok.RParen)
                {
                    while (true)
                    {
                        args.Add(Ternary());
                        if (_tok == Tok.Comma)
                        {
                            Next();
                            continue;
                        }

                        break;
                    }
                }

                if (_tok != Tok.RParen) throw Error("ожидалось «)»");
                Next();

                void Arity(int min, int max)
                {
                    if (args.Count < min || args.Count > max) throw Error($"{name}: аргументов {args.Count}, нужно {(min == max ? min.ToString() : min + "–" + max)}");
                }

                switch (name)
                {
                    case "abs":
                        Arity(1, 1);
                        return r => Map(args[0](r), Math.Abs);
                    case "round":
                        Arity(1, 2);
                        return r =>
                        {
                            var v = Num(args[0](r));
                            var d = args.Count > 1 ? Num(args[1](r)) : 0;
                            if (double.IsNaN(v) || double.IsNaN(d)) return JsonValue.Null;
                            return JsonValue.Of(Math.Round(v, (int)Math.Max(0, Math.Min(10, d)), MidpointRounding.AwayFromZero));
                        };
                    case "min":
                        Arity(2, 2);
                        return r => Arith(args[0](r), args[1](r), Math.Min);
                    case "max":
                        Arity(2, 2);
                        return r => Arith(args[0](r), args[1](r), Math.Max);
                    case "len":
                        Arity(1, 1);
                        return r =>
                        {
                            var v = args[0](r);
                            return v.Kind == JsonKind.String ? JsonValue.Of(v.Text.Length) : JsonValue.Null;
                        };
                    case "lower":
                        Arity(1, 1);
                        return r =>
                        {
                            var v = args[0](r);
                            return v.IsNull ? v : JsonValue.Of(v.AsText().ToLowerInvariant());
                        };
                    case "has":
                        Arity(1, 1);
                        return r => JsonValue.Of(!args[0](r).IsNull);
                    case "if":
                        Arity(3, 3);
                        return r => Truthy(args[0](r)) ? args[1](r) : args[2](r);
                    default:
                        throw Error("нет функции «" + name + "» (есть: abs round min max len lower has if)");
                }
            }
        }

        // ============================================================ значения

        private static double Num(JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.Number: return v.Number;
                case JsonKind.Bool: return v.Flag ? 1 : 0;
                case JsonKind.String:
                    return double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
                default: return double.NaN;
            }
        }

        private static JsonValue Arith(JsonValue a, JsonValue b, Func<double, double, double> f)
        {
            var x = Num(a);
            var y = Num(b);
            if (double.IsNaN(x) || double.IsNaN(y)) return JsonValue.Null;
            var v = f(x, y);
            return double.IsNaN(v) || double.IsInfinity(v) ? JsonValue.Null : JsonValue.Of(v);
        }

        private static JsonValue Map(JsonValue a, Func<double, double> f)
        {
            var x = Num(a);
            return double.IsNaN(x) ? JsonValue.Null : JsonValue.Of(f(x));
        }

        private static bool Equal(JsonValue a, JsonValue b)
        {
            if (a.IsNull || b.IsNull) return a.IsNull && b.IsNull;
            if (a.Kind == JsonKind.Number || b.Kind == JsonKind.Number)
            {
                var x = Num(a);
                var y = Num(b);
                if (!double.IsNaN(x) && !double.IsNaN(y)) return Math.Abs(x - y) < 1e-9;
            }

            if (a.Kind == JsonKind.Bool || b.Kind == JsonKind.Bool) return Truthy(a) == Truthy(b);
            return string.Equals(a.AsText(), b.AsText(), StringComparison.Ordinal);
        }

        private static int? Order(JsonValue a, JsonValue b)
        {
            if (a.IsNull || b.IsNull) return null;
            var x = Num(a);
            var y = Num(b);
            if (!double.IsNaN(x) && !double.IsNaN(y)) return x.CompareTo(y);
            if (a.Kind == JsonKind.String && b.Kind == JsonKind.String) return string.CompareOrdinal(a.Text, b.Text);
            return null;
        }

        private static bool Contains(JsonValue a, JsonValue b)
        {
            if (a.IsNull || b.IsNull) return false;
            var hay = a.AsText();
            var needle = b.AsText();
            return hay != null && needle != null && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
