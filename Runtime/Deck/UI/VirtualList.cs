using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>Поставщик строк для <see cref="VirtualList"/>. Без делегатов: одна реализация — ноль замыканий.</summary>
    public interface IVirtualListAdapter
    {
        /// <summary>Создать строку. Зовётся только когда пулу не хватает строк (рост высоты окна).</summary>
        VisualElement MakeRow();

        /// <summary>Наполнить строку данными элемента. Зовётся, только когда в строку попал другой элемент или данные изменились.</summary>
        void BindRow(VisualElement row, int index);
    }

    /// <summary>
    /// Виртуальный список с фиксированной высотой строки: строк в дереве ровно на видимую высоту + 1,
    /// сколько бы ни было элементов.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Строки стоят на постоянных местах внутри контейнера; прокрутка внутри строки — один
    /// <c>translate</c> контейнера (без прохода раскладки), а смена первой видимой строки —
    /// перепривязка данных. Строки не создаются и не удаляются при прокрутке.
    /// </para>
    /// <para>
    /// Обработчики событий — по одному методу на список, строка знает свой номер в пуле:
    /// никаких замыканий на строку. Видимость лишних строк — <c>visibility</c> (только перерисовка).
    /// </para>
    /// </remarks>
    [UxmlElement]
    public partial class VirtualList : VisualElement
    {
        public const string UssClass = "appdeck-vlist";
        public const string ViewportUssClass = "appdeck-vlist__viewport";
        public const string RowUssClass = "appdeck-vlist__row";
        public const string SelectedUssClass = "appdeck-vlist__row--selected";
        public const string ScrollerUssClass = "appdeck-vlist__scroller";

        private readonly VisualElement _viewport;
        private readonly VisualElement _content;
        private readonly Scroller _scroller;
        private readonly List<VisualElement> _rows = new List<VisualElement>();
        private int[] _bound = new int[0];

        private IVirtualListAdapter _adapter;
        private float _rowHeight = 20f;
        private int _count;
        private float _offset;
        private float _viewportHeight;
        private int _first = -1;
        private float _appliedFrac = float.NaN;
        private int _visibleRows;
        private int _selected = -1;
        private bool _stickToEnd = true;
        private bool _selectable = true;

        public VirtualList()
        {
            AddToClassList(UssClass);
            style.flexDirection = FlexDirection.Row;
            style.flexGrow = 1;

            _viewport = new VisualElement { name = "viewport" };
            _viewport.AddToClassList(ViewportUssClass);
            _viewport.style.flexGrow = 1;
            _viewport.style.overflow = Overflow.Hidden;
            Add(_viewport);

            _content = new VisualElement { name = "content", pickingMode = PickingMode.Ignore };
            _content.style.position = Position.Absolute;
            _content.style.left = 0;
            _content.style.right = 0;
            _content.style.top = 0;
            _content.style.bottom = 0;
            _viewport.Add(_content);

            _scroller = new Scroller(0f, 1f, OnScrollerChanged, SliderDirection.Vertical);
            _scroller.AddToClassList(ScrollerUssClass);
            Add(_scroller);

            _viewport.RegisterCallback<GeometryChangedEvent>(OnViewportGeometry);
            RegisterCallback<WheelEvent>(OnWheel);
        }

        /// <summary>Высота строки в точках. Все строки одной высоты — на этом держится виртуализация.</summary>
        [UxmlAttribute]
        public float RowHeight
        {
            get => _rowHeight;
            set
            {
                value = Mathf.Max(4f, value);
                if (Mathf.Approximately(_rowHeight, value)) return;

                _rowHeight = value;
                for (var i = 0; i < _rows.Count; i++) PlaceRow(_rows[i], i);
                Relayout(true);
            }
        }

        public int Count => _count;

        /// <summary>Индекс первого видимого элемента.</summary>
        public int FirstVisible => _rowHeight > 0f ? (int)(_offset / _rowHeight) : 0;

        /// <summary>Сколько элементов помещается на экране (с частично видимыми).</summary>
        public int VisibleCount => Mathf.Min(_visibleRows, Mathf.Max(0, _count - FirstVisible));

        /// <summary>Выбирать строку кликом (консоль — да; сетка кнопок и метрики — нет: там клик — действие).</summary>
        public bool Selectable
        {
            get => _selectable;
            set
            {
                _selectable = value;
                if (!value) SetSelectedSilent(-1);
            }
        }

        public int SelectedIndex => _selected;

        /// <summary>Держать прокрутку у конца, когда добавляются элементы. Снимается сам, когда пользователь листает вверх.</summary>
        public bool StickToEnd
        {
            get => _stickToEnd;
            set
            {
                if (_stickToEnd == value) return;

                _stickToEnd = value;
                if (value) ScrollToEnd();
                StickChanged?.Invoke(value);
            }
        }

        public event Action<int> SelectionChanged;

        /// <summary>Прилипание к концу изменилось (пользователь пролистал вверх или вернулся в конец).</summary>
        public event Action<bool> StickChanged;

        public void SetAdapter(IVirtualListAdapter adapter)
        {
            _adapter = adapter;

            for (var i = 0; i < _rows.Count; i++) _rows[i].RemoveFromHierarchy();
            _rows.Clear();
            _bound = new int[0];

            Relayout(true);
        }

        /// <summary>Новое число элементов. Позиция сохраняется (или прилипает к концу).</summary>
        public void SetCount(int count)
        {
            count = Mathf.Max(0, count);

            // без события: владелец списка сам решает, что делать с выбором (он знает, какой элемент это был)
            if (_selected >= count) SetSelectedSilent(-1);

            _count = count;
            _offset = _stickToEnd ? MaxOffset : Mathf.Clamp(_offset, 0f, MaxOffset);
            UpdateScroller();
            Refresh(false);
        }

        /// <summary>Перепривязать видимые строки. <paramref name="force"/> — даже те, в которых тот же элемент (данные поменялись).</summary>
        public void Refresh(bool force)
        {
            if (_adapter == null || _rows.Count == 0) return;

            int first = _rowHeight > 0f ? (int)(_offset / _rowHeight) : 0;
            float frac = _offset - first * _rowHeight;

            if (!Mathf.Approximately(frac, _appliedFrac))
            {
                _appliedFrac = frac;
                _content.style.translate = new Translate(0f, -frac);
            }

            bool shifted = first != _first;
            _first = first;

            for (var r = 0; r < _rows.Count; r++)
            {
                VisualElement row = _rows[r];
                int index = first + r;

                // строки пула за пределами текущей высоты окна (окно уменьшили) не привязываются
                if (index >= _count || r >= _visibleRows)
                {
                    if (_bound[r] != -1)
                    {
                        _bound[r] = -1;
                        row.style.visibility = Visibility.Hidden;
                        row.EnableInClassList(SelectedUssClass, false);
                    }

                    continue;
                }

                if (_bound[r] == -1) row.style.visibility = Visibility.Visible;

                if (force || shifted || _bound[r] != index)
                {
                    _bound[r] = index;
                    _adapter.BindRow(row, index);
                    row.EnableInClassList(SelectedUssClass, index == _selected);
                }
            }
        }

        public void ScrollToEnd()
        {
            _offset = MaxOffset;
            SyncScroller();
            Refresh(false);
        }

        /// <summary>Прокрутить так, чтобы элемент был виден.</summary>
        public void ScrollTo(int index)
        {
            if (index < 0 || index >= _count) return;

            float top = index * _rowHeight;
            float bottom = top + _rowHeight;

            if (top < _offset) _offset = top;
            else if (bottom > _offset + _viewportHeight) _offset = bottom - _viewportHeight;

            _offset = Mathf.Clamp(_offset, 0f, MaxOffset);
            UpdateStick();
            SyncScroller();
            Refresh(false);
        }

        public void SetSelected(int index)
        {
            if (index == _selected) return;

            SetSelectedSilent(index);
            SelectionChanged?.Invoke(index);
        }

        /// <summary>Сменить выбор без события (пересборка представления, сдвиг элементов).</summary>
        public void SetSelectedSilent(int index)
        {
            if (index == _selected) return;

            _selected = index;
            for (var r = 0; r < _rows.Count; r++) _rows[r].EnableInClassList(SelectedUssClass, _bound[r] >= 0 && _bound[r] == index);
        }

        /// <summary>
        /// Из начала списка ушло <paramref name="removed"/> элементов (кольцо логов вытеснило старые).
        /// Если пользователь читает середину, сдвигаем прокрутку на столько же — текст не уезжает из-под глаз.
        /// </summary>
        public void ShiftForRemoved(int removed)
        {
            if (removed <= 0 || _stickToEnd) return;

            _offset = Mathf.Max(0f, _offset - removed * _rowHeight);
            SyncScroller();
        }

        // ---------------------------------------------------------------- внутреннее

        private float MaxOffset => Mathf.Max(0f, _count * _rowHeight - _viewportHeight);

        private void OnViewportGeometry(GeometryChangedEvent evt)
        {
            float height = evt.newRect.height;
            if (Mathf.Approximately(height, _viewportHeight) && _rows.Count > 0) return;

            _viewportHeight = height;
            Relayout(false);
        }

        private void Relayout(bool force)
        {
            if (_adapter == null) return;

            int needed = _viewportHeight > 0f ? Mathf.CeilToInt(_viewportHeight / _rowHeight) + 1 : 0;
            _visibleRows = needed;

            // пул только растёт: окно уменьшили — лишние строки просто скрыты, при росте не пересоздаём
            if (needed > _rows.Count)
            {
                int old = _rows.Count;
                Array.Resize(ref _bound, needed);

                for (int r = old; r < needed; r++)
                {
                    VisualElement row = _adapter.MakeRow();
                    row.AddToClassList(RowUssClass);
                    row.userData = r;
                    row.RegisterCallback<PointerDownEvent>(OnRowPointerDown);
                    PlaceRow(row, r);
                    row.style.visibility = Visibility.Hidden;
                    _bound[r] = -1;

                    _content.Add(row);
                    _rows.Add(row);
                }
            }

            _offset = _stickToEnd ? MaxOffset : Mathf.Clamp(_offset, 0f, MaxOffset);
            UpdateScroller();
            Refresh(force);
        }

        private void PlaceRow(VisualElement row, int r)
        {
            row.style.position = Position.Absolute;
            row.style.left = 0;
            row.style.right = 0;
            row.style.top = r * _rowHeight;
            row.style.height = _rowHeight;
        }

        private void UpdateScroller()
        {
            float max = MaxOffset;
            _scroller.style.display = max > 0.5f ? DisplayStyle.Flex : DisplayStyle.None;

            _scroller.highValue = Mathf.Max(max, 0.0001f);
            float total = _count * _rowHeight;
            _scroller.Adjust(total > 0f ? Mathf.Clamp01(_viewportHeight / total) : 1f);

            SyncScroller();
        }

        /// <summary>
        /// Ползунок — за прокруткой, без события: событие ползунка ставится в очередь диспетчера и пришло бы
        /// позже любого флага-сторожа, вернув прокрутку назад.
        /// </summary>
        private void SyncScroller() => _scroller.slider.SetValueWithoutNotify(_offset);

        private void OnScrollerChanged(float value)
        {
            // эхо нашей же синхронизации (или зажатие при смене максимума) — не действие пользователя
            if (Mathf.Abs(value - _offset) < 0.5f) return;

            _offset = Mathf.Clamp(value, 0f, MaxOffset);
            UpdateStick();
            Refresh(false);
        }

        private void OnWheel(WheelEvent evt)
        {
            if (MaxOffset <= 0f) return;

            _offset = Mathf.Clamp(_offset + evt.delta.y * _rowHeight * 3f, 0f, MaxOffset);
            UpdateStick();
            SyncScroller();
            Refresh(false);
            evt.StopPropagation();
        }

        private void UpdateStick()
        {
            bool atEnd = _offset >= MaxOffset - 0.5f;
            if (atEnd == _stickToEnd) return;

            _stickToEnd = atEnd;
            StickChanged?.Invoke(atEnd);
        }

        private void OnRowPointerDown(PointerDownEvent evt)
        {
            if (!_selectable || evt.button != 0 || !(evt.currentTarget is VisualElement row) || !(row.userData is int r)) return;
            if (r < 0 || r >= _bound.Length || _bound[r] < 0) return;

            SetSelected(_bound[r] == _selected ? -1 : _bound[r]);
        }
    }
}
