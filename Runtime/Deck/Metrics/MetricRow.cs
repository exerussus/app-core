using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Строка метрики из пула. Число выводится без создания строки (<c>SetText(float, format)</c>)
    /// и только когда оно изменилось; неизменные подписи не трогаются.
    /// </summary>
    internal sealed class MetricRow : VisualElement
    {
        private readonly Label _name;
        private readonly Label _group;
        private readonly Label _value;
        private readonly Label _unit;
        private readonly Label _rate;
        private readonly Label _rateUnit;
        private readonly Sparkline _spark;

        private int _slot = -1;
        private string _shownName;
        private string _shownText;
        private double _shownValue = double.NaN;
        private double _shownRate = double.NaN;
        private bool _shownPinned;
        private bool _shownTracked;
        private MetricKind _shownKind = (MetricKind)255;

        public MetricRow()
        {
            AddToClassList("appdeck-metric");

            PinButton = new Label("★") { tooltip = "Закрепить в мини-HUD" };
            PinButton.AddToClassList("appdeck-metric__pin");
            Add(PinButton);

            _name = MakeLabel("appdeck-metric__name");
            _group = MakeLabel("appdeck-metric__group");
            _value = MakeLabel("appdeck-metric__value");
            _unit = MakeLabel("appdeck-metric__unit");
            _rate = MakeLabel("appdeck-metric__rate");
            _rateUnit = MakeLabel("appdeck-metric__rate-unit");
            _rateUnit.text = "/s";

            _spark = new Sparkline();
            _spark.AddToClassList("appdeck-metric__spark");
            Add(_spark);
        }

        public Label PinButton { get; }

        public int Slot => _slot;

        private Label MakeLabel(string ussClass)
        {
            var label = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
            label.AddToClassList(ussClass);
            Add(label);
            return label;
        }

        public void Bind(MetricRegistry metrics, int slot)
        {
            bool changedSlot = slot != _slot;
            _slot = slot;

            string name = metrics.NameOf(slot);
            if (!ReferenceEquals(name, _shownName))
            {
                _shownName = name;
                _name.text = name;
                _group.text = metrics.GroupOf(slot);
                _unit.text = metrics.UnitOf(slot) ?? string.Empty;
                _shownValue = double.NaN;
                _shownRate = double.NaN;
                _shownText = null;
            }

            MetricKind kind = metrics.KindOf(slot);
            if (kind != _shownKind)
            {
                _shownKind = kind;
                DisplayStyle counter = kind == MetricKind.Counter ? DisplayStyle.Flex : DisplayStyle.None;
                _rate.style.display = counter;
                _rateUnit.style.display = counter;
            }

            if (kind == MetricKind.Text)
            {
                string text = metrics.TextOf(slot);
                if (!ReferenceEquals(text, _shownText))
                {
                    _shownText = text;
                    _value.text = text ?? "—";
                }
            }
            else
            {
                double value = metrics.ValueOf(slot);
                if (!value.Equals(_shownValue))
                {
                    _shownValue = value;
                    _value.SetText((float)value, metrics.FormatOf(slot));
                }

                if (kind == MetricKind.Counter)
                {
                    double rate = metrics.RateOf(slot);
                    if (!rate.Equals(_shownRate))
                    {
                        _shownRate = rate;
                        _rate.SetText((float)rate, "F1");
                    }
                }
            }

            bool pinned = MetricPins.IsPinned(name);
            if (pinned != _shownPinned || changedSlot)
            {
                _shownPinned = pinned;
                PinButton.EnableInClassList("appdeck-metric__pin--on", pinned);
            }

            bool tracked = metrics.IsTracked(slot);
            if (tracked != _shownTracked || changedSlot)
            {
                _shownTracked = tracked;
                _spark.style.visibility = tracked ? Visibility.Visible : Visibility.Hidden;
            }

            if (tracked)
            {
                float[] history = metrics.HistoryOf(slot, out int oldest, out int count);
                _spark.SetSource(history, oldest, count);
            }
        }
    }
}
