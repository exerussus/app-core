using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>
    /// Поле выбора ключа локализации: показывает текст основного языка вместо ключа; «…» раскрывает
    /// под полем поиск по ключу и тексту на любом языке (префикс поля сужает список, пока его не сняли)
    /// и форму нового ключа — тексты языков, пакет; новый ключ пишется в пакет сразу.
    /// </summary>
    /// <remarks>
    /// Для инспектора — атрибут <see cref="LocKeyAttribute"/>; в страницах Nexus — напрямую:
    /// <c>new LocKeyField("Приветствие", "greet.") { value = key }</c> и <see cref="Changed"/>.
    /// </remarks>
    [UxmlElement]
    public partial class LocKeyField : VisualElement
    {
        private const int MaxResults = 60;

        private readonly Label _label;
        private readonly Label _shown;
        private readonly Button _toggle;
        private readonly VisualElement _popup;

        private string _value = string.Empty;
        private bool _onlyPrefix = true;
        private string _search = string.Empty;

        /// <summary>Подпись слева. Пусто — без подписи.</summary>
        [UxmlAttribute]
        public string label
        {
            get => _label.text;
            set
            {
                _label.text = value ?? string.Empty;
                _label.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        /// <summary>Префикс ключей поля (домен): сужает поиск, подставляется в новый ключ.</summary>
        [UxmlAttribute]
        public string prefix { get; set; }

        /// <summary>Хранить ключ с «^».</summary>
        [UxmlAttribute]
        public bool caret { get; set; } = true;

        /// <summary>Значение: ключ (с «^», если <see cref="caret"/>) или пусто.</summary>
        public string value
        {
            get => _value;
            set
            {
                _value = value ?? string.Empty;
                Refresh();
            }
        }

        /// <summary>Пользователь выбрал или создал ключ (новое значение).</summary>
        public event Action<string> Changed;

        public LocKeyField() : this(null) { }

        public LocKeyField(string label, string prefix = null, bool caret = true)
        {
            this.prefix = prefix;
            this.caret = caret;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            Add(row);

            _label = new Label();
            _label.AddToClassList(BaseField<string>.labelUssClassName);
            _label.style.minWidth = 120f;
            row.Add(_label);
            this.label = label;

            _shown = new Label { enableRichText = false };
            _shown.style.flexGrow = 1;
            _shown.style.flexShrink = 1;
            _shown.style.overflow = Overflow.Hidden;
            _shown.style.textOverflow = TextOverflow.Ellipsis;
            _shown.style.whiteSpace = WhiteSpace.NoWrap;
            _shown.style.paddingLeft = 4f;
            row.Add(_shown);

            _toggle = new Button(TogglePopup) { text = "…", tooltip = "Выбрать или создать ключ" };
            row.Add(_toggle);

            var clear = new Button(() => Set(string.Empty)) { text = "✕", tooltip = "Снять ключ" };
            row.Add(clear);

            _popup = new VisualElement();
            _popup.style.display = DisplayStyle.None;
            _popup.style.marginTop = 4f;
            _popup.style.marginBottom = 6f;
            _popup.style.paddingLeft = 6f;
            _popup.style.paddingRight = 6f;
            _popup.style.paddingTop = 6f;
            _popup.style.paddingBottom = 6f;
            _popup.style.borderLeftWidth = 2f;
            _popup.style.borderLeftColor = new Color(0.4f, 0.6f, 1f, 0.6f);
            Add(_popup);

            Refresh();
        }

        // ================================================================ показ

        private static LocTable Table => LocKeyCache.Merged;

        private string Key => Loc.KeyOf(_value);

        private void Refresh()
        {
            if (_shown == null) return;

            if (string.IsNullOrEmpty(_value))
            {
                _shown.text = "—";
                _shown.tooltip = "Ключ не выбран";
                return;
            }

            var entry = Table.Get(Key);
            if (entry == null)
            {
                _shown.text = $"⚠ {Key} (нет в таблицах)";
                _shown.tooltip = "Ключа нет ни в базе, ни в пакетах";
                return;
            }

            var primary = Table.Languages.Count > 0 ? Table.Languages[0].Code : null;
            var text = (primary != null ? entry.Find(primary)?.Text : null) ?? FirstText(entry) ?? "—";
            _shown.text = OneLine(text) + (LocKeyCache.IsDuplicateText(text, Key) ? $"  ({Key})" : string.Empty);

            var lines = new List<string> { Key };
            foreach (var value in entry.Values) lines.Add($"{value.Slot}: {OneLine(value.Text)}");
            _shown.tooltip = string.Join("\n", lines);
        }

        private static string FirstText(LocEntry entry)
        {
            foreach (var value in entry.Values)
                if (!string.IsNullOrEmpty(value.Text)) return value.Text;
            return null;
        }

        private static string OneLine(string text) => (text ?? string.Empty).Replace('\n', ' ');

        private void Set(string key)
        {
            var stored = string.IsNullOrEmpty(key) ? string.Empty : caret ? Loc.Prefix + Loc.KeyOf(key) : Loc.KeyOf(key);
            if (stored == _value) return;

            _value = stored;
            Refresh();
            Changed?.Invoke(_value);
        }

        // ================================================================ поиск

        private void TogglePopup()
        {
            if (_popup.style.display == DisplayStyle.Flex)
            {
                _popup.style.display = DisplayStyle.None;
                return;
            }

            LocKeyCache.Invalidate();
            _popup.style.display = DisplayStyle.Flex;
            BuildPopup();
        }

        private void BuildPopup()
        {
            _popup.Clear();

            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            _popup.Add(bar);

            var search = new TextField { value = _search };
            search.style.flexGrow = 1;
            search.tooltip = "Ключ или текст на любом языке";
            bar.Add(search);

            if (!string.IsNullOrEmpty(prefix))
            {
                var only = new Toggle(prefix) { value = _onlyPrefix, tooltip = "Только ключи с префиксом поля" };
                only.RegisterValueChangedCallback(e =>
                {
                    _onlyPrefix = e.newValue;
                    BuildPopup();
                });
                bar.Add(only);
            }

            var results = new ScrollView();
            results.style.maxHeight = 220f;
            _popup.Add(results);

            void Fill()
            {
                results.Clear();
                var count = 0;
                var query = (_search ?? string.Empty).Trim().TrimStart('^');

                foreach (var entry in LocKeyCache.Sorted)
                {
                    if (_onlyPrefix && !string.IsNullOrEmpty(prefix) && !entry.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (query.Length > 0 && !Matches(entry, query)) continue;

                    var key = entry.Key;
                    var item = new Button(() =>
                    {
                        Set(key);
                        _popup.style.display = DisplayStyle.None;
                    })
                    {
                        text = $"{key}  —  {OneLine(FirstText(entry) ?? "—")}",
                        enableRichText = false,
                    };
                    item.style.unityTextAlign = TextAnchor.MiddleLeft;
                    item.style.whiteSpace = WhiteSpace.NoWrap;
                    item.style.overflow = Overflow.Hidden;
                    if ("^" + key == _value || key == _value) item.style.unityFontStyleAndWeight = FontStyle.Bold;
                    results.Add(item);

                    if (++count >= MaxResults)
                    {
                        results.Add(new Label("… уточните поиск"));
                        break;
                    }
                }

                if (count == 0) results.Add(new Label("Ничего не найдено."));
            }

            search.RegisterValueChangedCallback(e =>
            {
                _search = e.newValue;
                Fill();
            });
            Fill();

            _popup.Add(NewKeyForm());
            search.schedule.Execute(() => search.Focus());
        }

        private static bool Matches(LocEntry entry, string query)
        {
            if (entry.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var value in entry.Values)
                if (value.Text != null && value.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ================================================================ новый ключ

        private VisualElement NewKeyForm()
        {
            var form = new Foldout { text = "Новый ключ", value = false };
            form.style.marginTop = 6f;

            var key = new TextField("Ключ") { value = prefix ?? string.Empty };
            form.Add(key);

            var workspace = LocWorkspace.Load();
            var files = new List<LocWorkspace.File>();
            var names = new List<string>();
            foreach (var file in workspace.Files)
            {
                if (!file.Writable) continue;
                files.Add(file);
                names.Add(file.Info.IsBase ? "base" : file.Name);
            }

            if (files.Count == 0)
            {
                form.Add(new HelpBox("Нет записываемых таблиц: назначьте базовую таблицу или маски пакетов.", HelpBoxMessageType.Warning));
                return form;
            }

            var preferred = LocKeyCache.PackageFor(prefix, workspace);
            var package = new DropdownField("Пакет", names, Math.Max(0, files.IndexOf(preferred)));
            form.Add(package);

            var texts = new Dictionary<string, TextField>();
            foreach (var language in workspace.Merged.Languages)
            {
                var field = new TextField(language.Code + (string.IsNullOrEmpty(language.Name) ? string.Empty : " · " + language.Name)) { multiline = true };
                texts[language.Code] = field;
                form.Add(field);
            }

            var create = new Button(() =>
            {
                var name = (key.value ?? string.Empty).Trim().TrimStart('^').Replace(' ', '_');
                var target = files[Math.Max(0, package.index)];

                // Свежая копия: таблицы могли измениться, пока форма была открыта.
                var fresh = LocWorkspace.Load();
                var file = fresh.FindFile(target.Info.Path);
                if (!LocSlots.IsValidKey(name) || fresh.Merged.Contains(name) || file == null || !fresh.AddKey(name, file))
                {
                    EditorUtility.DisplayDialog("Новый ключ", $"Ключ «{name}» не создать: занят, недопустим (латиница, цифры, _ . -) или пакет только для чтения.", "Ок");
                    return;
                }

                foreach (var pair in texts)
                    if (!string.IsNullOrWhiteSpace(pair.Value.value)) fresh.Merged.Set(name, pair.Key, pair.Value.value);

                var result = fresh.Save();
                if (!result.Ok)
                {
                    EditorUtility.DisplayDialog("Новый ключ", "Не сохранено:\n" + string.Join("\n", result.Conflicts) + string.Join("\n", result.Errors), "Ок");
                    return;
                }

                LocEditor.GenerateKeys();
                LocKeyCache.Invalidate();
                Set(name);
                _popup.style.display = DisplayStyle.None;
            }) { text = "Создать и выбрать" };
            create.style.marginTop = 4f;
            form.Add(create);
            return form;
        }
    }
}
