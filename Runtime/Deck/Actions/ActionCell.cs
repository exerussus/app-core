using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Ячейка кнопки из пула. Помнит показанное и трогает элементы только при изменении.
    /// Состояние: 0 — обычное, 1 — успех (мигание), 2 — ошибка, 3 — ждёт подтверждения.
    /// </summary>
    internal sealed class ActionCell : VisualElement
    {
        private static readonly string[] StateClasses =
        {
            null,
            "appdeck-action--ok",
            "appdeck-action--fail",
            "appdeck-action--armed",
        };

        private readonly Label _title;
        private readonly Label _tags;
        private readonly Label _pin;
        private readonly Label _form;

        private bool _used = true;
        private bool _filled = true;
        private string _shownTitle;
        private string _shownTags;
        private string _shownTooltip;
        private int _state;
        private bool _shownPinned;
        private bool _shownForm;

        public ActionCell()
        {
            AddToClassList("appdeck-action");

            _title = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
            _title.AddToClassList("appdeck-action__title");
            Add(_title);

            _tags = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
            _tags.AddToClassList("appdeck-action__tags");
            Add(_tags);

            _pin = new Label("★") { pickingMode = PickingMode.Ignore };
            _pin.AddToClassList("appdeck-action__pin");
            _pin.style.display = DisplayStyle.None;
            Add(_pin);

            _form = new Label("…") { pickingMode = PickingMode.Ignore, tooltip = "С аргументами — откроется форма" };
            _form.AddToClassList("appdeck-action__form");
            _form.style.display = DisplayStyle.None;
            Add(_form);
        }

        /// <summary>Индекс записи во вкладке; -1 — пустая ячейка.</summary>
        public int Entry { get; private set; } = -1;

        /// <summary>Ячейка за пределами числа колонок — убрать из раскладки.</summary>
        public void SetUsed(bool used)
        {
            if (used == _used) return;

            _used = used;
            style.display = used ? DisplayStyle.Flex : DisplayStyle.None;
            if (!used) Entry = -1;
        }

        /// <summary>Хвост последней строки: место держим, ячейку не показываем.</summary>
        public void SetEmpty()
        {
            Entry = -1;
            if (!_filled) return;

            _filled = false;
            style.visibility = Visibility.Hidden;
        }

        public void Bind(int entry, string title, string tags, string tooltip, bool hasForm, bool pinned, int state)
        {
            Entry = entry;

            if (!_filled)
            {
                _filled = true;
                style.visibility = Visibility.Visible;
            }

            if (!ReferenceEquals(title, _shownTitle))
            {
                _shownTitle = title;
                _title.text = title;
            }

            if (!ReferenceEquals(tags, _shownTags))
            {
                _shownTags = tags;
                _tags.text = tags ?? string.Empty;
            }

            if (!ReferenceEquals(tooltip, _shownTooltip))
            {
                _shownTooltip = tooltip;
                this.tooltip = tooltip;
            }

            if (pinned != _shownPinned)
            {
                _shownPinned = pinned;
                _pin.style.display = pinned ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (hasForm != _shownForm)
            {
                _shownForm = hasForm;
                _form.style.display = hasForm ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (state != _state)
            {
                if (StateClasses[_state] != null) RemoveFromClassList(StateClasses[_state]);
                if (StateClasses[state] != null) AddToClassList(StateClasses[state]);
                _state = state;
            }
        }
    }
}
