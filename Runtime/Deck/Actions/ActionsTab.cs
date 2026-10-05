using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Вкладка Actions: сетка кнопок с поиском, фильтром по тегам и закреплёнными сверху.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Источники кнопок — реестр кнопок (<see cref="AppDeck.AddAction(ActionSpec,object)"/>) и команды
    /// с флагом <see cref="CommandSpec.AsAction"/>. Список записей пересобирается только при изменении
    /// любого из реестров или закреплений; фильтр — массив индексов, пересчёт только при смене
    /// поиска или тегов. Каждый кадр — сравнение версий.
    /// </para>
    /// <para>
    /// Сетка — тот же <see cref="VirtualList"/>: строка списка держит до <see cref="MaxColumns"/> ячеек,
    /// число колонок зависит от ширины. Ячейки из пула, обработчик клика один на вкладку.
    /// </para>
    /// </remarks>
    internal sealed class ActionsTab : DeckTab, IVirtualListAdapter
    {
        public const string TabId = "actions";

        private const int MaxColumns = 12;
        private const float CellMinWidth = 170f;
        private const float RowHeight = 56f;
        private const float FlashSeconds = 0.6f;
        private const float ConfirmSeconds = 3f;
        private const string PrefsPins = "appdeck.actions.pins";

        private struct Entry
        {
            public bool FromCommand;
            public int Slot;
            public string Id;
            public string Title;
            public string Tags;
            public string Search;
            public string Tooltip;
            public ulong TagMask;
            public bool HasForm;
            public bool Confirm;
            public bool Pinned;
        }

        private Entry[] _entries = new Entry[64];
        private int _entryCount;
        private int[] _visible = new int[64];
        private int _visibleCount;

        private int _seenActions = -1;
        private int _seenCommands = -1;
        private int _seenTags = -1;
        private bool _filterDirty = true;

        private string _query = string.Empty;
        private ulong _tagFilter;
        private bool _pinnedOnly;
        private readonly HashSet<string> _pins = new HashSet<string>(StringComparer.Ordinal);

        private VirtualList _grid;
        private TextField _search;
        private VisualElement _chips;
        private Button _pinnedChip;
        private Label _empty;
        private ActionForm _form;
        private readonly List<Button> _tagChips = new List<Button>();
        private int _columns = 1;

        // мигание результата и ожидание подтверждения — по id, ячейки могли перепривязаться
        private string _flashId;
        private bool _flashOk;
        private float _flashUntil;
        private string _armedId;
        private float _armedUntil;
        private float _time;

        public override string Id => TabId;

        public override string Title => "Actions";

        public override int Order => -200;

        protected internal override bool IsTyping =>
            _search != null && (_search.focusController?.focusedElement == _search || (_form != null && _form.IsTyping));

        // ---------------------------------------------------------------- вёрстка

        protected internal override void Build(VisualElement root)
        {
            root.AddToClassList("appdeck-actions");
            LoadPins();

            var toolbar = new VisualElement();
            toolbar.AddToClassList("appdeck-toolbar");
            root.Add(toolbar);

            _search = new TextField { name = "search" };
            _search.AddToClassList("appdeck-search");
            _search.textEdition.placeholder = "Поиск кнопки";
            _search.RegisterValueChangedCallback(e =>
            {
                _query = (e.newValue ?? string.Empty).Trim().ToLowerInvariant();
                _filterDirty = true;
            });
            toolbar.Add(_search);

            _pinnedChip = new Button(() =>
            {
                _pinnedOnly = !_pinnedOnly;
                _pinnedChip.EnableInClassList("appdeck-chip--on", _pinnedOnly);
                _filterDirty = true;
            }) { text = "★", tooltip = "Только закреплённые (ПКМ по кнопке — закрепить)" };
            _pinnedChip.AddToClassList("appdeck-chip");
            toolbar.Add(_pinnedChip);

            _chips = new VisualElement { name = "tags" };
            _chips.AddToClassList("appdeck-actions__chips");
            toolbar.Add(_chips);

            _grid = new VirtualList { name = "grid", RowHeight = RowHeight };
            _grid.AddToClassList("appdeck-actions__grid");
            _grid.SetAdapter(this);
            _grid.StickToEnd = false;
            _grid.Selectable = false;
            _grid.RegisterCallback<GeometryChangedEvent>(OnGridGeometry);
            root.Add(_grid);

            _empty = new Label("Кнопок нет. Регистрируются через AppDeck.AddAction или командой с AsAction.")
            {
                pickingMode = PickingMode.Ignore,
            };
            _empty.AddToClassList("appdeck-empty");
            root.Add(_empty);

            _form = new ActionForm(this);
            root.Add(_form);
        }

        protected internal override void Focus() => _search?.Focus();

        protected internal override bool HandleEscape()
        {
            if (_form == null || !_form.IsShown) return false;

            _form.Hide();
            return true;
        }

        // ---------------------------------------------------------------- кадр

        protected internal override void Tick(float deltaTime)
        {
            _time += deltaTime;

            ActionRegistry actions = AppDeck.Actions;
            CommandRegistry commands = AppDeck.Commands;

            if (actions.Version != _seenActions || commands.Version != _seenCommands)
            {
                _seenActions = actions.Version;
                _seenCommands = commands.Version;
                RebuildEntries(actions, commands);
                _filterDirty = true;
            }

            if (actions.TagTable.Version != _seenTags)
            {
                _seenTags = actions.TagTable.Version;
                RebuildChips(actions.TagTable);
            }

            if (_filterDirty)
            {
                _filterDirty = false;
                ApplyFilter();
            }

            if (_flashId != null && _time >= _flashUntil)
            {
                _flashId = null;
                _grid.Refresh(true);
            }

            if (_armedId != null && _time >= _armedUntil)
            {
                _armedId = null;
                _grid.Refresh(true);
            }

            _form?.Tick();
        }

        // ---------------------------------------------------------------- записи

        private void RebuildEntries(ActionRegistry actions, CommandRegistry commands)
        {
            _entryCount = 0;

            for (var slot = 0; slot < actions.HighWater; slot++)
            {
                ActionSpec spec = actions.Spec(slot);
                if (spec == null) continue;

                ref Entry e = ref NextEntry();
                e.FromCommand = false;
                e.Slot = slot;
                e.Id = spec.Id;
                e.Title = spec.Title;
                e.Tags = spec.Tags;
                e.Search = actions.SearchOf(slot);
                e.Tooltip = spec.Tooltip ?? spec.Line ?? spec.Command;
                e.TagMask = actions.TagsOf(slot);
                e.HasForm = string.IsNullOrWhiteSpace(spec.Line);
                e.Confirm = spec.Confirm;
            }

            int total = commands.SortedCount;
            for (var i = 0; i < total; i++)
            {
                int slot = commands.SortedSlot(i);
                CommandSpec spec = commands.Spec(slot);
                if (!spec.AsAction) continue;

                ref Entry e = ref NextEntry();
                e.FromCommand = true;
                e.Slot = slot;
                e.Id = "cmd:" + spec.Name;
                e.Title = spec.Title ?? spec.Name;
                e.Tags = spec.Tags;
                e.Search = ActionRegistry.SearchKey(e.Title, spec.Name, spec.Tags);
                e.Tooltip = string.IsNullOrEmpty(spec.Summary) ? spec.Signature : spec.Signature + "\n" + spec.Summary;
                e.TagMask = actions.TagTable.MaskOf(spec.Tags);
                e.HasForm = spec.Args.Length > 0;
                e.Confirm = false;
            }

            for (var i = 0; i < _entryCount; i++) _entries[i].Pinned = _pins.Contains(_entries[i].Id);
            SortEntries();
        }

        private ref Entry NextEntry()
        {
            if (_entryCount == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
            _entries[_entryCount] = default;
            return ref _entries[_entryCount++];
        }

        /// <summary>Закреплённые первыми, дальше по заголовку. Вставками: пересборка редкая, без компаратора-замыкания.</summary>
        private void SortEntries()
        {
            for (var i = 1; i < _entryCount; i++)
            {
                Entry item = _entries[i];
                int j = i - 1;

                while (j >= 0 && Compare(_entries[j], item) > 0)
                {
                    _entries[j + 1] = _entries[j];
                    j--;
                }

                _entries[j + 1] = item;
            }

            static int Compare(in Entry a, in Entry b)
            {
                if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ApplyFilter()
        {
            if (_visible.Length < _entryCount) _visible = new int[Math.Max(_entryCount, _visible.Length * 2)];

            _visibleCount = 0;
            for (var i = 0; i < _entryCount; i++)
            {
                ref Entry e = ref _entries[i];

                if (_pinnedOnly && !e.Pinned) continue;
                if ((e.TagMask & _tagFilter) != _tagFilter) continue;
                if (_query.Length > 0 && e.Search.IndexOf(_query, StringComparison.Ordinal) < 0) continue;

                _visible[_visibleCount++] = i;
            }

            _empty.style.display = _entryCount == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateRows();
        }

        private void UpdateRows()
        {
            int rows = (_visibleCount + _columns - 1) / _columns;
            _grid.SetCount(rows);
            _grid.Refresh(true);
        }

        private void OnGridGeometry(GeometryChangedEvent evt)
        {
            int columns = Mathf.Clamp(Mathf.FloorToInt((evt.newRect.width - 12f) / CellMinWidth), 1, MaxColumns);
            if (columns == _columns) return;

            _columns = columns;
            UpdateRows();
        }

        // ---------------------------------------------------------------- теги

        private void RebuildChips(TagTable tags)
        {
            for (int i = _tagChips.Count; i < tags.Count; i++)
            {
                int bit = i;
                var chip = new Button(() => ToggleTag(bit)) { text = tags.NameOf(i) };
                chip.AddToClassList("appdeck-chip");
                _chips.Add(chip);
                _tagChips.Add(chip);
            }
        }

        private void ToggleTag(int bit)
        {
            _tagFilter ^= 1UL << bit;
            _tagChips[bit].EnableInClassList("appdeck-chip--on", (_tagFilter & (1UL << bit)) != 0);
            _filterDirty = true;
        }

        // ---------------------------------------------------------------- ячейки

        public VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("appdeck-actions__row");

            for (var c = 0; c < MaxColumns; c++)
            {
                var cell = new ActionCell();
                cell.RegisterCallback<PointerUpEvent>(OnCellPointerUp);
                row.Add(cell);
            }

            return row;
        }

        public void BindRow(VisualElement row, int index)
        {
            for (var c = 0; c < MaxColumns; c++)
            {
                var cell = (ActionCell)row[c];

                if (c >= _columns)
                {
                    cell.SetUsed(false);
                    continue;
                }

                cell.SetUsed(true);

                int v = index * _columns + c;
                if (v >= _visibleCount)
                {
                    cell.SetEmpty();
                    continue;
                }

                int entry = _visible[v];
                ref Entry e = ref _entries[entry];

                int state = 0;
                if (_flashId != null && e.Id == _flashId) state = _flashOk ? 1 : 2;
                else if (_armedId != null && e.Id == _armedId) state = 3;

                cell.Bind(entry, e.Title, e.Tags, e.Tooltip, e.HasForm, e.Pinned, state);
            }
        }

        private void OnCellPointerUp(PointerUpEvent evt)
        {
            if (!(evt.currentTarget is ActionCell cell) || cell.Entry < 0 || cell.Entry >= _entryCount) return;

            ref Entry e = ref _entries[cell.Entry];

            if (evt.button == 1)
            {
                TogglePin(e.Id);
                evt.StopPropagation();
                return;
            }

            if (evt.button != 0) return;
            evt.StopPropagation();

            if (e.Confirm && _armedId != e.Id)
            {
                _armedId = e.Id;
                _armedUntil = _time + ConfirmSeconds;
                _grid.Refresh(true);
                return;
            }

            _armedId = null;
            Activate(in e);
        }

        private void Activate(in Entry e)
        {
            CommandSpec command;
            string line;

            if (e.FromCommand)
            {
                command = AppDeck.Commands.Spec(e.Slot);
                line = command?.Name;
            }
            else
            {
                ActionSpec spec = AppDeck.Actions.Spec(e.Slot);
                if (spec == null) return;

                line = spec.Line;
                command = string.IsNullOrWhiteSpace(spec.Line) ? AppDeck.FindCommand(spec.Command) : null;

                if (string.IsNullOrWhiteSpace(line) && command == null)
                {
                    AppDeck.Print($"Кнопка «{spec.Title}»: нет команды «{spec.Command}».", DeckLogType.Error);
                    Flash(e.Id, false);
                    return;
                }
            }

            if (command == null && line == null) return;

            if (e.HasForm && command != null)
            {
                _form.Show(command, e.Id, e.Title);
                return;
            }

            Flash(e.Id, AppDeck.Execute(line, CommandSource.Action));
        }

        /// <summary>Форма отправила готовую строку.</summary>
        internal void RunFromForm(string id, string line) => Flash(id, AppDeck.Execute(line, CommandSource.Action));

        private void Flash(string id, bool ok)
        {
            _flashId = id;
            _flashOk = ok;
            _flashUntil = _time + FlashSeconds;
            _grid.Refresh(true);
        }

        // ---------------------------------------------------------------- закрепление

        private void TogglePin(string id)
        {
            if (!_pins.Remove(id)) _pins.Add(id);
            SavePins();

            for (var i = 0; i < _entryCount; i++) _entries[i].Pinned = _pins.Contains(_entries[i].Id);
            SortEntries();
            _filterDirty = true;
        }

        private void LoadPins()
        {
            _pins.Clear();
            string saved = PlayerPrefs.GetString(PrefsPins, string.Empty);
            if (saved.Length == 0) return;

            foreach (string id in saved.Split('\n'))
            {
                if (id.Length > 0) _pins.Add(id);
            }
        }

        private void SavePins() => PlayerPrefs.SetString(PrefsPins, string.Join("\n", _pins));
    }
}
