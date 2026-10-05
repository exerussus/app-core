using System;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Вкладка Console: логи (виртуальный список), фильтры, поиск, детали записи и строка команд.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Представление — кольцо номеров записей, прошедших фильтр, той же ёмкости, что и буфер логов.
    /// Новые записи дописываются в конец, вытесненные уходят из начала — без пересборки. Полная
    /// пересборка — только при смене фильтра или поиска.
    /// </para>
    /// <para>
    /// Кадр без новых логов — одно сравнение версии. С новыми — дописывание номеров и перепривязка
    /// видимых строк (строка сама не трогает элементы, если значение то же).
    /// </para>
    /// </remarks>
    internal sealed class ConsoleTab : DeckTab, IVirtualListAdapter
    {
        public const string TabId = "console";

        private const string PrefsMask = "appdeck.console.mask";

        private static readonly string[] FilterTitles = { "Log", "Warn", "Error", "Cmd" };

        // бит фильтра → какие виды записей он включает
        private static readonly int[] FilterMasks =
        {
            1 << (int)DeckLogType.Log,
            1 << (int)DeckLogType.Warning,
            1 << (int)DeckLogType.Error,
            (1 << (int)DeckLogType.Input) | (1 << (int)DeckLogType.Output),
        };

        private readonly DeckSettings _settings;

        private VirtualList _list;
        private ConsoleInput _input;
        private TextField _search;
        private VisualElement _details;
        private TextField _detailsText;
        private Button _collapseButton;
        private Button _stickButton;
        private readonly Button[] _filterButtons = new Button[4];
        private readonly Label[] _filterCounts = new Label[4];
        private readonly int[] _shownCounts = { -1, -1, -1, -1 };

        private int _filterMask;
        private string _query = string.Empty;

        private long[] _view = new long[0];
        private int _viewMask;
        private int _viewStart;
        private int _viewCount;
        private long _synced;
        private int _seenVersion = -1;
        private LogBuffer _bound;

        private long _selectedSeq = -1;

        public ConsoleTab(DeckSettings settings) => _settings = settings;

        public override string Id => TabId;

        public override string Title => "Console";

        public override int Order => -300;

        protected internal override bool IsTyping => _input != null && (_input.IsFocused || _search.focusController?.focusedElement == _search);

        // ---------------------------------------------------------------- вёрстка

        protected internal override void Build(VisualElement root)
        {
            root.AddToClassList("appdeck-console");

            var toolbar = new VisualElement();
            toolbar.AddToClassList("appdeck-toolbar");
            root.Add(toolbar);

            _filterMask = PlayerPrefs.GetInt(PrefsMask, 0b1111);

            for (var i = 0; i < FilterTitles.Length; i++)
            {
                int bit = i;
                var button = new Button(() => ToggleFilter(bit));
                button.AddToClassList("appdeck-filter");
                button.AddToClassList("appdeck-filter--" + FilterTitles[i].ToLowerInvariant());

                var title = new Label(FilterTitles[i]) { pickingMode = PickingMode.Ignore };
                title.AddToClassList("appdeck-filter__title");
                button.Add(title);

                var count = new Label { pickingMode = PickingMode.Ignore };
                count.AddToClassList("appdeck-filter__count");
                button.Add(count);

                _filterButtons[i] = button;
                _filterCounts[i] = count;
                toolbar.Add(button);
            }

            _search = new TextField { name = "search" };
            _search.AddToClassList("appdeck-search");
            _search.textEdition.placeholder = "Поиск";
            _search.RegisterValueChangedCallback(OnSearchChanged);
            toolbar.Add(_search);

            _collapseButton = ToolbarButton(toolbar, "≡", "Сворачивать повторы", ToggleCollapse);
            _stickButton = ToolbarButton(toolbar, "⤓", "Держать в конце", () => _list.StickToEnd = !_list.StickToEnd);
            ToolbarButton(toolbar, "⧉", "Копировать (выбранное или всё видимое)", Copy);
            ToolbarButton(toolbar, "⌫", "Очистить", AppDeck.ClearLog);

            _list = new VirtualList { name = "log", RowHeight = 20f };
            _list.AddToClassList("appdeck-console__list");
            _list.SetAdapter(this);
            _list.SelectionChanged += OnSelectionChanged;
            _list.StickChanged += _ => RefreshStickButton();
            root.Add(_list);

            _details = new VisualElement { name = "details" };
            _details.AddToClassList("appdeck-console__details");
            _details.style.display = DisplayStyle.None;
            _detailsText = new TextField { multiline = true, isReadOnly = true };
            _detailsText.AddToClassList("appdeck-console__details-text");
            _detailsText.verticalScrollerVisibility = ScrollerVisibility.Auto;
            _details.Add(_detailsText);
            root.Add(_details);

            _input = new ConsoleInput(_settings != null ? _settings.HistorySize : 64);
            _input.Submitted += OnSubmitted;
            root.Add(_input);

            RefreshFilterButtons();
            RefreshCollapseButton();
            RefreshStickButton();
        }

        private static Button ToolbarButton(VisualElement toolbar, string text, string tooltip, Action click)
        {
            var button = new Button(click) { text = text, tooltip = tooltip };
            button.AddToClassList("appdeck__icon-button");
            toolbar.Add(button);
            return button;
        }

        protected internal override void Focus() => _input?.FocusField(true);

        protected internal override void Refocus() => _input?.FocusField(false);

        protected internal override bool HandleEscape() => _input != null && _input.HandleEscape();

        protected internal override void OnShow()
        {
            // за время, пока вкладка была скрыта, логи шли — догоняем одним проходом
            _seenVersion = -1;
        }

        // ---------------------------------------------------------------- кадр

        protected internal override void Tick(float deltaTime)
        {
            _input.Tick();

            LogBuffer log = AppDeck.Log;
            if (log == null) return;

            if (!ReferenceEquals(log, _bound))
            {
                _bound = log;
                Rebuild();
            }

            if (log.Version == _seenVersion) return;
            _seenVersion = log.Version;

            int before = _viewCount;
            int removed = Sync(log);

            // кольцо полно: из начала ушло столько же, сколько пришло в конец — число строк то же,
            // но индексы сдвинулись. Прокрутку и выбор переносим вслед за записями.
            if (removed > 0)
            {
                _list.ShiftForRemoved(removed);
                _list.SetSelectedSilent(IndexOfSeq(_selectedSeq));
            }

            if (_viewCount != before) _list.SetCount(_viewCount);
            _list.Refresh(true);

            RefreshCounts(log);
        }

        // ---------------------------------------------------------------- представление

        private void Rebuild()
        {
            LogBuffer log = _bound;
            if (log == null) return;

            if (_view.Length != log.Capacity)
            {
                _view = new long[log.Capacity];
                _viewMask = log.Capacity - 1;
            }

            _viewStart = 0;
            _viewCount = 0;
            _synced = log.Oldest;
            _seenVersion = -1;

            long keep = _selectedSeq;
            Sync(log);
            _list.SetCount(_viewCount);

            // выбранная запись прошла новый фильтр — остаётся выбранной; нет — детали прячем
            int index = IndexOfSeq(keep);
            _list.SetSelectedSilent(index);
            if (index < 0) SelectSeq(-1);

            _list.Refresh(true);
        }

        /// <summary>Дописать новые записи, выкинуть вытесненные. Возвращает, сколько ушло из начала.</summary>
        private int Sync(LogBuffer log)
        {
            var removed = 0;

            // вытесненные из буфера записи уходят из начала представления
            long oldest = log.Oldest;
            while (_viewCount > 0 && _view[_viewStart] < oldest)
            {
                _viewStart = (_viewStart + 1) & _viewMask;
                _viewCount--;
                removed++;
            }

            if (_synced < oldest) _synced = oldest;

            long next = log.Next;
            for (long seq = _synced; seq < next; seq++)
            {
                if (!Matches(log, seq)) continue;

                if (_viewCount == _view.Length)
                {
                    _viewStart = (_viewStart + 1) & _viewMask;
                    _viewCount--;
                    removed++;
                }

                _view[(_viewStart + _viewCount) & _viewMask] = seq;
                _viewCount++;
            }

            _synced = next;

            if (_selectedSeq >= 0 && !log.IsAlive(_selectedSeq)) SelectSeq(-1);
            return removed;
        }

        private bool Matches(LogBuffer log, long seq)
        {
            int bit = 1 << (int)log.TypeOf(seq);
            var pass = false;
            for (var i = 0; i < FilterMasks.Length; i++)
            {
                if ((_filterMask & (1 << i)) != 0 && (FilterMasks[i] & bit) != 0)
                {
                    pass = true;
                    break;
                }
            }

            if (!pass) return false;
            if (_query.Length == 0) return true;

            return log.MessageOf(seq).IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private long SeqAt(int index) => _view[(_viewStart + index) & _viewMask];

        private int IndexOfSeq(long seq)
        {
            if (seq < 0) return -1;

            // номера в представлении возрастают — бинарный поиск
            int lo = 0, hi = _viewCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                long s = SeqAt(mid);
                if (s == seq) return mid;
                if (s < seq) lo = mid + 1;
                else hi = mid - 1;
            }

            return -1;
        }

        // ---------------------------------------------------------------- строки

        public VisualElement MakeRow() => new LogRow();

        public void BindRow(VisualElement row, int index)
        {
            if (_bound == null || index < 0 || index >= _viewCount) return;
            ((LogRow)row).Bind(_bound, SeqAt(index));
        }

        // ---------------------------------------------------------------- выбор и детали

        private void OnSelectionChanged(int index)
        {
            long seq = index >= 0 && index < _viewCount ? SeqAt(index) : -1;
            SelectSeq(seq);
        }

        private void SelectSeq(long seq)
        {
            _selectedSeq = seq;

            if (_bound == null || seq < 0 || !_bound.IsAlive(seq))
            {
                _selectedSeq = -1;
                _details.style.display = DisplayStyle.None;
                return;
            }

            _details.style.display = DisplayStyle.Flex;
            _detailsText.value = DetailsOf(_bound, seq);
        }

        private static string DetailsOf(LogBuffer log, long seq)
        {
            string message = log.MessageOf(seq);
            string stack = log.StackOf(seq);
            int repeat = log.RepeatOf(seq);

            var sb = new StringBuilder(message.Length + (stack?.Length ?? 0) + 32);
            sb.Append(message);
            if (repeat > 1) sb.Append("\n\n×").Append(repeat).Append(", последний — кадр ").Append(log.FrameOf(seq));
            else sb.Append("\n\nКадр ").Append(log.FrameOf(seq));
            if (!string.IsNullOrEmpty(stack)) sb.Append("\n\n").Append(stack);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- панель инструментов

        private void ToggleFilter(int bit)
        {
            _filterMask ^= 1 << bit;
            PlayerPrefs.SetInt(PrefsMask, _filterMask);
            RefreshFilterButtons();
            Rebuild();
        }

        private void OnSearchChanged(ChangeEvent<string> evt)
        {
            _query = evt.newValue?.Trim() ?? string.Empty;
            Rebuild();
        }

        private void ToggleCollapse()
        {
            LogBuffer log = AppDeck.Log;
            if (log == null) return;

            log.Collapse = !log.Collapse;
            RefreshCollapseButton();
        }

        private void Copy()
        {
            if (_bound == null) return;

            if (_selectedSeq >= 0 && _bound.IsAlive(_selectedSeq))
            {
                GUIUtility.systemCopyBuffer = DetailsOf(_bound, _selectedSeq);
                return;
            }

            var sb = new StringBuilder(_viewCount * 64);
            for (var i = 0; i < _viewCount; i++)
            {
                long seq = SeqAt(i);
                if (!_bound.IsAlive(seq)) continue;

                sb.Append('[').Append(_bound.TypeOf(seq)).Append("] ").Append(_bound.MessageOf(seq));
                int repeat = _bound.RepeatOf(seq);
                if (repeat > 1) sb.Append(" ×").Append(repeat);
                sb.Append('\n');
            }

            GUIUtility.systemCopyBuffer = sb.ToString();
        }

        private void OnSubmitted(string line)
        {
            _list.StickToEnd = true;
            AppDeck.Execute(line, CommandSource.Console);
        }

        private void RefreshFilterButtons()
        {
            for (var i = 0; i < _filterButtons.Length; i++)
                _filterButtons[i].EnableInClassList("appdeck-filter--on", (_filterMask & (1 << i)) != 0);
        }

        private void RefreshCollapseButton()
        {
            LogBuffer log = AppDeck.Log;
            _collapseButton.EnableInClassList("appdeck__icon-button--on", log != null && log.Collapse);
        }

        private void RefreshStickButton() =>
            _stickButton?.EnableInClassList("appdeck__icon-button--on", _list != null && _list.StickToEnd);

        private void RefreshCounts(LogBuffer log)
        {
            for (var i = 0; i < _filterCounts.Length; i++)
            {
                int count = i switch
                {
                    0 => log.CountOf(DeckLogType.Log),
                    1 => log.CountOf(DeckLogType.Warning),
                    2 => log.CountOf(DeckLogType.Error),
                    _ => log.CountOf(DeckLogType.Input) + log.CountOf(DeckLogType.Output),
                };

                if (count == _shownCounts[i]) continue;

                _shownCounts[i] = count;
                _filterCounts[i].SetText(count);
            }
        }
    }
}
