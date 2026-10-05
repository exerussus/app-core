using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Строка консоли из пула виртуального списка. Помнит, что в ней сейчас показано, и трогает
    /// элемент, только если значение действительно другое: перепривязка той же записи — пара сравнений.
    /// </summary>
    internal sealed class LogRow : VisualElement
    {
        private static readonly string[] TypeClasses =
        {
            "appdeck-log--log",
            "appdeck-log--warning",
            "appdeck-log--error",
            "appdeck-log--input",
            "appdeck-log--output",
        };

        // один буфер на все строки: SetText копирует символы, буфер свободен сразу после вызова
        private static readonly char[] TimeBuffer = new char[8];

        private readonly Label _time;
        private readonly Label _repeat;
        private readonly Label _message;

        private int _type = -1;
        private int _shownTime = -1;
        private int _shownRepeat = -1;
        private string _shownMessage;

        public LogRow()
        {
            AddToClassList("appdeck-log");

            var stripe = new VisualElement { pickingMode = PickingMode.Ignore };
            stripe.AddToClassList("appdeck-log__stripe");
            Add(stripe);

            _time = MakeLabel("appdeck-log__time");
            _repeat = MakeLabel("appdeck-log__repeat");
            _message = MakeLabel("appdeck-log__message");

            _repeat.style.display = DisplayStyle.None;
        }

        private Label MakeLabel(string ussClass)
        {
            var label = new Label { pickingMode = PickingMode.Ignore, enableRichText = false, parseEscapeSequences = false };
            label.AddToClassList(ussClass);
            Add(label);
            return label;
        }

        public void Bind(LogBuffer buffer, long seq)
        {
            var type = (int)buffer.TypeOf(seq);
            if (type != _type)
            {
                if (_type >= 0) RemoveFromClassList(TypeClasses[_type]);
                AddToClassList(TypeClasses[type]);
                _type = type;
            }

            int time = buffer.TimeOf(seq);
            if (time != _shownTime)
            {
                _shownTime = time;
                FormatTime(time);
                _time.SetText(TimeBuffer, 0, 8);
            }

            int repeat = buffer.RepeatOf(seq);
            if (repeat != _shownRepeat)
            {
                bool many = repeat > 1;
                if (many != _shownRepeat > 1) _repeat.style.display = many ? DisplayStyle.Flex : DisplayStyle.None;
                if (many) _repeat.SetText(repeat);
                _shownRepeat = repeat;
            }

            string message = buffer.HeadOf(seq, 400);
            if (!ReferenceEquals(message, _shownMessage))
            {
                _shownMessage = message;
                _message.text = message;
            }
        }

        private static void FormatTime(int secondOfDay)
        {
            int h = secondOfDay / 3600 % 24;
            int m = secondOfDay / 60 % 60;
            int s = secondOfDay % 60;

            TimeBuffer[0] = (char)('0' + h / 10);
            TimeBuffer[1] = (char)('0' + h % 10);
            TimeBuffer[2] = ':';
            TimeBuffer[3] = (char)('0' + m / 10);
            TimeBuffer[4] = (char)('0' + m % 10);
            TimeBuffer[5] = ':';
            TimeBuffer[6] = (char)('0' + s / 10);
            TimeBuffer[7] = (char)('0' + s % 10);
        }
    }
}
