using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Строка ввода консоли: подсказки, Tab-дописывание, сигнатура текущей команды, история.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Анализ ввода — по токенам-диапазонам на стеке, поиск команд — бинарный по отсортированному
    /// реестру прямо по куску ввода. Строки подсказок — уже существующие (имена команд, варианты,
    /// значения источников), их не создают на каждое нажатие.
    /// </para>
    /// <para>
    /// Подсказка-сигнатура пересобирается, только когда сменилась команда или аргумент под кареткой,
    /// а не на каждый символ. Единственная аллокация на нажатие — значение самого TextField.
    /// </para>
    /// </remarks>
    internal sealed class ConsoleInput : VisualElement
    {
        private const int MaxSuggestions = 8;
        private const string PrefsHistory = "appdeck.history";

        private readonly TextField _field;
        private readonly Label _hint;
        private readonly VisualElement _popup;
        private readonly SuggestionRow[] _rows = new SuggestionRow[MaxSuggestions];

        private readonly string[] _sugText = new string[MaxSuggestions];
        private readonly string[] _sugDetail = new string[MaxSuggestions];
        private readonly List<string> _collect = new List<string>(MaxSuggestions + 1);
        private int _sugCount;
        private int _sugSelected = -1;
        private bool _sugExplicit;
        private bool _sugIsCommand;
        private bool _popupSuppressed;

        // что заменит подстановка: диапазон токена под кареткой и введённый префикс
        private int _replStart;
        private int _replEnd;
        private int _prefixLength;

        private bool _cycling;
        private string _appliedText;

        // что было на прошлом анализе — анализируем, только если ввод или каретка сдвинулись
        private string _analyzedText;
        private int _analyzedCaret = -1;
        private int _analyzedRegistry = -1;

        // ключ подсказки-сигнатуры: пересобираем строку, только когда он сменился
        private int _hintSlot = -2;
        private int _hintArg = -2;
        private int _hintRegistry = -1;
        private readonly StringBuilder _hintBuilder = new StringBuilder(128);

        private readonly string[] _history;
        private int _historyCount;
        private int _historyCursor = -1;
        private string _draft;
        private string _historyText;
        private bool _forceSuggest;

        private bool _focused;

        public ConsoleInput(int historySize)
        {
            AddToClassList("appdeck-input");

            _popup = new VisualElement { name = "suggestions" };
            _popup.AddToClassList("appdeck-input__popup");
            _popup.style.position = Position.Absolute;
            _popup.style.display = DisplayStyle.None;
            Add(_popup);

            for (var i = 0; i < MaxSuggestions; i++)
            {
                var row = new SuggestionRow(i);
                row.RegisterCallback<PointerDownEvent>(OnSuggestionPointerDown);
                _rows[i] = row;
                _popup.Add(row);
            }

            _hint = new Label { name = "hint", enableRichText = true, parseEscapeSequences = false, pickingMode = PickingMode.Ignore };
            _hint.AddToClassList("appdeck-input__hint");
            Add(_hint);

            var line = new VisualElement();
            line.AddToClassList("appdeck-input__line");
            Add(line);

            var prompt = new Label(">") { pickingMode = PickingMode.Ignore };
            prompt.AddToClassList("appdeck-input__prompt");
            line.Add(prompt);

            _field = new TextField { name = "command", selectAllOnFocus = false, selectAllOnMouseUp = false };
            _field.AddToClassList("appdeck-input__field");
            _field.style.flexGrow = 1;
            line.Add(_field);

            _field.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _field.RegisterCallback<NavigationMoveEvent>(OnNavigationMove, TrickleDown.TrickleDown);
            _field.RegisterCallback<NavigationSubmitEvent>(OnNavigationSubmit, TrickleDown.TrickleDown);
            _field.RegisterCallback<FocusInEvent>(_ => _focused = true);
            _field.RegisterCallback<FocusOutEvent>(_ => _focused = false);

            _history = new string[Mathf.Max(8, historySize)];
            LoadHistory();
            UpdateHint(-1, -1, null);
        }

        public bool IsFocused => _focused;

        /// <summary>Строка отправлена в выполнение.</summary>
        public event Action<string> Submitted;

        /// <summary>Фокус в поле. <paramref name="caretToEnd"/> — каретку в конец (открытие окна); false — оставить, где была.</summary>
        public void FocusField(bool caretToEnd)
        {
            int cursor = _field.cursorIndex;
            _field.Focus();

            int end = _field.value?.Length ?? 0;
            int at = caretToEnd ? end : Mathf.Clamp(cursor, 0, end);
            _field.SelectRange(at, at);
        }

        /// <summary>
        /// Подставить токен у каретки: каретка внутри слова — слово заменяется (повторный выбор сущности
        /// меняет аргумент, а не копит их), иначе — вставка с пробелами вокруг.
        /// </summary>
        public bool InsertToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;

            token = CommandLine.Quote(token);
            string text = _field.value ?? string.Empty;
            int caret = Mathf.Clamp(_field.cursorIndex, 0, text.Length);

            int stmtStart = 0, stmtEnd = text.Length, from = 0;
            while (CommandLine.NextStatement(text, from, out int s, out int e, out int next))
            {
                stmtStart = s;
                stmtEnd = e;
                if (caret <= e) break;
                from = next;
            }

            if (caret > stmtEnd) stmtStart = stmtEnd = caret;

            Span<TokenRange> tokens = stackalloc TokenRange[CommandLine.MaxTokens];
            int count = CommandLine.Tokenize(text, stmtStart, stmtEnd, tokens);
            int ti = CommandLine.TokenAt(tokens.Slice(0, count), caret, out bool inside);

            // имя команды не заменяем — подставляем следующим словом
            if (inside && ti == 0)
            {
                caret = tokens[0].OuterEnd;
                inside = false;
            }

            string result;
            int at;

            if (inside)
            {
                TokenRange t = tokens[ti];
                result = string.Concat(text.Substring(0, t.OuterStart), token, text.Substring(t.OuterEnd));
                at = t.OuterStart + token.Length;
            }
            else
            {
                bool spaceBefore = caret > 0 && !char.IsWhiteSpace(text[caret - 1]);
                bool spaceAfter = caret >= text.Length || !char.IsWhiteSpace(text[caret]);
                string insert = (spaceBefore ? " " : string.Empty) + token + (spaceAfter ? " " : string.Empty);
                result = string.Concat(text.Substring(0, caret), insert, text.Substring(caret));
                at = caret + insert.Length;
            }

            _field.value = result;
            _field.SelectRange(at, at);
            return true;
        }

        /// <summary>Escape: сначала закрываем подсказки, потом уже окно.</summary>
        public bool HandleEscape()
        {
            if (_popup.style.display == DisplayStyle.None) return false;

            _popupSuppressed = true;
            ShowPopup(false);
            return true;
        }

        /// <summary>Кадр: анализ ввода, только если что-то сдвинулось.</summary>
        public void Tick()
        {
            string text = _field.value ?? string.Empty;
            int caret = Mathf.Clamp(_field.cursorIndex, 0, text.Length);
            int registry = AppDeck.CommandsVersion;

            if (ReferenceEquals(text, _analyzedText) && caret == _analyzedCaret && registry == _analyzedRegistry) return;

            // перебор вариантов Tab'ом: наш собственный ввод — не повод пересчитывать список
            if (_cycling && ReferenceEquals(text, _appliedText))
            {
                _analyzedText = text;
                _analyzedCaret = caret;
                return;
            }

            if (!ReferenceEquals(text, _analyzedText) && !ReferenceEquals(text, _historyText))
            {
                // правка руками: подсказки снова можно, листание истории закончилось
                _popupSuppressed = false;
                _historyCursor = -1;
                _historyText = null;
            }

            _cycling = false;
            _analyzedText = text;
            _analyzedCaret = caret;
            _analyzedRegistry = registry;

            Analyze(text, caret);
        }

        // ---------------------------------------------------------------- анализ

        private void Analyze(string text, int caret)
        {
            _sugCount = 0;
            _sugSelected = -1;
            _sugExplicit = false;
            _sugIsCommand = false;

            // команда, в которой стоит каретка (строка может содержать несколько через «;»)
            int stmtStart = 0, stmtEnd = text.Length, from = 0;
            while (CommandLine.NextStatement(text, from, out int s, out int e, out int next))
            {
                stmtStart = s;
                stmtEnd = e;
                if (caret <= e) break;
                from = next;
            }

            // «god on;|» — каретка за последней «;»: это новая, ещё пустая команда, а не аргумент прежней
            if (caret > stmtEnd) stmtStart = stmtEnd = caret;

            Span<TokenRange> tokens = stackalloc TokenRange[CommandLine.MaxTokens];
            int count = CommandLine.Tokenize(text, stmtStart, stmtEnd, tokens);
            ReadOnlySpan<TokenRange> used = tokens.Slice(0, count);

            int ti = CommandLine.TokenAt(used, caret, out bool inside);

            ReadOnlySpan<char> prefix;
            if (inside)
            {
                TokenRange t = used[ti];
                int pStart = t.Start;
                int pEnd = Mathf.Clamp(caret, t.Start, t.End);
                prefix = text.AsSpan(pStart, pEnd - pStart);
                _replStart = t.OuterStart;
                _replEnd = t.OuterEnd;
            }
            else
            {
                prefix = ReadOnlySpan<char>.Empty;
                _replStart = _replEnd = caret;
            }

            _prefixLength = prefix.Length;

            bool empty = count == 0 && !_forceSuggest;
            _forceSuggest = false;

            CommandRegistry registry = AppDeck.Commands;
            int slot = count > 0 ? registry.Find(used[0].Span(text)) : -1;

            _field.EnableInClassList("appdeck-input__field--known", slot >= 0);
            _field.EnableInClassList("appdeck-input__field--unknown", count > 0 && slot < 0 && !(ti == 0 && inside));

            if (empty)
            {
                UpdateHint(-1, -1, null);
            }
            else if (ti == 0)
            {
                // имя команды: диапазон отсортированного реестра по префиксу
                _sugIsCommand = true;
                registry.PrefixRange(prefix, out int first, out int n);

                for (var i = 0; i < n && _sugCount < MaxSuggestions; i++)
                {
                    CommandSpec spec = registry.Spec(registry.SortedSlot(first + i));
                    if (spec.Hidden) continue;

                    // единственный точный вариант, уже введённый целиком, — подсказывать нечего
                    if (n == 1 && prefix.Length == spec.Name.Length) break;

                    _sugText[_sugCount] = spec.Name;
                    _sugDetail[_sugCount] = spec.Summary;
                    _sugCount++;
                }

                UpdateHint(slot, -1, slot >= 0 ? registry.Spec(slot) : null);
            }
            else if (slot >= 0)
            {
                CommandSpec spec = registry.Spec(slot);
                int argIndex = ti - 1;

                if (argIndex >= spec.Args.Length)
                    argIndex = spec.Args.Length > 0 && spec.Args[spec.Args.Length - 1].Kind == ArgKind.Rest ? spec.Args.Length - 1 : -1;

                if (argIndex >= 0) CollectArgSuggestions(spec.Args[argIndex], prefix);

                UpdateHint(slot, argIndex, spec);
            }
            else
            {
                UpdateHint(-1, -1, null);
                _hint.text = "Нет такой команды. Tab на первом слове — подсказка, help — список.";
                _hintSlot = -2;
            }

            BindPopup();
        }

        private void CollectArgSuggestions(ArgSpec arg, ReadOnlySpan<char> prefix)
        {
            switch (arg.Kind)
            {
                case ArgKind.Choice:
                    for (var i = 0; i < arg.Choices.Length && _sugCount < MaxSuggestions; i++)
                        AddIfMatches(arg.Choices[i], prefix);
                    break;

                case ArgKind.Bool:
                    AddIfMatches("true", prefix);
                    AddIfMatches("false", prefix);
                    break;

                case ArgKind.Target:
                    AddIfMatches("$", prefix);
                    break;
            }

            if (arg.Source != null && _sugCount < MaxSuggestions &&
                AppDeck.Suggestions.Collect(arg.Source, prefix, _collect, MaxSuggestions - _sugCount))
            {
                for (var i = 0; i < _collect.Count && _sugCount < MaxSuggestions; i++)
                {
                    string value = _collect[i];
                    if (value == null || (prefix.Length == value.Length && prefix.Equals(value.AsSpan(), StringComparison.OrdinalIgnoreCase))) continue;

                    _sugText[_sugCount] = value;
                    _sugDetail[_sugCount] = null;
                    _sugCount++;
                }

                _collect.Clear();
            }
        }

        private void AddIfMatches(string value, ReadOnlySpan<char> prefix)
        {
            if (_sugCount >= MaxSuggestions || !value.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            if (prefix.Length == value.Length) return;

            _sugText[_sugCount] = value;
            _sugDetail[_sugCount] = null;
            _sugCount++;
        }

        private void UpdateHint(int slot, int argIndex, CommandSpec spec)
        {
            int registry = AppDeck.CommandsVersion;
            if (slot == _hintSlot && argIndex == _hintArg && registry == _hintRegistry) return;

            _hintSlot = slot;
            _hintArg = argIndex;
            _hintRegistry = registry;

            if (spec == null)
            {
                _hint.text = "Введите команду. Tab — дописать, ↑/↓ — история, help — список.";
                return;
            }

            StringBuilder sb = _hintBuilder.Clear();
            sb.Append("<b>").Append(spec.Name).Append("</b>");

            for (var i = 0; i < spec.Args.Length; i++)
            {
                sb.Append(' ');
                if (i == argIndex) sb.Append("<color=#FFD866><b>").Append(Escape(spec.Args[i].Display)).Append("</b></color>");
                else sb.Append(Escape(spec.Args[i].Display));
            }

            string about = argIndex >= 0 && !string.IsNullOrEmpty(spec.Args[argIndex].Description)
                ? spec.Args[argIndex].Description
                : spec.Summary;

            if (!string.IsNullOrEmpty(about)) sb.Append("  <color=#8A8F98>— ").Append(Escape(about)).Append("</color>");

            _hint.text = sb.ToString();
        }

        /// <summary>«&lt;» в сигнатуре — не тег разметки.</summary>
        private static string Escape(string value) =>
            value.IndexOf('<') < 0 ? value : value.Replace("<", "<noparse><</noparse>");

        // ---------------------------------------------------------------- всплывающий список

        private void BindPopup()
        {
            for (var i = 0; i < MaxSuggestions; i++)
            {
                if (i < _sugCount) _rows[i].Bind(_sugText[i], _sugIsCommand ? _sugDetail[i] : null, i == _sugSelected);
                _rows[i].style.display = i < _sugCount ? DisplayStyle.Flex : DisplayStyle.None;
            }

            ShowPopup(_sugCount > 0 && !_popupSuppressed);
        }

        private void ShowPopup(bool show)
        {
            DisplayStyle wanted = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (_popup.style.display != wanted) _popup.style.display = wanted;
        }

        private void MoveSelection(int dir)
        {
            if (_sugCount == 0) return;

            _sugSelected = _sugSelected < 0 ? (dir > 0 ? 0 : _sugCount - 1) : (_sugSelected + dir + _sugCount) % _sugCount;
            _sugExplicit = true;

            for (var i = 0; i < _sugCount; i++) _rows[i].SetSelected(i == _sugSelected);
        }

        private void OnSuggestionPointerDown(PointerDownEvent evt)
        {
            if (!(evt.currentTarget is SuggestionRow row) || row.Index >= _sugCount) return;

            Apply(row.Index, true);
            evt.StopPropagation();
            FocusField(false);
        }

        // ---------------------------------------------------------------- дописывание

        private void Complete(int dir)
        {
            if (_sugCount == 0)
            {
                // Tab на пустой строке (или после подавления) — показать варианты и сразу перейти к ним
                _forceSuggest = true;
                _popupSuppressed = false;
                Analyze(_field.value ?? string.Empty, Mathf.Clamp(_field.cursorIndex, 0, (_field.value ?? string.Empty).Length));
                return;
            }

            if (_cycling)
            {
                _sugSelected = (_sugSelected + dir + _sugCount) % _sugCount;
                Apply(_sugSelected, false);
                return;
            }

            if (_sugExplicit && _sugSelected >= 0)
            {
                Apply(_sugSelected, true);
                return;
            }

            if (_sugCount == 1)
            {
                Apply(0, true);
                return;
            }

            // сначала — общая часть всех вариантов; если дописывать нечего — перебор
            int common = CommonPrefixLength();
            if (common > _prefixLength)
            {
                ReplaceToken(_sugText[0].Substring(0, common), false);
                return;
            }

            _cycling = true;
            _sugSelected = dir > 0 ? 0 : _sugCount - 1;
            Apply(_sugSelected, false);
        }

        private int CommonPrefixLength()
        {
            int length = _sugText[0].Length;
            for (var i = 1; i < _sugCount; i++)
            {
                string s = _sugText[i];
                int n = Math.Min(length, s.Length);
                int k = 0;
                while (k < n && char.ToLowerInvariant(s[k]) == char.ToLowerInvariant(_sugText[0][k])) k++;
                length = k;
            }

            return length;
        }

        private void Apply(int index, bool finish)
        {
            if (index < 0 || index >= _sugCount) return;

            ReplaceToken(CommandLine.Quote(_sugText[index]), finish);

            for (var i = 0; i < _sugCount; i++) _rows[i].SetSelected(i == index);

            if (finish)
            {
                _cycling = false;
                _sugExplicit = false;
            }
        }

        private void ReplaceToken(string value, bool addSpace)
        {
            string text = _field.value ?? string.Empty;
            int start = Mathf.Clamp(_replStart, 0, text.Length);
            int end = Mathf.Clamp(_replEnd, start, text.Length);

            bool space = addSpace && (end >= text.Length || text[end] != ' ');
            string result = string.Concat(text.Substring(0, start), value, space ? " " : string.Empty, text.Substring(end));
            int caret = start + value.Length + (addSpace ? 1 : 0);

            _field.value = result;
            _field.SelectRange(caret, caret);

            // при переборе следующий вариант заменит этот же кусок
            _replEnd = start + value.Length;
            _appliedText = _field.value;
        }

        // ---------------------------------------------------------------- клавиши

        private void OnKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.Tab:
                    Complete(evt.shiftKey ? -1 : 1);
                    Consume(evt);
                    return;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_sugExplicit && _sugSelected >= 0 && _popup.style.display == DisplayStyle.Flex) Apply(_sugSelected, true);
                    else Submit();
                    Consume(evt);
                    return;

                case KeyCode.UpArrow:
                    if (ArrowsToPopup) MoveSelection(-1);
                    else HistoryStep(1);
                    Consume(evt);
                    return;

                case KeyCode.DownArrow:
                    if (ArrowsToPopup) MoveSelection(1);
                    else HistoryStep(-1);
                    Consume(evt);
                    return;
            }

            // парные «символьные» события тех же клавиш
            if (evt.character == '\t' || evt.character == '\n' || evt.character == '\r') Consume(evt);
        }

        /// <summary>Стрелки листают подсказки, только если они видны и мы не листаем историю (или уже выбираем в списке).</summary>
        private bool ArrowsToPopup =>
            _popup.style.display == DisplayStyle.Flex && (_sugExplicit || _historyCursor == -1);

        private void OnNavigationMove(NavigationMoveEvent evt)
        {
            // Tab и стрелки не должны уводить фокус из поля
            Consume(evt);
        }

        private void OnNavigationSubmit(NavigationSubmitEvent evt) => Consume(evt);

        private void Consume(EventBase evt)
        {
            evt.StopImmediatePropagation();
            _field.focusController?.IgnoreEvent(evt);
        }

        // ---------------------------------------------------------------- отправка и история

        private void Submit()
        {
            string line = (_field.value ?? string.Empty).Trim();
            if (line.Length == 0) return;

            PushHistory(line);
            _historyCursor = -1;
            _historyText = null;
            _draft = null;

            _field.value = string.Empty;
            ShowPopup(false);

            Submitted?.Invoke(line);
            FocusField(true);
        }

        private void PushHistory(string line)
        {
            // повтор последней команды историю не засоряет
            if (_historyCount > 0 && _history[0] == line) return;

            int n = Math.Min(_historyCount, _history.Length - 1);
            Array.Copy(_history, 0, _history, 1, n);
            _history[0] = line;
            _historyCount = Math.Min(_historyCount + 1, _history.Length);

            SaveHistory();
        }

        private void HistoryStep(int dir)
        {
            if (_historyCount == 0) return;

            if (_historyCursor == -1) _draft = _field.value;

            int next = Mathf.Clamp(_historyCursor + dir, -1, _historyCount - 1);
            if (next == _historyCursor) return;

            _historyCursor = next;
            string value = next == -1 ? _draft ?? string.Empty : _history[next];

            _field.value = value;
            _field.SelectRange(value.Length, value.Length);

            // строка из истории — без всплывающих подсказок, пока её не начнут править
            _historyText = _field.value;
            _popupSuppressed = true;
            ShowPopup(false);
            if (next == -1) _historyText = null;
        }

        private void LoadHistory()
        {
            string saved = PlayerPrefs.GetString(PrefsHistory, string.Empty);
            if (saved.Length == 0) return;

            string[] lines = saved.Split('\n');
            _historyCount = 0;
            for (var i = 0; i < lines.Length && _historyCount < _history.Length; i++)
            {
                if (lines[i].Length > 0) _history[_historyCount++] = lines[i];
            }
        }

        private void SaveHistory()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < _historyCount; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(_history[i].Replace('\n', ' '));
            }

            PlayerPrefs.SetString(PrefsHistory, sb.ToString());
        }

        // ---------------------------------------------------------------- строка подсказки

        private sealed class SuggestionRow : VisualElement
        {
            private readonly Label _name;
            private readonly Label _detail;
            private string _shownName;
            private string _shownDetail;

            public SuggestionRow(int index)
            {
                Index = index;
                AddToClassList("appdeck-input__suggestion");

                _name = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
                _name.AddToClassList("appdeck-input__suggestion-name");
                Add(_name);

                _detail = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
                _detail.AddToClassList("appdeck-input__suggestion-detail");
                Add(_detail);
            }

            public int Index { get; }

            public void Bind(string name, string detail, bool selected)
            {
                if (!ReferenceEquals(name, _shownName))
                {
                    _shownName = name;
                    _name.text = name;
                }

                if (!ReferenceEquals(detail, _shownDetail))
                {
                    _shownDetail = detail;
                    _detail.text = detail ?? string.Empty;
                    _detail.style.display = string.IsNullOrEmpty(detail) ? DisplayStyle.None : DisplayStyle.Flex;
                }

                SetSelected(selected);
            }

            public void SetSelected(bool selected) => EnableInClassList("appdeck-input__suggestion--selected", selected);
        }
    }
}
