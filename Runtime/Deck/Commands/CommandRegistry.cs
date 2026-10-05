using System;
using System.Collections.Generic;
using System.Globalization;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Хэндл зарегистрированной команды: слот + версия. Снятая команда делает хэндл «протухшим» —
    /// повторное снятие по нему ничего не трогает, даже если слот уже занят другой командой.
    /// </summary>
    public readonly struct CommandHandle : IEquatable<CommandHandle>
    {
        internal readonly int Slot;
        internal readonly int Version;

        internal CommandHandle(int slot, int version)
        {
            Slot = slot;
            Version = version;
        }

        /// <summary>Хэндл когда-то был выдан (не default). Жива ли команда — <see cref="AppDeck.IsRegistered"/>.</summary>
        public bool IsAssigned => Version != 0;

        public bool Equals(CommandHandle other) => Slot == other.Slot && Version == other.Version;
        public override bool Equals(object obj) => obj is CommandHandle other && Equals(other);
        public override int GetHashCode() => (Slot * 397) ^ Version;
    }

    /// <summary>
    /// Реестр команд: слоты с версиями в параллельных массивах, индекс имён и выполнение строк.
    /// Регистрация и снятие — в любой момент рантайма, с главного потока.
    /// </summary>
    /// <remarks>
    /// Имена держит отсортированный массив слотов: поиск команды по имени и по префиксу —
    /// бинарный поиск по <see cref="ReadOnlySpan{T}"/> прямо по вводу, без подстрок. Массив
    /// перестраивается лениво, только после регистрации или снятия.
    /// </remarks>
    internal sealed class CommandRegistry
    {
        private CommandSpec[] _specs = new CommandSpec[64];
        private CommandHandler[] _handlers = new CommandHandler[64];
        private object[] _owners = new object[64];
        private int[] _versions = new int[64];

        private int[] _free = new int[16];
        private int _freeCount;
        private int _highWater;
        private int _alive;

        private int[] _sorted = new int[64];
        private int _sortedCount;
        private bool _sortedDirty;

        // контексты на случай вложенных вызовов (команда выполняет команду)
        private readonly List<CommandContext> _contexts = new List<CommandContext>();
        private int _depth;

        /// <summary>Растёт на каждое изменение состава. UI по нему понимает, что кэш устарел.</summary>
        public int Version { get; private set; }

        public int Count => _alive;

        // ---------------------------------------------------------------- регистрация

        public CommandHandle Register(CommandSpec spec, CommandHandler handler, object owner)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            PrepareDefaults(spec);

            // одно имя — одна команда: поздняя регистрация побеждает, прежняя снимается
            int existing = Find(spec.Name.AsSpan());
            if (existing >= 0)
            {
                if (!ReferenceEquals(_owners[existing], owner))
                    AppDeck.Print($"Команда «{spec.Name}» перерегистрирована другим владельцем — прежняя снята.", DeckLogType.Warning);

                Release(existing);
            }

            int slot = _freeCount > 0 ? _free[--_freeCount] : _highWater++;
            EnsureCapacity(slot + 1);

            _specs[slot] = spec;
            _handlers[slot] = handler;
            _owners[slot] = owner;

            int version = _versions[slot] + 1;
            if (version <= 0) version = 1;
            _versions[slot] = version;

            _alive++;
            _sortedDirty = true;
            Version++;

            return new CommandHandle(slot, version);
        }

        public bool IsAlive(CommandHandle handle) =>
            handle.Version != 0 && handle.Slot >= 0 && handle.Slot < _highWater &&
            _versions[handle.Slot] == handle.Version && _specs[handle.Slot] != null;

        public bool Unregister(CommandHandle handle)
        {
            if (!IsAlive(handle)) return false;

            Release(handle.Slot);
            return true;
        }

        public bool Unregister(string name)
        {
            int slot = Find(name.AsSpan());
            if (slot < 0) return false;

            Release(slot);
            return true;
        }

        /// <summary>Снять всё, что зарегистрировал владелец. Возвращает, сколько снято.</summary>
        public int UnregisterAll(object owner)
        {
            if (owner == null) return 0;

            var removed = 0;
            for (var i = 0; i < _highWater; i++)
            {
                if (_specs[i] == null || !ReferenceEquals(_owners[i], owner)) continue;

                Release(i);
                removed++;
            }

            return removed;
        }

        private void Release(int slot)
        {
            _specs[slot] = null;
            _handlers[slot] = null;
            _owners[slot] = null;

            if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
            _free[_freeCount++] = slot;

            _alive--;
            _sortedDirty = true;
            Version++;
        }

        private void EnsureCapacity(int size)
        {
            if (size <= _specs.Length) return;

            int n = Math.Max(size, _specs.Length * 2);
            Array.Resize(ref _specs, n);
            Array.Resize(ref _handlers, n);
            Array.Resize(ref _owners, n);
            Array.Resize(ref _versions, n);
        }

        // ---------------------------------------------------------------- поиск

        public CommandSpec Spec(int slot) => slot >= 0 && slot < _highWater ? _specs[slot] : null;

        public object Owner(int slot) => slot >= 0 && slot < _highWater ? _owners[slot] : null;

        /// <summary>Отсортированный по имени список живых слотов (для подсказок, help и Actions).</summary>
        public int SortedCount
        {
            get
            {
                RebuildSorted();
                return _sortedCount;
            }
        }

        public int SortedSlot(int index)
        {
            RebuildSorted();
            return _sorted[index];
        }

        /// <summary>Слот команды с таким именем (без учёта регистра), -1 — нет.</summary>
        public int Find(ReadOnlySpan<char> name)
        {
            RebuildSorted();

            int lo = 0, hi = _sortedCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int cmp = _specs[_sorted[mid]].Name.AsSpan().CompareTo(name, StringComparison.OrdinalIgnoreCase);

                if (cmp == 0) return _sorted[mid];
                if (cmp < 0) lo = mid + 1;
                else hi = mid - 1;
            }

            return -1;
        }

        /// <summary>Диапазон отсортированного списка, имена в котором начинаются с <paramref name="prefix"/>.</summary>
        public void PrefixRange(ReadOnlySpan<char> prefix, out int first, out int count)
        {
            RebuildSorted();

            // нижняя граница: первое имя, которое не меньше префикса
            int lo = 0, hi = _sortedCount;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_specs[_sorted[mid]].Name.AsSpan().CompareTo(prefix, StringComparison.OrdinalIgnoreCase) < 0) lo = mid + 1;
                else hi = mid;
            }

            first = lo;
            int end = lo;
            while (end < _sortedCount && _specs[_sorted[end]].Name.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) end++;
            count = end - first;
        }

        private void RebuildSorted()
        {
            if (!_sortedDirty) return;
            _sortedDirty = false;

            if (_sorted.Length < _alive) _sorted = new int[Math.Max(_alive, _sorted.Length * 2)];

            _sortedCount = 0;
            for (var i = 0; i < _highWater; i++)
            {
                if (_specs[i] != null) _sorted[_sortedCount++] = i;
            }

            // вставками: команд десятки-сотни, перестройка редкая, а компаратор без замыкания
            for (var i = 1; i < _sortedCount; i++)
            {
                int slot = _sorted[i];
                string name = _specs[slot].Name;
                int j = i - 1;

                while (j >= 0 && string.Compare(_specs[_sorted[j]].Name, name, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    _sorted[j + 1] = _sorted[j];
                    j--;
                }

                _sorted[j + 1] = slot;
            }
        }

        // ---------------------------------------------------------------- выполнение

        /// <summary>
        /// Выполнить строку: одна или несколько команд через «;». Возвращает false, если хоть одна
        /// не нашлась, не разобралась или упала.
        /// </summary>
        public bool Execute(string line, CommandSource source, DeckTarget target)
        {
            if (string.IsNullOrWhiteSpace(line)) return true;

            var ok = true;
            int from = 0;

            while (CommandLine.NextStatement(line, from, out int start, out int end, out int next))
            {
                from = next;
                if (!ExecuteStatement(line, start, end, source, target)) ok = false;
            }

            return ok;
        }

        private bool ExecuteStatement(string line, int start, int end, CommandSource source, DeckTarget target)
        {
            Span<TokenRange> tokens = stackalloc TokenRange[CommandLine.MaxTokens];
            int count = CommandLine.Tokenize(line, start, end, tokens);
            if (count == 0) return true;

            int slot = Find(tokens[0].Span(line));
            if (slot < 0)
            {
                AppDeck.Print($"Нет команды «{CommandLine.Value(line, tokens[0])}». Список — help.", DeckLogType.Error);
                return false;
            }

            CommandSpec spec = _specs[slot];
            CommandHandler handler = _handlers[slot];

            if (_depth >= 16)
            {
                AppDeck.Print($"«{spec.Name}»: слишком глубокая вложенность команд — похоже на цикл.", DeckLogType.Error);
                return false;
            }

            if (_contexts.Count <= _depth) _contexts.Add(new CommandContext());
            CommandContext ctx = _contexts[_depth];
            ctx.Begin(spec, source, target, line, start, end);

            if (!BindArgs(spec, line, tokens.Slice(0, count), end, ctx.Values, target, out string error))
            {
                ctx.End();
                AppDeck.Print($"{error}\n  {spec.Signature}", DeckLogType.Error);
                return false;
            }

            _depth++;
            try
            {
                handler(ctx);
                return !ctx.Failed;
            }
            catch (Exception e)
            {
                AppDeck.Print($"«{spec.Name}» упала: {e.GetType().Name}: {e.Message}", DeckLogType.Error, e.ToString());
                return false;
            }
            finally
            {
                _depth--;
                ctx.End();
            }
        }

        // ---------------------------------------------------------------- аргументы

        private static bool BindArgs(CommandSpec spec, string line, ReadOnlySpan<TokenRange> tokens, int statementEnd,
                                     ArgValue[] values, DeckTarget target, out string error)
        {
            ArgSpec[] args = spec.Args;
            int given = tokens.Length - 1;

            if (given > args.Length && (args.Length == 0 || args[args.Length - 1].Kind != ArgKind.Rest))
            {
                error = $"«{spec.Name}»: лишние аргументы (ждёт {args.Length}, дано {given}).";
                return false;
            }

            for (var i = 0; i < args.Length; i++)
            {
                ArgSpec arg = args[i];
                int t = i + 1;

                if (t >= tokens.Length)
                {
                    if (arg.Kind == ArgKind.Target)
                    {
                        values[i].Target = target;
                        values[i].Present = target.IsValid || arg.HasDefault;
                        continue;
                    }

                    if (arg.HasDefault)
                    {
                        values[i] = arg.DefaultValue;
                        continue;
                    }

                    if (!arg.Optional)
                    {
                        error = $"«{spec.Name}»: не хватает аргумента «{arg.Name}».";
                        return false;
                    }

                    continue;
                }

                if (arg.Kind == ArgKind.Rest)
                {
                    int s = tokens[t].OuterStart;
                    values[i].Text = line.Substring(s, statementEnd - s).Trim();
                    values[i].Present = true;
                    break;
                }

                if (!TryParse(arg, line, tokens[t], target, ref values[i], out error))
                {
                    error = $"«{spec.Name}»: {error}";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>Разобрать значение одного аргумента из токена.</summary>
        internal static bool TryParse(ArgSpec arg, string text, in TokenRange token, DeckTarget target,
                                      ref ArgValue value, out string error)
        {
            ReadOnlySpan<char> span = token.Span(text);
            error = null;
            value.Present = true;

            switch (arg.Kind)
            {
                case ArgKind.Int:
                    if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out value.Int))
                    {
                        value.Float = value.Int;
                        return true;
                    }

                    // «1.0» в целом аргументе — не повод ругаться, если число целое
                    if (TryParseFloat(span, out double asFloat) && Math.Abs(asFloat - Math.Round(asFloat)) < 1e-9)
                    {
                        value.Int = (long)Math.Round(asFloat);
                        value.Float = value.Int;
                        return true;
                    }

                    error = $"«{arg.Name}» — нужно целое, а не «{span.ToString()}».";
                    return false;

                case ArgKind.Float:
                    if (TryParseFloat(span, out value.Float))
                    {
                        value.Int = (long)value.Float;
                        return true;
                    }

                    error = $"«{arg.Name}» — нужно число, а не «{span.ToString()}».";
                    return false;

                case ArgKind.Bool:
                    if (TryParseBool(span, out value.Bool)) return true;

                    error = $"«{arg.Name}» — нужно true/false (1/0, on/off), а не «{span.ToString()}».";
                    return false;

                case ArgKind.Choice:
                    for (var c = 0; c < arg.Choices.Length; c++)
                    {
                        if (!span.Equals(arg.Choices[c].AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;

                        // каноническая строка варианта — без подстроки ввода
                        value.Text = arg.Choices[c];
                        value.Int = c;
                        return true;
                    }

                    error = $"«{arg.Name}» — одно из: {string.Join(", ", arg.Choices)}.";
                    return false;

                case ArgKind.Target:
                    if (span.Length == 0 || span[0] == '$')
                    {
                        value.Target = target;
                        value.Present = target.IsValid;
                        if (!value.Present)
                        {
                            error = $"«{arg.Name}» — цель не выбрана.";
                            return false;
                        }

                        return true;
                    }

                    // слово вместо «$»: спросить резолверы (#42, me, имя); не ответили — команда решит сама (all)
                    value.Text = CommandLine.Value(text, token);
                    if (AppDeck.TryResolveTarget(value.Text, out DeckTarget resolved)) value.Target = resolved;
                    return true;

                default:
                    value.Text = CommandLine.Value(text, token);
                    return true;
            }
        }

        internal static bool TryParseFloat(ReadOnlySpan<char> span, out double result)
        {
            if (double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return true;

            // запятая вместо точки — частая привычка; копия на стеке, без аллокации
            if (span.Length > 64 || span.IndexOf(',') < 0) return false;

            Span<char> copy = stackalloc char[span.Length];
            span.CopyTo(copy);
            for (var i = 0; i < copy.Length; i++)
            {
                if (copy[i] == ',') copy[i] = '.';
            }

            return double.TryParse(copy, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        internal static bool TryParseBool(ReadOnlySpan<char> span, out bool result)
        {
            if (Is(span, "true") || Is(span, "1") || Is(span, "on") || Is(span, "yes") || Is(span, "да") || Is(span, "+"))
            {
                result = true;
                return true;
            }

            if (Is(span, "false") || Is(span, "0") || Is(span, "off") || Is(span, "no") || Is(span, "нет") || Is(span, "-"))
            {
                result = false;
                return true;
            }

            result = false;
            return false;

            static bool Is(ReadOnlySpan<char> s, string word) => s.Equals(word.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Разобрать умолчания один раз при регистрации: битое умолчание — ошибка регистрации, а не вызова.</summary>
        private static void PrepareDefaults(CommandSpec spec)
        {
            ArgSpec[] args = spec.Args;
            for (var i = 0; i < args.Length; i++)
            {
                ArgSpec arg = args[i];
                if (arg.DefaultText == null || arg.HasDefault) continue;

                if (arg.Kind == ArgKind.Rest || arg.Kind == ArgKind.String || arg.Kind == ArgKind.Target)
                {
                    arg.DefaultValue = new ArgValue { Present = true, Text = arg.DefaultText };
                    arg.HasDefault = true;
                    continue;
                }

                var token = new TokenRange(0, arg.DefaultText.Length, 0, arg.DefaultText.Length, false, false);
                var value = default(ArgValue);

                if (!TryParse(arg, arg.DefaultText, token, default, ref value, out string error))
                    throw new ArgumentException($"Команда «{spec.Name}»: умолчание аргумента не разбирается — {error}");

                arg.DefaultValue = value;
                arg.HasDefault = true;
            }
        }
    }
}
