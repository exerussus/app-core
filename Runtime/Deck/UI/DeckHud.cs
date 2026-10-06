using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Мини-HUD: закреплённые метрики строкой в углу экрана, пока окно AppDeck закрыто.
    /// </summary>
    /// <remarks>
    /// Опрашивает только закреплённые метрики и только с частотой <see cref="DeckSettings.HudRate"/>.
    /// Числа выводятся без строк, неизменившиеся значения не трогаются. Не ловит указатель.
    /// Список закреплённых пересобирается только при изменении закреплений или реестра метрик.
    /// </remarks>
    internal sealed class DeckHud : VisualElement
    {
        private const int MaxRows = 12;

        private readonly bool _enabled;
        private readonly float _interval;
        private readonly HudRow[] _rows = new HudRow[MaxRows];
        private readonly int[] _slots = new int[MaxRows];
        private int _count;

        private int _seenPins = -1;
        private int _seenMetrics = -1;
        private bool _shown;
        private float _time;
        private float _next;

        public DeckHud(DeckSettings settings)
        {
            name = "appDeckHud";
            AddToClassList("appdeck-hud");
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.display = DisplayStyle.None;

            _enabled = settings == null || settings.Hud;
            _interval = 1f / Mathf.Max(1, settings != null ? settings.HudRate : 4);

            DeckCorner corner = settings != null ? settings.HudCorner : DeckCorner.TopLeft;
            _left = corner == DeckCorner.TopLeft || corner == DeckCorner.BottomLeft;
            _top = corner == DeckCorner.TopLeft || corner == DeckCorner.TopRight;
            ApplySafeArea(0f, 0f, 0f, 0f);
            if (!_left) AddToClassList("appdeck-hud--right");

            for (var i = 0; i < MaxRows; i++)
            {
                _rows[i] = new HudRow();
                _rows[i].style.display = DisplayStyle.None;
                Add(_rows[i]);
            }
        }

        private const float Margin = 6f;
        private readonly bool _left;
        private readonly bool _top;

        /// <summary>Угол мини-HUD — внутри безопасной зоны: отступ своей стороны + поле.</summary>
        public void ApplySafeArea(float left, float right, float top, float bottom)
        {
            if (_left) style.left = Margin + left;
            else style.right = Margin + right;

            if (_top) style.top = Margin + top;
            else style.bottom = Margin + bottom;
        }

        public void Tick(float deltaTime, bool deckOpen)
        {
            if (!_enabled) return;

            _time += deltaTime;
            MetricRegistry metrics = AppDeck.Metrics;

            if (MetricPins.Version != _seenPins || metrics.Version != _seenMetrics)
            {
                _seenPins = MetricPins.Version;
                _seenMetrics = metrics.Version;
                Collect(metrics);
                _next = 0f;
            }

            bool show = !deckOpen && _count > 0;
            if (show != _shown)
            {
                _shown = show;
                style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                _next = 0f;
            }

            if (!show || _time < _next) return;
            _next = _time + _interval;

            for (var i = 0; i < _count; i++)
            {
                metrics.Sample(_slots[i], _interval * 0.5f);
                _rows[i].Bind(metrics, _slots[i]);
            }
        }

        private void Collect(MetricRegistry metrics)
        {
            _count = 0;
            for (var slot = 0; slot < metrics.HighWater && _count < MaxRows; slot++)
            {
                if (metrics.IsAliveSlot(slot) && MetricPins.IsPinned(metrics.NameOf(slot))) _slots[_count++] = slot;
            }

            for (var i = 0; i < MaxRows; i++) _rows[i].style.display = i < _count ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private sealed class HudRow : VisualElement
        {
            private readonly Label _name;
            private readonly Label _value;
            private readonly Label _unit;
            private string _shownName;
            private string _shownText;
            private double _shownValue = double.NaN;

            public HudRow()
            {
                AddToClassList("appdeck-hud__row");
                pickingMode = PickingMode.Ignore;

                _name = Make("appdeck-hud__name");
                _value = Make("appdeck-hud__value");
                _unit = Make("appdeck-hud__unit");
            }

            private Label Make(string ussClass)
            {
                var label = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
                label.AddToClassList(ussClass);
                Add(label);
                return label;
            }

            public void Bind(MetricRegistry metrics, int slot)
            {
                string name = metrics.NameOf(slot);
                if (!ReferenceEquals(name, _shownName))
                {
                    _shownName = name;
                    _name.text = name;
                    _unit.text = metrics.UnitOf(slot) ?? string.Empty;
                    _shownValue = double.NaN;
                    _shownText = null;
                }

                if (metrics.KindOf(slot) == MetricKind.Text)
                {
                    string text = metrics.TextOf(slot);
                    if (ReferenceEquals(text, _shownText)) return;

                    _shownText = text;
                    _value.text = text ?? "—";
                    return;
                }

                double value = metrics.KindOf(slot) == MetricKind.Counter ? metrics.RateOf(slot) : metrics.ValueOf(slot);
                if (value.Equals(_shownValue)) return;

                _shownValue = value;
                _value.SetText((float)value, metrics.FormatOf(slot));
            }
        }
    }
}
