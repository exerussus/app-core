using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Форма аргументов кнопки. Поля строятся по той же сигнатуре команды, что и автоподстановка:
    /// числа и строки — поле ввода, bool — переключатель, варианты — выпадающий список, источник
    /// подсказок — поле с меню значений, цель — текущий выбор. Итог — обычная строка команды.
    /// </summary>
    /// <remarks>
    /// Строится при открытии (действие пользователя, не горячий путь). Последние введённые значения
    /// запоминаются на команду — повторный вызов с теми же аргументами в один клик.
    /// </remarks>
    internal sealed class ActionForm : VisualElement
    {
        private sealed class Binding
        {
            public ArgSpec Arg;
            public TextField Text;
            public Toggle Toggle;
            public DropdownField Choice;
            public Label TargetLabel;
        }

        private readonly ActionsTab _owner;
        private readonly Label _title;
        private readonly Label _signature;
        private readonly ScrollView _fields;
        private readonly List<Binding> _bindings = new List<Binding>();
        private readonly Dictionary<string, string[]> _lastValues = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _collect = new List<string>();

        private CommandSpec _spec;
        private string _id;
        private object _shownTarget;

        public ActionForm(ActionsTab owner)
        {
            _owner = owner;

            AddToClassList("appdeck-form");
            style.position = Position.Absolute;
            style.display = DisplayStyle.None;

            var header = new VisualElement();
            header.AddToClassList("appdeck-form__header");
            Add(header);

            _title = new Label { enableRichText = false };
            _title.AddToClassList("appdeck-form__title");
            header.Add(_title);

            var close = new Button(Hide) { text = "✕" };
            close.AddToClassList("appdeck__icon-button");
            header.Add(close);

            _signature = new Label { enableRichText = false };
            _signature.AddToClassList("appdeck-form__signature");
            Add(_signature);

            _fields = new ScrollView(ScrollViewMode.Vertical);
            _fields.AddToClassList("appdeck-form__fields");
            Add(_fields);

            var footer = new VisualElement();
            footer.AddToClassList("appdeck-form__footer");
            Add(footer);

            var run = new Button(Run) { text = "Выполнить" };
            run.AddToClassList("appdeck-form__run");
            footer.Add(run);

            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
        }

        public bool IsShown => style.display == DisplayStyle.Flex;

        public bool IsTyping
        {
            get
            {
                if (!IsShown) return false;
                Focusable focused = focusController?.focusedElement;
                return focused is VisualElement element && Contains(element);
            }
        }

        public void Show(CommandSpec spec, string id, string title)
        {
            _spec = spec;
            _id = id;
            _title.text = title;
            _signature.text = string.IsNullOrEmpty(spec.Summary) ? spec.Signature : spec.Signature + "\n" + spec.Summary;

            _fields.Clear();
            _bindings.Clear();
            _lastValues.TryGetValue(spec.Name, out string[] last);

            for (var i = 0; i < spec.Args.Length; i++)
            {
                string remembered = last != null && i < last.Length ? last[i] : null;
                _fields.Add(BuildField(spec.Args[i], remembered));
            }

            style.display = DisplayStyle.Flex;
            _shownTarget = null;
            Tick();

            // фокус на первое текстовое поле — можно сразу печатать
            for (var i = 0; i < _bindings.Count; i++)
            {
                if (_bindings[i].Text == null) continue;
                _bindings[i].Text.Focus();
                break;
            }
        }

        public void Hide() => style.display = DisplayStyle.None;

        /// <summary>Подпись цели меняется вместе с выбором в мире.</summary>
        public void Tick()
        {
            if (!IsShown) return;

            DeckTarget target = AppDeck.Target;
            if (ReferenceEquals(target.Value, _shownTarget)) return;
            _shownTarget = target.Value;

            for (var i = 0; i < _bindings.Count; i++)
            {
                if (_bindings[i].TargetLabel == null) continue;
                _bindings[i].TargetLabel.text = target.IsValid ? target.Label : "— цель не выбрана —";
            }
        }

        private VisualElement BuildField(ArgSpec arg, string remembered)
        {
            var binding = new Binding { Arg = arg };
            _bindings.Add(binding);

            var row = new VisualElement();
            row.AddToClassList("appdeck-form__row");

            var label = new Label(arg.Optional ? arg.Name : arg.Name + " *") { tooltip = arg.Description };
            label.AddToClassList("appdeck-form__label");
            row.Add(label);

            string initial = remembered ?? arg.DefaultText ?? string.Empty;

            switch (arg.Kind)
            {
                case ArgKind.Bool:
                    bool on = CommandRegistry.TryParseBool(initial.AsSpan(), out bool parsed) && parsed;
                    binding.Toggle = new Toggle { value = on };
                    binding.Toggle.AddToClassList("appdeck-form__toggle");
                    row.Add(binding.Toggle);
                    break;

                case ArgKind.Choice:
                    var choices = new List<string>(arg.Choices);
                    if (arg.Optional) choices.Insert(0, string.Empty);
                    int index = Math.Max(0, choices.FindIndex(c => string.Equals(c, initial, StringComparison.OrdinalIgnoreCase)));
                    binding.Choice = new DropdownField(choices, index);
                    binding.Choice.AddToClassList("appdeck-form__choice");
                    row.Add(binding.Choice);
                    break;

                case ArgKind.Target:
                    binding.TargetLabel = new Label();
                    binding.TargetLabel.AddToClassList("appdeck-form__target");
                    row.Add(binding.TargetLabel);
                    break;

                default:
                    binding.Text = new TextField { value = initial };
                    binding.Text.AddToClassList("appdeck-form__text");
                    binding.Text.style.flexGrow = 1;
                    row.Add(binding.Text);

                    if (arg.Source != null)
                    {
                        TextField field = binding.Text;
                        Button pick = null;
                        pick = new Button(() => OpenSourceMenu(arg.Source, field, pick)) { text = "▾", tooltip = "Значения" };
                        pick.AddToClassList("appdeck__icon-button");
                        row.Add(pick);
                    }

                    break;
            }

            if (!string.IsNullOrEmpty(arg.Description))
            {
                var about = new Label(arg.Description) { enableRichText = false };
                about.AddToClassList("appdeck-form__about");
                var wrap = new VisualElement();
                wrap.Add(row);
                wrap.Add(about);
                return wrap;
            }

            return row;
        }

        private void OpenSourceMenu(string source, TextField field, VisualElement anchor)
        {
            string prefix = field.value ?? string.Empty;
            AppDeck.Suggestions.Collect(source, prefix.AsSpan(), _collect, 40);
            if (_collect.Count == 0 && prefix.Length > 0) AppDeck.Suggestions.Collect(source, ReadOnlySpan<char>.Empty, _collect, 40);

            var menu = new GenericDropdownMenu();
            if (_collect.Count == 0) menu.AddDisabledItem("Нет значений", false);

            for (var i = 0; i < _collect.Count; i++)
            {
                string value = _collect[i];
                menu.AddItem(value, string.Equals(value, field.value, StringComparison.OrdinalIgnoreCase), () => field.value = value);
            }

            _collect.Clear();
            menu.DropDown(anchor.worldBound, anchor, DropdownMenuSizeMode.Auto);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != UnityEngine.KeyCode.Return && evt.keyCode != UnityEngine.KeyCode.KeypadEnter) return;

            Run();
            evt.StopImmediatePropagation();
        }

        private void Run()
        {
            if (_spec == null) return;

            // позиционные аргументы: хвост пустых отбрасываем, пустые в середине — умолчание или ""
            var values = new string[_bindings.Count];
            int last = -1;

            for (var i = 0; i < _bindings.Count; i++)
            {
                values[i] = ValueOf(_bindings[i]);
                if (!string.IsNullOrEmpty(values[i])) last = i;
            }

            var sb = new StringBuilder(_spec.Name);
            for (var i = 0; i <= last; i++)
            {
                string value = values[i];
                if (string.IsNullOrEmpty(value)) value = _bindings[i].Arg.DefaultText ?? string.Empty;

                sb.Append(' ');
                sb.Append(_bindings[i].Arg.Kind == ArgKind.Rest ? value : CommandLine.Quote(value));
            }

            _lastValues[_spec.Name] = values;
            _owner.RunFromForm(_id, sb.ToString());
        }

        private static string ValueOf(Binding b)
        {
            if (b.Toggle != null) return b.Toggle.value ? "true" : "false";
            if (b.Choice != null) return b.Choice.value;
            if (b.TargetLabel != null) return AppDeck.Target.IsValid ? "$" : string.Empty;
            return b.Text?.value?.Trim();
        }
    }
}
