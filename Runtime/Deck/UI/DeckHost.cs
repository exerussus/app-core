using UnityEngine;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Единственный MonoBehaviour AppDeck: своя панель поверх всех остальных, кадр, клавиши, курсор,
    /// выбор цели. Создаётся кодом до первой сцены и живёт через все загрузки (DontDestroyOnLoad).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Своя панель, а не слой внутри AppRunner: окна игры живут в других документах и панелях,
    /// а экраны загрузки — внутри AppRunner. Только отдельная панель с наибольшим порядком отрисовки
    /// гарантированно накрывает всё, и только объект вне сцен переживает её перезагрузку.
    /// </para>
    /// <para>
    /// Закрытое окно ничего не стоит: дерево скрыто (display: none), кадр — переливка логов
    /// и опрос одной клавиши.
    /// </para>
    /// </remarks>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    internal sealed class DeckHost : MonoBehaviour
    {
        private DeckSettings _settings;
        private PanelRenderer _renderer;
        private PanelSettings _panelClone;
        private VisualElement _root;
        private DeckWindow _window;
        private DeckHud _hud;

        private bool _open;
        private bool _wantOpen;
        private int _closedFrame = -1;
        private int _focusFrame = -1;
        private int _refocusFrame = -1;

        private bool _cursorCaptured;
        private CursorLockMode _restoreLock;
        private bool _restoreVisible;

        private PhysicsDeckPicker _physicsPicker;
        private DeckBuiltinMetrics _builtinMetrics;
        private float _metricsClock;
        private float _metricsNext;

        public static DeckHost Create(DeckSettings settings)
        {
            var go = new GameObject("[AppDeck]");
            go.SetActive(false);
            DontDestroyOnLoad(go);

            var host = go.AddComponent<DeckHost>();
            host._settings = settings;
            host._wantOpen = settings != null && settings.StartOpen;

            host._renderer = go.AddComponent<PanelRenderer>();
            host._panelClone = CreatePanelSettings(settings);
            host._renderer.panelSettings = host._panelClone;
            host._renderer.RegisterUIReloadCallback(host.OnUIReload);

            // встроенные вкладки — владелец «встроенное», проект добавляет свои рядом
            AppDeck.AddTab(new ConsoleTab(settings), AppDeck.BuiltinOwner);
            DeckBuiltinTabs.Register(settings);

            if (settings == null || (settings.Picking && settings.PhysicsPicker))
            {
                host._physicsPicker = new PhysicsDeckPicker(settings != null ? settings.PickDistance : 1000f,
                                                            settings != null ? settings.PickLayers : ~0);
                AppDeck.RegisterPicker(host._physicsPicker, AppDeck.BuiltinOwner);
            }

            AppDeck.TargetChanged += host.OnTargetChanged;

            host._builtinMetrics = new DeckBuiltinMetrics(AppDeck.BuiltinOwner);
            host._builtinMetrics.Register();

            go.SetActive(true);
            return host;
        }

        /// <summary>
        /// Копия настроек панели приложения: та же тема, шрифты и масштаб, но свой порядок отрисовки.
        /// Без источника — пустые настройки (стили AppDeck самодостаточны, но без шрифтов с кириллицей).
        /// </summary>
        private static PanelSettings CreatePanelSettings(DeckSettings settings)
        {
            PanelSettings source = settings != null ? settings.PanelSettings : null;
            PanelSettings clone;

            if (source != null)
            {
                clone = Instantiate(source);
                clone.name = source.name + " (AppDeck)";
            }
            else
            {
                Debug.LogWarning($"[AppDeck] Нет настроек панели в {DeckSettings.AssetPath} — панель без темы приложения.");
                clone = ScriptableObject.CreateInstance<PanelSettings>();
                clone.name = "AppDeck Panel";
            }

            clone.sortingOrder = settings != null ? settings.SortingOrder : 30000f;
            return clone;
        }

        public bool IsOpen => _open;

        public bool IsTyping => _open && _window?.ActiveTab != null && _window.ActiveTab.IsTyping;

        /// <summary>Окно закрылось в этом кадре — игра ещё не должна реагировать на ту же клавишу (Escape).</summary>
        public bool ClosedThisFrame => _closedFrame == Time.frameCount;

        public DeckLayout Layout => _window?.Layout ?? DeckLayout.Partial;

        // ---------------------------------------------------------------- панель

        private void OnUIReload(PanelRenderer renderer, VisualElement root, int version)
        {
            if (root == null) return;

            // LiveReload может отдать новый корень — переносим уже построенное окно, а не строим заново
            if (_window != null)
            {
                if (_root != root)
                {
                    _root = root;
                    PrepareRoot(root);
                    if (_hud != null) root.Add(_hud);
                    root.Add(_window);
                }

                return;
            }

            _root = root;
            PrepareRoot(root);

            _hud = new DeckHud(_settings);
            root.Add(_hud);

            _window = new DeckWindow(_settings);
            _window.style.display = DisplayStyle.None;
            _window.RegisterCallback<KeyDownEvent>(OnWindowKeyDown, TrickleDown.TrickleDown);
            root.Add(_window);

            if (_wantOpen) SetOpen(true);
        }

        /// <summary>
        /// Корень панели: стили AppDeck и прозрачность для указателя. Корень растянут на весь экран
        /// на самом верхнем порядке отрисовки — ловя указатель, он отнял бы клики у всех панелей ниже.
        /// </summary>
        private void PrepareRoot(VisualElement root)
        {
            root.pickingMode = PickingMode.Ignore;

            StyleSheet sheet = _settings != null ? _settings.StyleSheet : null;
            if (sheet == null)
            {
                Debug.LogWarning($"[AppDeck] Нет таблицы стилей ({DeckSettings.StyleSheetPath}) — окно без оформления.");
                return;
            }

            if (!root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
        }

        /// <summary>
        /// Символ клавиши открытия не должен попасть в поле ввода (нажали «`» — окно закрылось,
        /// а в строке остался «`»). Глотаем его до того, как событие дойдёт до поля.
        /// </summary>
        private void OnWindowKeyDown(KeyDownEvent evt)
        {
            DeckHotkey key = _settings != null ? _settings.ToggleKey : DeckHotkey.BackQuote;
            KeyCode code = DeckInput.ToKeyCode(key);

            // Escape тоже глотаем: поле ввода на Escape откатывает текст к значению на момент фокуса —
            // набранная строка пропала бы. Сам Escape обрабатывает кадр хоста (подсказки, закрытие окна).
            bool swallow = (evt.keyCode == code && code != KeyCode.None) || evt.keyCode == KeyCode.Escape;
            if (!swallow && key == DeckHotkey.BackQuote)
            {
                char c = evt.character;
                swallow = c == '`' || c == '~' || c == 'ё' || c == 'Ё';
            }

            if (!swallow) return;

            evt.StopImmediatePropagation();
            _window.focusController?.IgnoreEvent(evt);
        }

        // ---------------------------------------------------------------- открыть / закрыть

        public void SetOpen(bool open)
        {
            _wantOpen = open;
            if (_window == null || open == _open) return;

            _open = open;
            _window.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;

            if (open)
            {
                _window.OnOpened();
                // фокус — в следующем кадре: в этом ещё летит событие клавиши открытия
                _focusFrame = Time.frameCount + 1;
                CaptureCursor();
            }
            else
            {
                _window.OnClosed();
                _closedFrame = Time.frameCount;
                _window.focusController?.focusedElement?.Blur();
                ReleaseCursor();
            }

            AppDeck.RaiseOpenChanged(open);
        }

        public void SetLayout(DeckLayout layout) => _window?.SetLayout(layout);

        public void ShowTab(string tabId) => _window?.ShowTab(tabId);

        public void OnTabRemoved(DeckTab tab) => _window?.OnTabRemoved(tab);

        private void OnTargetChanged(DeckTarget target) => _window?.SetTarget(target);

        // ---------------------------------------------------------------- курсор

        private void CaptureCursor()
        {
            if (_settings != null && !_settings.UnlockCursor) return;

            _restoreLock = Cursor.lockState;
            _restoreVisible = Cursor.visible;
            _cursorCaptured = true;
            EnforceCursor();
        }

        /// <summary>Игра могла захватить курсор, пока окно открыто (смена страницы) — запоминаем её желание и держим свободным.</summary>
        private void EnforceCursor()
        {
            if (!_cursorCaptured) return;

            if (Cursor.lockState != CursorLockMode.None)
            {
                _restoreLock = Cursor.lockState;
                Cursor.lockState = CursorLockMode.None;
            }

            if (!Cursor.visible)
            {
                _restoreVisible = false;
                Cursor.visible = true;
            }
        }

        private void ReleaseCursor()
        {
            if (!_cursorCaptured) return;

            _cursorCaptured = false;
            Cursor.lockState = _restoreLock;
            Cursor.visible = _restoreVisible;
        }

        // ---------------------------------------------------------------- кадр

        private void Update()
        {
            AppDeck.PumpLogs();

            if (_window == null) return;

            DeckHotkey key = _settings != null ? _settings.ToggleKey : DeckHotkey.BackQuote;
            if (DeckInput.WasPressed(key))
            {
                if (_open && DeckInput.ShiftHeld) _window.SetLayout(_window.Layout == DeckLayout.Full ? DeckLayout.Partial : DeckLayout.Full);
                else if (!_open && DeckInput.ShiftHeld)
                {
                    SetOpen(true);
                    _window.SetLayout(DeckLayout.Full);
                }
                else SetOpen(!_open);
            }

            float dt = Time.unscaledDeltaTime;

            if (_open)
            {
                if (DeckInput.EscapePressed && (_window.ActiveTab == null || !_window.ActiveTab.HandleEscape())) SetOpen(false);
            }

            if (_open)
            {
                EnforceCursor();

                if (Time.frameCount == _focusFrame) _window.ActiveTab?.Focus();
                else if (Time.frameCount == _refocusFrame) _window.ActiveTab?.Refocus();

                if (_window.PickActive && DeckInput.LeftClick(out Vector2 screen) && !IsOverWindow(screen))
                {
                    AppDeck.Pick(screen);

                    // клик по миру забрал фокус у строки ввода — возвращаем, чтобы сразу писать команду про цель
                    _refocusFrame = Time.frameCount + 1;
                }

                _window.Tick(dt);
            }

            _builtinMetrics?.Tick(dt);
            TickAlwaysTracked(dt);
            _hud?.Tick(dt, _open);
        }

        /// <summary>Треки с «копить всегда» опрашиваются и при закрытом окне — с частотой метрик.</summary>
        private void TickAlwaysTracked(float dt)
        {
            _metricsClock += dt;
            if (_metricsClock < _metricsNext) return;

            float interval = 1f / Mathf.Max(1, _settings != null ? _settings.MetricsRate : 8);
            _metricsNext = _metricsClock + interval;

            MetricRegistry metrics = AppDeck.Metrics;
            for (var slot = 0; slot < metrics.HighWater; slot++)
            {
                if (metrics.IsAliveSlot(slot) && metrics.AlwaysTrack(slot)) metrics.Sample(slot, interval * 0.5f);
            }
        }

        private bool IsOverWindow(Vector2 screen)
        {
            IPanel panel = _root?.panel;
            if (panel == null) return false;

            // корень, окно и HUD не ловят указатель — любой пойманный элемент принадлежит окну или его
            // всплывающим меню (выпадающие списки живут в корне панели, вне окна)
            Vector2 point = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.x, Screen.height - screen.y));
            return panel.Pick(point) != null;
        }

        private void OnDestroy()
        {
            AppDeck.TargetChanged -= OnTargetChanged;
            if (_physicsPicker != null) AppDeck.UnregisterPicker(_physicsPicker);
            _builtinMetrics?.Dispose();
            if (_renderer != null) _renderer.UnregisterUIReloadCallback(OnUIReload);

            ReleaseCursor();
            PlayerPrefs.Save();

            if (_panelClone != null) Destroy(_panelClone);
            AppDeck.DetachHost(this);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) PlayerPrefs.Save();
        }
    }
}
