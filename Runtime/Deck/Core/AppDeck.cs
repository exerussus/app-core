using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// AppDeck — панель приложения поверх всего: консоль, быстрые действия, метрики.
    /// Единая точка входа для регистрации и управления.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Два слоя. Реестры (команды, подсказки, пикеры, вкладки) — дешёвые данные, работают всегда,
    /// даже когда панель выключена настройкой: код игры и DSL-мост собираются и регистрируют одинаково.
    /// Окно и перехват логов поднимаются только если <see cref="DeckSettings.ShouldRun"/>.
    /// </para>
    /// <para>
    /// AppCore не знает, кто регистрирует: C#, скрипты, моды. У каждой регистрации есть владелец —
    /// <see cref="UnregisterAll"/> снимает всё его разом (перезагрузка скриптов, выключение мода).
    /// </para>
    /// <para>
    /// Внутреннее состояние спрятано в приватной статике и наружу видно только через хэндлы и методы.
    /// Сбрасывается на SubsystemRegistration — корректно и без Domain Reload.
    /// Всё, кроме <see cref="Print"/>, — только с главного потока.
    /// </para>
    /// </remarks>
    public static class AppDeck
    {
        /// <summary>Владелец встроенных команд.</summary>
        internal static readonly object BuiltinOwner = new object();

        private static CommandRegistry _commands = new CommandRegistry();
        private static SuggestionRegistry _suggestions = new SuggestionRegistry();
        private static ActionRegistry _actions = new ActionRegistry();
        private static MetricRegistry _metrics = new MetricRegistry();
        private static LogIntake _intake;
        private static LogBuffer _log;
        private static DeckHost _host;
        private static int _mainThreadId;
        private static bool _logSubscribed;

        private static readonly List<TargetResolver> _resolvers = new List<TargetResolver>();
        private static readonly List<object> _resolverOwners = new List<object>();
        private static DeckTarget _target;

        private static readonly List<DeckTab> _tabs = new List<DeckTab>();
        private static readonly List<object> _tabOwners = new List<object>();
        private static int _tabsVersion;

        // ================================================================ жизненный цикл

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Unsubscribe();

            _commands = new CommandRegistry();
            _suggestions = new SuggestionRegistry();
            _actions = new ActionRegistry();
            _metrics = new MetricRegistry();
            _log = null;
            _host = null;
            _target = default;
            HardwareCursorHidden = false;
            _resolvers.Clear();
            _resolverOwners.Clear();
            _tabs.Clear();
            _tabOwners.Clear();
            _tabsVersion++;
            OpenChanged = null;
            TargetChanged = null;

            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            // логи ловим с самого начала: бут, загрузка сцены — всё попадёт в консоль.
            // Если настройка скажет «выключено», подписка снимется в Boot.
            _intake = new LogIntake(2048);
            Application.logMessageReceivedThreaded += OnUnityLog;
            Application.quitting += Unsubscribe;
            _logSubscribed = true;

            DeckBuiltins.Register();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            DeckSettings settings = DeckSettings.Current;
            bool run = settings != null ? settings.ShouldRun : Debug.isDebugBuild;

            if (!run)
            {
                Unsubscribe();
                _intake = null;
                return;
            }

#if !UNITY_EDITOR
            // настройки не попали в сборку (не в Preloaded Assets собираемого профиля) — иначе это не видно
            if (settings == null)
                Debug.LogWarning("[AppDeck] Настройки AppDeckSettings не попали в сборку — работают умолчания. " +
                                 "Проверьте Preloaded Assets профиля сборки.");
#endif

            _log = new LogBuffer(settings != null ? settings.LogCapacity : 4096)
            {
                Collapse = settings == null || settings.CollapseDuplicates,
            };

            _host = DeckHost.Create(settings);
        }

        private static void Unsubscribe()
        {
            if (!_logSubscribed) return;

            Application.logMessageReceivedThreaded -= OnUnityLog;
            Application.quitting -= Unsubscribe;
            _logSubscribed = false;
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            DeckLogType kind = type switch
            {
                LogType.Warning => DeckLogType.Warning,
                LogType.Log => DeckLogType.Log,
                _ => DeckLogType.Error,
            };

            _intake?.Push(kind, condition, stackTrace);
        }

        /// <summary>Перелить накопленные логи в консоль. Хост зовёт раз в кадр.</summary>
        internal static void PumpLogs() => _intake?.Drain(_log, Time.frameCount);

        internal static LogBuffer Log => _log;

        internal static CommandRegistry Commands => _commands;

        internal static SuggestionRegistry Suggestions => _suggestions;

        internal static void DetachHost(DeckHost host)
        {
            if (_host == host) _host = null;
        }

        // ================================================================ команды

        /// <summary>Начать описание команды (fluent). Завершается <see cref="CommandBuilder.Register"/>.</summary>
        public static CommandBuilder Command(string name) => new CommandBuilder(name);

        /// <summary>Зарегистрировать команду. Имя занято — прежняя команда снимается (поздний побеждает).</summary>
        public static CommandHandle Register(CommandSpec spec, CommandHandler handler, object owner = null) =>
            _commands.Register(spec, handler, owner);

        /// <summary>Зарегистрировать команду с сигнатурой строкой (<see cref="CommandSignature"/>).</summary>
        public static CommandHandle Register(string name, string signature, string summary, CommandHandler handler,
                                             object owner = null) =>
            _commands.Register(new CommandSpec(name, summary, CommandSignature.Parse(signature)), handler, owner);

        public static bool Unregister(CommandHandle handle) => _commands.Unregister(handle);

        public static bool Unregister(string commandName) => _commands.Unregister(commandName);

        public static bool IsRegistered(CommandHandle handle) => _commands.IsAlive(handle);

        public static bool IsRegistered(string commandName) => commandName != null && _commands.Find(commandName.AsSpan()) >= 0;

        /// <summary>Описание команды по имени или null.</summary>
        public static CommandSpec FindCommand(string commandName) =>
            commandName == null ? null : _commands.Spec(_commands.Find(commandName.AsSpan()));

        /// <summary>Сколько команд зарегистрировано.</summary>
        public static int CommandCount => _commands.Count;

        /// <summary>Растёт на любое изменение состава команд.</summary>
        public static int CommandsVersion => _commands.Version;

        /// <summary>
        /// Снять ВСЁ, что зарегистрировал владелец: команды, подсказки, пикеры, вкладки, кнопки, метрики.
        /// Возвращает число снятых регистраций.
        /// </summary>
        public static int UnregisterAll(object owner)
        {
            if (owner == null) return 0;

            int removed = _commands.UnregisterAll(owner) + _suggestions.UnregisterAll(owner);

            for (int i = _resolvers.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_resolverOwners[i], owner)) continue;

                _resolvers.RemoveAt(i);
                _resolverOwners.RemoveAt(i);
                removed++;
            }

            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_tabOwners[i], owner)) continue;

                RemoveTabAt(i);
                removed++;
            }

            removed += UnregisterAllExtra(owner);
            return removed;
        }

        private static int UnregisterAllExtra(object owner) => _actions.RemoveAll(owner) + UnregisterMetrics(owner);

        /// <summary>
        /// Выполнить строку команд (несколько — через «;»). Эхо и вывод попадают в консоль.
        /// Возвращает false, если что-то не нашлось, не разобралось или упало.
        /// </summary>
        public static bool Execute(string line, CommandSource source = CommandSource.Code, bool echo = true)
        {
            if (string.IsNullOrWhiteSpace(line)) return true;

            if (echo) Print(line, DeckLogType.Input);

            bool ok = _commands.Execute(line, source, _target);

            // вывод команды виден в том же кадре, а не в следующем
            if (IsMainThread) PumpLogs();
            return ok;
        }

        // ================================================================ кнопки

        /// <summary>Кнопка во вкладке Actions: нажатие выполняет строку команд.</summary>
        public static ActionHandle AddAction(string title, string line, string tags = null, object owner = null, bool confirm = false) =>
            _actions.Add(new ActionSpec(title, line, tags, confirm: confirm), owner);

        /// <summary>Кнопка с полным описанием (форма команды, подсказка, подтверждение, стабильный id).</summary>
        public static ActionHandle AddAction(ActionSpec spec, object owner = null) => _actions.Add(spec, owner);

        public static bool RemoveAction(ActionHandle handle) => _actions.Remove(handle);

        public static bool IsAlive(ActionHandle handle) => _actions.IsAlive(handle);

        internal static ActionRegistry Actions => _actions;

        // ================================================================ метрики

        /// <summary>
        /// Числовая метрика, которая читается функцией — только когда её показывают (вкладка, мини-HUD),
        /// не чаще заданной частоты. <paramref name="track"/> — хранить историю для спарклайна.
        /// </summary>
        public static MetricHandle AddGauge(string name, Func<double> read, string group = null, string unit = null,
                                            string format = "F1", bool track = false, object owner = null) =>
            _metrics.Add(name, MetricKind.Gauge, read, null, group, unit, format, track ? TrackLength : 0, false, owner);

        /// <summary>Строковая метрика (текущая страница, сцена, режим). Функция зовётся только при показе.</summary>
        public static MetricHandle AddText(string name, Func<string> read, string group = null, object owner = null) =>
            _metrics.Add(name, MetricKind.Text, null, read, group, null, null, 0, false, owner);

        /// <summary>
        /// Счётчик, который пишет сам код: <see cref="Count"/> / <see cref="SetMetric"/>. Показывается значение
        /// и скорость в секунду. <paramref name="alwaysTrack"/> — копить историю, даже когда вкладка закрыта.
        /// </summary>
        public static MetricHandle AddCounter(string name, string group = null, string unit = null, string format = "F0",
                                              bool track = false, bool alwaysTrack = false, object owner = null) =>
            _metrics.Add(name, MetricKind.Counter, null, null, group, unit, format, track || alwaysTrack ? TrackLength : 0, alwaysTrack, owner);

        /// <summary>Прибавить к счётчику. Горячий путь: проверка версии и сложение в массиве.</summary>
        public static void Count(MetricHandle handle, double delta = 1) => _metrics.Add(handle, delta);

        /// <summary>Записать значение счётчика.</summary>
        public static void SetMetric(MetricHandle handle, double value) => _metrics.Set(handle, value);

        public static bool RemoveMetric(MetricHandle handle) => _metrics.Remove(handle);

        /// <summary>Закрепить метрику в мини-HUD по имени (закрепление запоминается).</summary>
        public static void PinMetric(string name, bool pinned = true) => MetricPins.Set(name, pinned);

        private static int UnregisterMetrics(object owner) => _metrics.RemoveAll(owner);

        private static int TrackLength => DeckSettings.Current != null ? DeckSettings.Current.TrackLength : 120;

        internal static MetricRegistry Metrics => _metrics;

        // ================================================================ подсказки

        /// <summary>Источник подсказок для аргументов с <c>@имя</c>. То же имя — замена.</summary>
        public static void RegisterSuggestions(string name, SuggestionProvider provider, object owner = null) =>
            _suggestions.Register(name, provider, owner);

        /// <summary>Источник подсказок по готовому списку (список читается вживую, копии нет).</summary>
        public static void RegisterSuggestions(string name, IReadOnlyList<string> values, object owner = null) =>
            _suggestions.Register(name, SuggestionRegistry.FromList(values), owner);

        public static bool UnregisterSuggestions(string name) => _suggestions.Unregister(name);

        /// <summary>Есть ли источник подсказок с таким именем.</summary>
        public static bool HasSuggestions(string name) => _suggestions.Has(name);

        // ================================================================ вывод

        /// <summary>Строка в консоль AppDeck (в консоль Unity не дублируется). Можно с любого потока.</summary>
        public static void Print(string message, DeckLogType type = DeckLogType.Output, string stack = null) =>
            _intake?.Push(type, message, stack);

        /// <summary>Очистить консоль.</summary>
        public static void ClearLog() => _log?.Clear();

        // ================================================================ цель

        /// <summary>Выбранная цель (ставит владелец механизма выбора). Команды получают её аргументом <see cref="ArgKind.Target"/>.</summary>
        public static DeckTarget Target => _target.IsValid ? _target : default;

        public static event Action<DeckTarget> TargetChanged;

        public static void SetTarget(DeckTarget target)
        {
            _target = target;

            try { TargetChanged?.Invoke(target); }
            catch (Exception e) { Debug.LogException(e); }
        }

        public static void ClearTarget() => SetTarget(default);

        /// <summary>
        /// Резолвер токенов цели: <c>#42</c>, <c>me</c>, имя — как решит владелец. Спрашиваются по очереди
        /// регистрации, первый ответивший побеждает.
        /// </summary>
        public static void RegisterTargetResolver(TargetResolver resolver, object owner = null)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (_resolvers.Contains(resolver)) return;

            _resolvers.Add(resolver);
            _resolverOwners.Add(owner);
        }

        public static bool UnregisterTargetResolver(TargetResolver resolver)
        {
            int index = _resolvers.IndexOf(resolver);
            if (index < 0) return false;

            _resolvers.RemoveAt(index);
            _resolverOwners.RemoveAt(index);
            return true;
        }

        /// <summary>Разрешить токен в цель зарегистрированными резолверами. Падение резолвера — в консоль, не роняет разбор.</summary>
        public static bool TryResolveTarget(string token, out DeckTarget target)
        {
            for (var i = 0; i < _resolvers.Count; i++)
            {
                try
                {
                    if (_resolvers[i](token, out target) && target.IsValid) return true;
                }
                catch (Exception e)
                {
                    Print($"Резолвер цели упал на «{token}»: {e.Message}", DeckLogType.Error, e.ToString());
                }
            }

            target = default;
            return false;
        }

        // ================================================================ окно

        /// <summary>Поднято ли окно AppDeck в этом запуске (настройка не выключила).</summary>
        public static bool IsAvailable => _host != null;

        /// <summary>Окно открыто. Игре стоит не читать управление мышью/клавиатурой, пока это так.</summary>
        public static bool IsOpen => _host != null && _host.IsOpen;

        /// <summary>Фокус в поле ввода AppDeck — игре точно не читать клавиатуру.</summary>
        public static bool IsTyping => _host != null && _host.IsTyping;

        /// <summary>
        /// Игре не читать управление: окно открыто или закрылось в этом же кадре (та же клавиша Escape
        /// не должна сразу открыть меню игры). Удобная единая проверка для обработчиков ввода игры.
        /// </summary>
        public static bool BlocksInput => _host != null && (_host.IsOpen || _host.ClosedThisFrame);

        public static event Action<bool> OpenChanged;

        internal static void RaiseOpenChanged(bool open)
        {
            try { OpenChanged?.Invoke(open); }
            catch (Exception e) { Debug.LogException(e); }
        }

        public static void Open() => _host?.SetOpen(true);

        public static void Close() => _host?.SetOpen(false);

        public static void Toggle() => _host?.SetOpen(!IsOpen);

        /// <summary>Размер окна: шторка или весь экран.</summary>
        public static DeckLayout Layout
        {
            get => _host != null ? _host.Layout : DeckLayout.Partial;
            set => _host?.SetLayout(value);
        }

        /// <summary>
        /// Подставить аргумент в строку ввода консоли извне (выбор сущности в мире, кнопка, инструмент):
        /// каретка внутри слова — слово заменяется, иначе вставляется у каретки с пробелами. Окно не открывает.
        /// false — окна нет или консоль ещё не построена.
        /// </summary>
        public static bool InsertArgument(string text) => _host != null && _host.InsertArgument(text);

        /// <summary>
        /// Точка экрана над окном AppDeck (пиксели, начало слева снизу). Нужна тем, кто сам ловит клики по миру
        /// при открытом окне: клик по окну — не по миру.
        /// </summary>
        public static bool IsOverWindow(Vector2 screenPosition) => _host != null && _host.IsOverWindow(screenPosition);

        /// <summary>
        /// Верхний элемент панели AppDeck под точкой экрана (пиксели, начало слева снизу) или null. Для тех, кто
        /// сам решает, что под мышью (свой курсор с подсветкой кнопок): окно AppDeck выше всех панелей игры.
        /// </summary>
        public static VisualElement PickAt(Vector2 screenPosition) => _host?.PickAt(screenPosition);

        /// <summary>
        /// Системную стрелку прячет проект (рисует свой курсор): открытое окно освобождает курсор, но не делает
        /// его видимым. По умолчанию false — окно показывает стрелку, как раньше.
        /// </summary>
        public static bool HardwareCursorHidden { get; set; }

        /// <summary>Открыть окно на вкладке.</summary>
        public static void ShowTab(string tabId)
        {
            _host?.ShowTab(tabId);
            Open();
        }

        // ================================================================ вкладки

        /// <summary>Добавить вкладку. Вёрстка вкладки строится при первом показе.</summary>
        public static void AddTab(DeckTab tab, object owner = null)
        {
            if (tab == null) throw new ArgumentNullException(nameof(tab));

            for (var i = 0; i < _tabs.Count; i++)
            {
                if (!string.Equals(_tabs[i].Id, tab.Id, StringComparison.Ordinal)) continue;

                RemoveTabAt(i);
                break;
            }

            int index = 0;
            while (index < _tabs.Count && _tabs[index].Order <= tab.Order) index++;

            _tabs.Insert(index, tab);
            _tabOwners.Insert(index, owner);
            _tabsVersion++;
        }

        public static bool RemoveTab(string tabId)
        {
            for (var i = 0; i < _tabs.Count; i++)
            {
                if (!string.Equals(_tabs[i].Id, tabId, StringComparison.Ordinal)) continue;

                RemoveTabAt(i);
                return true;
            }

            return false;
        }

        private static void RemoveTabAt(int index)
        {
            DeckTab tab = _tabs[index];
            _tabs.RemoveAt(index);
            _tabOwners.RemoveAt(index);
            _tabsVersion++;
            _host?.OnTabRemoved(tab);
        }

        internal static IReadOnlyList<DeckTab> Tabs => _tabs;

        internal static int TabsVersion => _tabsVersion;

        internal static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;
    }
}
