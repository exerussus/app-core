using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Окно AppDeck: шапка с вкладками, тело и ручка высоты. Режимы — шторка (Partial) и весь экран (Full).
    /// </summary>
    /// <remarks>
    /// Смена режима и перетаскивание ручки меняют ровно одно свойство — высоту панели; содержимое
    /// (виртуальные списки) подстраивается само по событию геометрии, ничего не пересоздаётся.
    /// Корень окна растянут на экран, но не ловит указатель: клик мимо панели уходит в мир.
    /// </remarks>
    internal sealed class DeckWindow : VisualElement
    {
        private const string PrefsLayout = "appdeck.layout";
        private const string PrefsHeight = "appdeck.height";
        private const string PrefsTab = "appdeck.tab";
        private const string PrefsPick = "appdeck.pick";

        private const float MinHeight = 0.15f;
        private const float MaxHeight = 0.95f;

        private readonly VisualElement _panel;
        private readonly VisualElement _tabsBar;
        private readonly VisualElement _body;
        private readonly VisualElement _resizer;
        private readonly VisualElement _targetChip;
        private readonly Label _targetLabel;
        private readonly Button _pickButton;
        private readonly Button _layoutButton;

        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly List<DeckTab> _tabButtonTabs = new List<DeckTab>();
        private int _tabsVersion = -1;

        private DeckTab _active;
        private bool _isOpen;
        private string _wantedTab;
        private DeckLayout _layout;
        private float _partialHeight;
        private bool _pickArmed;

        private bool _dragging;
        private float _dragStartY;
        private float _dragStartHeight;

        public DeckWindow(DeckSettings settings)
        {
            name = "appDeck";
            AddToClassList("appdeck");
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;

            _panel = new VisualElement { name = "panel" };
            _panel.AddToClassList("appdeck__panel");
            _panel.style.position = Position.Absolute;
            _panel.style.left = 0;
            _panel.style.right = 0;
            _panel.style.top = 0;
            Add(_panel);

            // ---- шапка
            var header = new VisualElement { name = "header" };
            header.AddToClassList("appdeck__header");
            header.RegisterCallback<ClickEvent>(OnHeaderClick);
            _panel.Add(header);

            _tabsBar = new VisualElement { name = "tabs" };
            _tabsBar.AddToClassList("appdeck__tabs");
            header.Add(_tabsBar);

            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.style.flexGrow = 1;
            header.Add(spacer);

            _targetChip = new VisualElement { name = "target" };
            _targetChip.AddToClassList("appdeck__target");
            _targetLabel = new Label { enableRichText = false };
            _targetLabel.AddToClassList("appdeck__target-label");
            var clearTarget = new Button(AppDeck.ClearTarget) { text = "×", tooltip = "Снять цель" };
            clearTarget.AddToClassList("appdeck__icon-button");
            _targetChip.Add(_targetLabel);
            _targetChip.Add(clearTarget);
            header.Add(_targetChip);

            _pickButton = new Button(TogglePick) { text = "◎", tooltip = "Выбор цели кликом по миру" };
            _pickButton.AddToClassList("appdeck__icon-button");
            _pickButton.style.display = settings == null || settings.Picking ? DisplayStyle.Flex : DisplayStyle.None;
            header.Add(_pickButton);

            _layoutButton = new Button(ToggleLayout) { tooltip = "Шторка / весь экран (Shift + клавиша открытия)" };
            _layoutButton.AddToClassList("appdeck__icon-button");
            header.Add(_layoutButton);

            var close = new Button(AppDeck.Close) { text = "✕", tooltip = "Закрыть" };
            close.AddToClassList("appdeck__icon-button");
            header.Add(close);

            // ---- тело
            _body = new VisualElement { name = "body" };
            _body.AddToClassList("appdeck__body");
            _body.style.flexGrow = 1;
            _panel.Add(_body);

            // ---- ручка высоты
            _resizer = new VisualElement { name = "resizer" };
            _resizer.AddToClassList("appdeck__resizer");
            _resizer.RegisterCallback<PointerDownEvent>(OnResizeDown);
            _resizer.RegisterCallback<PointerMoveEvent>(OnResizeMove);
            _resizer.RegisterCallback<PointerUpEvent>(OnResizeUp);
            _resizer.RegisterCallback<PointerCaptureOutEvent>(_ => _dragging = false);
            _panel.Add(_resizer);

            // ---- запомненное состояние
            float defaultHeight = settings != null ? settings.PartialHeight : 0.45f;
            _partialHeight = Mathf.Clamp(PlayerPrefs.GetFloat(PrefsHeight, defaultHeight), MinHeight, MaxHeight);
            int defaultLayout = settings != null ? (int)settings.DefaultLayout : 0;
            _layout = PlayerPrefs.GetInt(PrefsLayout, defaultLayout) == 1 ? DeckLayout.Full : DeckLayout.Partial;
            _pickArmed = PlayerPrefs.GetInt(PrefsPick, 1) == 1;
            _wantedTab = PlayerPrefs.GetString(PrefsTab, ConsoleTab.TabId);

            ApplyLayout();
            ApplyPick();
            SetTarget(AppDeck.Target);
        }

        public DeckLayout Layout => _layout;

        public DeckTab ActiveTab => _active;

        /// <summary>Выбор цели кликом сейчас включён (кнопка ◎ и режим шторки).</summary>
        public bool PickActive => _pickArmed && _layout == DeckLayout.Partial && _pickButton.style.display != DisplayStyle.None;

        // ---------------------------------------------------------------- режимы

        public void SetLayout(DeckLayout layout)
        {
            if (_layout == layout) return;

            _layout = layout;
            PlayerPrefs.SetInt(PrefsLayout, (int)layout);
            ApplyLayout();
        }

        private void ToggleLayout() => SetLayout(_layout == DeckLayout.Full ? DeckLayout.Partial : DeckLayout.Full);

        private void ApplyLayout()
        {
            bool full = _layout == DeckLayout.Full;
            EnableInClassList("appdeck--full", full);
            _panel.style.height = Length.Percent((full ? 1f : _partialHeight) * 100f);
            _resizer.style.display = full ? DisplayStyle.None : DisplayStyle.Flex;
            _layoutButton.text = full ? "▭" : "▣";
        }

        private void OnHeaderClick(ClickEvent evt)
        {
            // двойной клик по пустому месту шапки — смена режима
            if (evt.clickCount == 2 && evt.target == evt.currentTarget) ToggleLayout();
        }

        private void TogglePick()
        {
            _pickArmed = !_pickArmed;
            PlayerPrefs.SetInt(PrefsPick, _pickArmed ? 1 : 0);
            ApplyPick();
        }

        private void ApplyPick() => _pickButton.EnableInClassList("appdeck__icon-button--on", _pickArmed);

        public void SetTarget(DeckTarget target)
        {
            bool has = target.IsValid;
            _targetChip.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (has) _targetLabel.text = target.Label ?? target.Value.ToString();
        }

        // ---------------------------------------------------------------- ручка высоты

        private void OnResizeDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;

            _dragging = true;
            _dragStartY = evt.position.y;
            _dragStartHeight = _panel.layout.height;
            _resizer.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnResizeMove(PointerMoveEvent evt)
        {
            if (!_dragging || !_resizer.HasPointerCapture(evt.pointerId)) return;

            float total = layout.height;
            if (total <= 0f) return;

            float height = _dragStartHeight + (evt.position.y - _dragStartY);
            _partialHeight = Mathf.Clamp(height / total, MinHeight, MaxHeight);
            _panel.style.height = Length.Percent(_partialHeight * 100f);
        }

        private void OnResizeUp(PointerUpEvent evt)
        {
            if (!_dragging) return;

            _dragging = false;
            _resizer.ReleasePointer(evt.pointerId);
            PlayerPrefs.SetFloat(PrefsHeight, _partialHeight);
        }

        // ---------------------------------------------------------------- вкладки

        /// <summary>Сверить кнопки вкладок с реестром. Дёшево: сравнение версии.</summary>
        public void SyncTabs()
        {
            if (_tabsVersion == AppDeck.TabsVersion) return;
            _tabsVersion = AppDeck.TabsVersion;

            for (var i = 0; i < _tabButtons.Count; i++) _tabButtons[i].RemoveFromHierarchy();
            _tabButtons.Clear();
            _tabButtonTabs.Clear();

            IReadOnlyList<DeckTab> tabs = AppDeck.Tabs;
            for (var i = 0; i < tabs.Count; i++)
            {
                DeckTab tab = tabs[i];
                var button = new Button(() => ShowTab(tab)) { text = tab.Title };
                button.AddToClassList("appdeck__tab");
                _tabsBar.Add(button);
                _tabButtons.Add(button);
                _tabButtonTabs.Add(tab);
            }

            if (_active != null && !Contains(tabs, _active)) _active = null;

            if (_active == null)
            {
                DeckTab wanted = Find(tabs, _wantedTab) ?? (tabs.Count > 0 ? tabs[0] : null);
                if (wanted != null) ShowTab(wanted);
            }

            HighlightActive();
        }

        public void ShowTab(string tabId)
        {
            SyncTabs();
            DeckTab tab = Find(AppDeck.Tabs, tabId);
            if (tab != null) ShowTab(tab);
            else _wantedTab = tabId;
        }

        private void ShowTab(DeckTab tab)
        {
            if (_active == tab) return;

            if (_active != null && _active.IsBuilt)
            {
                _active.Root.style.display = DisplayStyle.None;
                if (_active.IsVisible)
                {
                    _active.IsVisible = false;
                    _active.OnHide();
                }
            }

            _active = tab;
            _wantedTab = tab.Id;
            PlayerPrefs.SetString(PrefsTab, tab.Id);

            if (!tab.IsBuilt)
            {
                var root = new VisualElement { name = "tab-" + tab.Id };
                root.AddToClassList("appdeck__tab-root");
                root.style.flexGrow = 1;
                _body.Add(root);
                tab.Root = root;
                tab.Build(root);
            }

            tab.Root.style.display = DisplayStyle.Flex;

            // OnShow/OnHide — только при открытом окне и ровно по разу
            if (_isOpen && !tab.IsVisible)
            {
                tab.IsVisible = true;
                tab.OnShow();
            }

            HighlightActive();
        }

        public void OnTabRemoved(DeckTab tab)
        {
            if (tab.IsBuilt)
            {
                tab.Root.RemoveFromHierarchy();
                tab.Root = null;
            }

            tab.IsVisible = false;
            tab.Dispose();

            if (_active == tab) _active = null;
            _tabsVersion = -1;
        }

        private void HighlightActive()
        {
            for (var i = 0; i < _tabButtons.Count; i++)
                _tabButtons[i].EnableInClassList("appdeck__tab--active", _tabButtonTabs[i] == _active);
        }

        private static bool Contains(IReadOnlyList<DeckTab> tabs, DeckTab tab)
        {
            for (var i = 0; i < tabs.Count; i++)
            {
                if (tabs[i] == tab) return true;
            }

            return false;
        }

        private static DeckTab Find(IReadOnlyList<DeckTab> tabs, string id)
        {
            if (id == null) return null;

            for (var i = 0; i < tabs.Count; i++)
            {
                if (tabs[i].Id == id) return tabs[i];
            }

            return null;
        }

        // ---------------------------------------------------------------- кадр

        public void OnOpened()
        {
            _isOpen = true;
            SyncTabs();

            if (_active != null && _active.IsBuilt && !_active.IsVisible)
            {
                _active.IsVisible = true;
                _active.OnShow();
            }
        }

        public void OnClosed()
        {
            _isOpen = false;

            if (_active != null && _active.IsVisible)
            {
                _active.IsVisible = false;
                _active.OnHide();
            }
        }

        public void Tick(float deltaTime)
        {
            SyncTabs();
            if (_active != null && _active.IsVisible) _active.Tick(deltaTime);
        }

    }
}
