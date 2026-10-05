using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Вкладка Metrics: список метрик с поиском, фильтром по группам, закреплением в мини-HUD
    /// и спарклайнами треков.
    /// </summary>
    /// <remarks>
    /// Опрос — с частотой <see cref="DeckSettings.MetricsRate"/> и только тех метрик, чьи строки
    /// сейчас на экране, плюс треков (чтобы история шла непрерывно, пока вкладка открыта).
    /// Значение в строке переписывается только если оно изменилось; числа выводятся без строк
    /// (<c>SetText(float, format)</c>).
    /// </remarks>
    internal sealed class MetricsTab : DeckTab, IVirtualListAdapter
    {
        public const string TabId = "metrics";

        private readonly float _interval;

        private int[] _sorted = new int[32];
        private int _sortedCount;
        private int[] _visible = new int[32];
        private int _visibleCount;

        private int _seenVersion = -1;
        private int _seenGroups = -1;
        private int _seenPins = -1;
        private bool _filterDirty = true;
        private string _query = string.Empty;
        private ulong _groupFilter;

        private VirtualList _list;
        private TextField _search;
        private VisualElement _chips;
        private Label _empty;
        private readonly List<Button> _groupChips = new List<Button>();

        private float _time;
        private float _nextSample;

        public MetricsTab(DeckSettings settings)
        {
            int rate = settings != null ? settings.MetricsRate : 8;
            _interval = 1f / Mathf.Max(1, rate);
        }

        public override string Id => TabId;

        public override string Title => "Metrics";

        public override int Order => -100;

        protected internal override bool IsTyping => _search != null && _search.focusController?.focusedElement == _search;

        protected internal override void Build(VisualElement root)
        {
            root.AddToClassList("appdeck-metrics");

            var toolbar = new VisualElement();
            toolbar.AddToClassList("appdeck-toolbar");
            root.Add(toolbar);

            _search = new TextField { name = "search" };
            _search.AddToClassList("appdeck-search");
            _search.textEdition.placeholder = "Поиск метрики";
            _search.RegisterValueChangedCallback(e =>
            {
                _query = (e.newValue ?? string.Empty).Trim().ToLowerInvariant();
                _filterDirty = true;
            });
            toolbar.Add(_search);

            _chips = new VisualElement();
            _chips.AddToClassList("appdeck-actions__chips");
            toolbar.Add(_chips);

            _list = new VirtualList { name = "metrics", RowHeight = 28f };
            _list.AddToClassList("appdeck-metrics__list");
            _list.SetAdapter(this);
            _list.StickToEnd = false;
            _list.Selectable = false;
            root.Add(_list);

            _empty = new Label("Метрик нет. Регистрируются через AppDeck.AddGauge / AddCounter / AddText.") { pickingMode = PickingMode.Ignore };
            _empty.AddToClassList("appdeck-empty");
            root.Add(_empty);
        }

        protected internal override void Focus() => _search?.Focus();

        protected internal override void OnShow() => _nextSample = 0f;

        protected internal override void Tick(float deltaTime)
        {
            _time += deltaTime;
            MetricRegistry metrics = AppDeck.Metrics;

            if (metrics.Version != _seenVersion)
            {
                _seenVersion = metrics.Version;
                RebuildSorted(metrics);
                _filterDirty = true;
            }

            if (metrics.Groups.Version != _seenGroups)
            {
                _seenGroups = metrics.Groups.Version;
                RebuildChips(metrics.Groups);
            }

            if (_filterDirty)
            {
                _filterDirty = false;
                ApplyFilter(metrics);
            }

            bool pinsChanged = MetricPins.Version != _seenPins;
            _seenPins = MetricPins.Version;

            if (_time < _nextSample && !pinsChanged) return;
            _nextSample = _time + _interval;

            // треки — все (непрерывная история), остальные — только видимые строки
            for (var i = 0; i < _sortedCount; i++)
            {
                int slot = _sorted[i];
                if (metrics.IsTracked(slot)) metrics.Sample(slot, _interval * 0.5f);
            }

            int first = _list.FirstVisible;
            int last = Mathf.Min(_visibleCount, first + _list.VisibleCount);
            for (int i = first; i < last; i++) metrics.Sample(_visible[i], _interval * 0.5f);

            _list.Refresh(true);
        }

        private void RebuildSorted(MetricRegistry metrics)
        {
            if (_sorted.Length < metrics.HighWater) _sorted = new int[Math.Max(metrics.HighWater, _sorted.Length * 2)];

            _sortedCount = 0;
            for (var slot = 0; slot < metrics.HighWater; slot++)
            {
                if (metrics.IsAliveSlot(slot)) _sorted[_sortedCount++] = slot;
            }

            // группа, потом имя; вставками — пересборка редкая
            for (var i = 1; i < _sortedCount; i++)
            {
                int slot = _sorted[i];
                int j = i - 1;

                while (j >= 0 && Compare(metrics, _sorted[j], slot) > 0)
                {
                    _sorted[j + 1] = _sorted[j];
                    j--;
                }

                _sorted[j + 1] = slot;
            }

            static int Compare(MetricRegistry m, int a, int b)
            {
                int g = string.Compare(m.GroupOf(a), m.GroupOf(b), StringComparison.OrdinalIgnoreCase);
                return g != 0 ? g : string.Compare(m.NameOf(a), m.NameOf(b), StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ApplyFilter(MetricRegistry metrics)
        {
            if (_visible.Length < _sortedCount) _visible = new int[Math.Max(_sortedCount, _visible.Length * 2)];

            _visibleCount = 0;
            for (var i = 0; i < _sortedCount; i++)
            {
                int slot = _sorted[i];

                if (_groupFilter != 0 && (metrics.GroupMaskOf(slot) & _groupFilter) == 0) continue;
                if (_query.Length > 0 && metrics.SearchOf(slot).IndexOf(_query, StringComparison.Ordinal) < 0) continue;

                _visible[_visibleCount++] = slot;
            }

            _empty.style.display = _sortedCount == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _list.SetCount(_visibleCount);
            _nextSample = 0f;
        }

        private void RebuildChips(TagTable groups)
        {
            for (int i = _groupChips.Count; i < groups.Count; i++)
            {
                int bit = i;
                Button chip = null;
                chip = new Button(() =>
                {
                    // группы — «или»: показать выбранные группы
                    _groupFilter ^= 1UL << bit;
                    chip.EnableInClassList("appdeck-chip--on", (_groupFilter & (1UL << bit)) != 0);
                    _filterDirty = true;
                }) { text = groups.NameOf(i) };
                chip.AddToClassList("appdeck-chip");
                _chips.Add(chip);
                _groupChips.Add(chip);
            }
        }

        public VisualElement MakeRow()
        {
            var row = new MetricRow();
            row.PinButton.RegisterCallback<ClickEvent>(OnPinClick);
            return row;
        }

        public void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _visibleCount) return;
            ((MetricRow)row).Bind(AppDeck.Metrics, _visible[index]);
        }

        private void OnPinClick(ClickEvent evt)
        {
            if (!(evt.currentTarget is VisualElement button) || !(button.parent is MetricRow row) || row.Slot < 0) return;

            string name = AppDeck.Metrics.NameOf(row.Slot);
            if (name == null) return;

            MetricPins.Set(name, !MetricPins.IsPinned(name));
            _list.Refresh(true);
            evt.StopPropagation();
        }
    }
}
