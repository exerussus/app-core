using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using App.Abstractions;
using App.Services.Navigator;
using App.Core;
using AppCore.Runtime.Core.InternalServices.Manipulators.Audio;
using AppCore.Runtime.Core.InternalServices.Manipulators.Signal;
using AppCore.Runtime.Core.Models;
using Cysharp.Threading.Tasks;
using Exerussus.DI;
using log4net.Core;
using UnityEngine;
using UnityEngine.UIElements;
using AppPopup = App.Core.AppPopup;
using Object = UnityEngine.Object;

namespace App
{
    /// <summary>
    /// Корневой раннер приложения.
    /// </summary>
    /// <remarks>
    /// Отвечает за три зоны ответственности:
    /// <list type="number">
    ///   <item><description>Инициализация всех стартовых систем и сборка контейнера зависимостей (<see cref="Awake"/>).</description></item>
    ///   <item><description>Навигация между страницами (<see cref="AppPage"/>) с поддержкой экрана загрузки.</description></item>
    ///   <item><description>Управление стеком попапов (<see cref="AppPopup"/>).</description></item>
    /// </list>
    /// Все переходы являются асинхронными и защищены флагами блокировки,
    /// чтобы исключить одновременные конкурирующие переходы.
    /// <para>
    /// Для безопасного вызова событий из произвольного потока используется встроенный
    /// диспатчер главного потока (см. <see cref="RunOnMainThreadAsync"/>): действия
    /// складываются в потокобезопасную очередь и выполняются в <see cref="Update"/>.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public class AppRunner : MonoBehaviour
    {
        // ─── Inspector fields ────────────────────────────────────────────────

        [Tooltip("Настройки навигации (PageUID/PopupUID и их генерация). ОБЯЗАТЕЛЕН: без него AppRunner не стартует.")]
        [SerializeField] private NavigationSetting navigationSetting;

        [Tooltip("Реестр внешних (проектных) сервисов приложения. Необязателен: если пуст, поднимаются только внутренние сервисы.")]
        [SerializeField] private AppServiceRegister appServiceRegister;

        [Tooltip("Адаптер UI-звуков. Необязателен: если не задан, в контейнер зависимостей не регистрируется, а страницы работают без звука.")]
        [SerializeField] private SoundAdapter soundAdapter;

        [Tooltip("Адаптер ввода. Необязателен: если не задан, в контейнер зависимостей не регистрируется.")]
        [SerializeField] private InputAdapter inputAdapter;

        /// <summary>
        /// Экран загрузки, отображаемый при переходах между страницами.
        /// Поле необязательное: если экран не назначен (<see cref="_hasScreen"/> = <c>false</c>),
        /// вся логика показа/скрытия экрана превращается в no-op, а приложение работает без него.
        /// </summary>
        [Tooltip("Экран загрузки для переходов между страницами. НЕОБЯЗАТЕЛЕН: если поле пустое, весь код показа/скрытия экрана пропускается (no-op), приложение работает как есть.")]
        [SerializeField] private LoadingScreen screen;

        /// <summary>
        /// Опциональный бутстраппер. Если задан — его <see cref="AppBootstrapper.PreInitialize"/>
        /// вызывается перед инициализацией сервисов, а <see cref="AppBootstrapper.PostInitialize"/>
        /// — после. Поле может быть <c>null</c>.
        /// </summary>
        [Tooltip("Опциональный бутстраппер: PreInitialize вызывается до инициализации сервисов, PostInitialize — после. Можно оставить пустым.")]
        [SerializeField] private AppBootstrapper bootstrapper;
        
        [Tooltip("Опциональная библиотека звуков: при назначении реализует проигрыш звук на страницах через uss классы. Можно оставить пустым.")]
        [SerializeField] private UISoundLibrary uiSoundLibrary;
        
        /// <summary>
        /// Объекты, которые будут зарегистрированы в контейнере зависимостей как общие сервисы.
        /// Если объект реализует <see cref="IInitializable"/>, его метод
        /// <see cref="IInitializable.Initialize"/> будет вызван сразу после регистрации.
        /// </summary>
        [Tooltip("Общие объекты, регистрируемые в контейнере зависимостей как сервисы. Необязателен: можно оставить пустым.")]
        [SerializeField] private Object[] sharedObjects;
        
        // ─── Private state ───────────────────────────────────────────────────

        /// <summary>
        /// Все страницы приложения. Первый элемент массива считается страницей по умолчанию
        /// и открывается автоматически при старте.
        /// </summary>
        private AppPage[] allPages;

        /// <summary>Все попапы приложения, доступные для открытия через <see cref="OpenPopup"/> и <see cref="SwitchPopup"/>.</summary>
        private AppPopup[] allPopups;

        private UIDocument _document;
        private VisualElement _loadingScreen;
        private VisualElement _pagesLayer;
        private VisualElement _popupsLayer;

        /// <summary>Контейнер инверсии зависимостей, хранящий все зарегистрированные сервисы.</summary>
        private DependenciesContainer _container;

        private bool _hasUpdatable;
        private IAppServiceUpdate[] _updatableServices;
        private IAppService[] _services;

        internal IAppManipulatorBuilder[] _appManipulatorBuilders;
        
        /// <summary>
        /// Страница, переход на которую отложен на время текущей навигации.
        /// Заполняется через <see cref="SwitchToPage"/> с <c>ignoreIfBusy = false</c>
        /// и обрабатывается циклом в <see cref="NavigateTo"/> сразу после завершения
        /// текущего перехода — без скрытия экрана загрузки между переходами.
        /// Перезаписывается при каждом новом отложенном запросе (выигрывает последний).
        /// </summary>
        private AppPage _pendingPage;

        /// <summary>Флаг <c>withScreen</c> отложенного запроса, привязанный к <see cref="_pendingPage"/>.</summary>
        private bool _pendingWithScreen;

        /// <summary>Признак наличия отложенного запроса в <see cref="_pendingPage"/>.</summary>
        private bool _hasPendingPage;

        /// <summary>
        /// Флаг, блокирующий одновременное выполнение нескольких переходов между попапами.
        /// Устанавливается в <c>true</c> на время асинхронных операций с попапами.
        /// </summary>
        private bool _isChangingPopup;

        /// <summary>Текущая активная страница. <c>null</c> до первого перехода.</summary>
        private AppPage _currentPage;

        /// <summary>Страница, открываемая при старте приложения (первый элемент <see cref="allPages"/>).</summary>
        private AppPage _defaultPage;

        /// <summary>
        /// Флаг принудительного удержания экрана загрузки.
        /// Пока <c>true</c> — экран не скрывается по завершении навигации.
        /// Управляется через <see cref="SetForceScreen"/>.
        /// </summary>
        private bool _isForceScreen;
        
        /// <summary>
        /// Есть ли у раннера экран загрузки. Вычисляется один раз в <see cref="Awake"/>
        /// как <c>screen != null</c>. Если экран не назначен в инспекторе — весь код
        /// показа/скрытия экрана становится no-op, а приложение продолжает работать без него.
        /// </summary>
        private bool _hasScreen;

        /// <summary>
        /// Флаг, блокирующий одновременное выполнение нескольких переходов между страницами.
        /// Устанавливается в <c>true</c> на время асинхронных операций навигации.
        /// </summary>
        private bool _isChangingPage;

        private readonly PayloadBuilder _payloadBuilder = new();

        /// <summary>
        /// Стек открытых попапов. Верхний элемент — текущий активный попап,
        /// получающий фокус ввода.
        /// </summary>
        private readonly Stack<AppPopup> _popupStack = new();

        /// <summary>Словарь для быстрого поиска попапа по типу.</summary>
        private readonly Dictionary<PopupUID, AppPopup> _popupsDict = new();

        /// <summary>Словарь для быстрого поиска страницы по типу.</summary>
        private readonly Dictionary<PageUID, AppPage> _pagesDict = new();

        private readonly BoundedStack<PageUID> _pageTransitionStack = new(8);
        
        // ─── Main thread dispatcher ──────────────────────────────────────────

        /// <summary>
        /// Идентификатор главного потока Unity, зафиксированный в <see cref="Awake"/>.
        /// Используется для определения, нужно ли перенаправлять действие в очередь
        /// или допустимо выполнить его синхронно.
        /// </summary>
        private int _mainThreadId;

        /// <summary>
        /// Очередь действий, которые должны быть выполнены в главном потоке.
        /// Опустошается в <see cref="Update"/> до выполнения логики обновляемых сервисов.
        /// </summary>
        private readonly Queue<Action> _mainThreadQueue = new();

        /// <summary>Объект-замок для синхронизированного доступа к <see cref="_mainThreadQueue"/>.</summary>
        private readonly object _mainThreadQueueLock = new();

        /// <summary>
        /// Флаг завершения работы компонента. Устанавливается в <see cref="OnDestroy"/>,
        /// после чего постановка новых действий в очередь главного потока становится бессмысленной
        /// и приводит к немедленному завершению ожидающих <see cref="UniTask"/> с отменой.
        /// </summary>
        private bool _isDestroyed;

        // ─── Events ──────────────────────────────────────────────────────────
        
        /// <summary>
        /// Вызывается один раз при первом монтировании страницы в дерево UI:
        /// передаёт её <see cref="PageUID"/> и корневой <see cref="VisualElement"/>.
        /// </summary>
        public event Action<PageUID, VisualElement> OnPageMounted;
        
        /// <summary>
        /// Вызывается после завершения перехода на новую страницу.
        /// </summary>
        /// <remarks>
        /// Событие гарантированно вызывается в главном потоке Unity.
        /// Все исключения из подписчиков перехватываются и логируются,
        /// не прерывая вызов остальных подписчиков.
        /// </remarks>
        public event Action<(PageUID prev, PageUID current)> OnPageChanged;

        /// <summary>
        /// Дополнительное событие смены страницы, вызываемое сразу после <see cref="OnPageChanged"/>
        /// в том же кадре и в главном потоке. Удобно, когда нужен порядок «сначала базовые
        /// подписчики, затем пост-обработчики».
        /// </summary>
        public event Action<(PageUID prev, PageUID current)> OnPagePostChanged;

        /// <summary>
        /// Вызывается после открытия попапа.
        /// </summary>
        /// <remarks>
        /// Событие гарантированно вызывается в главном потоке Unity.
        /// Все исключения из подписчиков перехватываются и логируются.
        /// </remarks>
        public event Action<PopupUID> OnPopupOpened;

        /// <summary>
        /// Вызывается после закрытия попапа.
        /// </summary>
        /// <remarks>
        /// Событие гарантированно вызывается в главном потоке Unity.
        /// Все исключения из подписчиков перехватываются и логируются.
        /// </remarks>
        public event Action<PopupUID> OnPopupClosed;

        // ─── Event invokers ──────────────────────────────────────────────────

        /// <summary>
        /// Безопасно вызывает событие <see cref="OnPageChanged"/>.
        /// Исключения из подписчиков перехватываются и логируются через <see cref="LogException"/>.
        /// </summary>
        private void InvokePageChanged(PageUID from, PageUID to)
        {
            try
            {
                OnPageChanged?.Invoke((from, to));
                OnPagePostChanged?.Invoke((from, to));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Безопасно вызывает событие <see cref="OnPopupOpened"/>.
        /// Исключения из подписчиков перехватываются и логируются через <see cref="LogException"/>.
        /// </summary>
        private void InvokePopupOpened(PopupUID popupUid)
        {
            try
            {
                OnPopupOpened?.Invoke(popupUid);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Безопасно вызывает событие <see cref="OnPopupClosed"/>.
        /// Исключения из подписчиков перехватываются и логируются через <see cref="LogException"/>.
        /// </summary>
        /// <param name="popupType">Тип попапа, который был закрыт.</param>
        private void InvokePopupClosed(PopupUID popupUid)
        {
            try
            {
                OnPopupClosed?.Invoke(popupUid);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // ─── Unity lifecycle ─────────────────────────────────────────────────

        private void Awake()
        {
            if (navigationSetting == null)
            {
                Debug.LogError($"Navigation settings is null. Please, set NavigationSetting asset.");
                return;
            }

            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            // Экран загрузки опционален: фиксируем его наличие один раз.
            // Если поле пустое — весь код показа/скрытия экрана станет no-op.
            _hasScreen = screen != null;
            
            SetupUiLayers();
            
            if (navigationSetting != null) NavigationLink.Initialize(navigationSetting);
            
            _bootCts = new CancellationTokenSource();
            
            _container = new();
            _container.Add(this);
            _container.Add(_container);

            if (soundAdapter != null) _container.Add(typeof(SoundAdapter), soundAdapter);
            if (uiSoundLibrary != null) _container.Add(uiSoundLibrary);
            if (inputAdapter != null) _container.Add(typeof(InputAdapter), inputAdapter);
            
            allPages = GetComponentsInChildren<AppPage>();
            allPopups = GetComponentsInChildren<AppPopup>();

            if (allPages.Length == 0)
            {
                Debug.LogError($"App Runner has no child pages. Assign at least one page below it in the hierarchy for it to work.");
                return;
            }
            
            _defaultPage = allPages[0];

            foreach (var page in allPages) page.gameObject.SetActive(false);
            foreach (var popup in allPopups) popup.gameObject.SetActive(false);
            
            // Стартуем машину. Дальше всё движется через Update.
            TransitionTo(BootState.CoveringScreen);
        }

        /// <summary>Создаёт и монтирует UI-слои. Чисто синхронная подготовка до старта машины.</summary>
        private void SetupUiLayers()
        {
            _document = GetComponent<UIDocument>();
            var root = _document.rootVisualElement;

            _loadingScreen = new VisualElement { name = "loadingScreenLayer" };
            _loadingScreen.style.position = Position.Absolute;
            _loadingScreen.style.left = 0;
            _loadingScreen.style.right = 0;
            _loadingScreen.style.top = 0;
            _loadingScreen.style.bottom = 0;
            _loadingScreen.style.flexGrow = 1;
            _loadingScreen.pickingMode = PickingMode.Ignore;

            _pagesLayer = new VisualElement { name = "pagesLayer" };
            _pagesLayer.style.position = Position.Absolute;
            _pagesLayer.style.left = 0;
            _pagesLayer.style.right = 0;
            _pagesLayer.style.top = 0;
            _pagesLayer.style.bottom = 0;
            _pagesLayer.style.flexGrow = 1;
            _pagesLayer.pickingMode = PickingMode.Ignore;

            _popupsLayer = new VisualElement { name = "popupsLayer" };
            _popupsLayer.style.position = Position.Absolute;
            _popupsLayer.style.left = _popupsLayer.style.right = 0;
            _popupsLayer.style.top = _popupsLayer.style.bottom = 0;
            _popupsLayer.pickingMode = PickingMode.Ignore;

            _loadingScreen.BringToFront();

            root.Add(_pagesLayer);
            root.Add(_popupsLayer);
            root.Add(_loadingScreen);
        }

        // /// <summary>
        // /// Запускает переход на страницу по умолчанию с имитацией экрана загрузки длительностью 1 секунду.
        // /// </summary>
        // private void Start()
        // {
        //     SwitchWithFakeLoading(_defaultPage.PageType, 1f).Forget(Debug.LogException);
        // }

        /// <summary>
        /// Очищает контекст приложения при уничтожении объекта в редакторе.
        /// </summary>
        /// <remarks>
        /// Дополнительно отменяет все ожидающие операции в очереди главного потока,
        /// чтобы привязанные к ним <see cref="UniTask"/> завершились с отменой,
        /// а не зависли навсегда.
        /// </remarks>
        private void OnDestroy()
        {
            _isDestroyed = true;

            if (_bootCts != null)
            {
                _bootCts.Cancel();
                _bootCts.Dispose();
                _bootCts = null;
            }

            DrainMainThreadQueueOnDestroy();

            if (_services != null) foreach (var service in _services) service.Destroy();
        }

        private void Update()
        {
            PumpMainThreadQueue();

            TickBootStateMachine();

            // Сервисы тикают только после полной инициализации.
            if (_bootState != BootState.Ready) return;

            if (!_hasUpdatable) return;
            foreach (var updatableService in _updatableServices) updatableService.Update();
        }
        
        /// <summary>
        /// Опрашивает флаг <see cref="_bootStepCompleted"/> и переводит машину в следующий стэйт,
        /// если шаг завершён. За один тик выполняется не более одного перехода.
        /// </summary>
        private void TickBootStateMachine()
        {
            if (_bootState == BootState.NotStarted || _bootState == BootState.Ready) return;
            if (!_bootStepCompleted) return;
            if (_bootStepFailed) return; // встали на ошибке — экран остаётся, дальше не идём

            switch (_bootState)
            {
                case BootState.CoveringScreen: TransitionTo(BootState.PreBootstrap); break;
                case BootState.PreBootstrap: TransitionTo(BootState.InitializingCore); break;
                case BootState.InitializingCore: TransitionTo(BootState.PostBootstrap); break;
                case BootState.PostBootstrap: TransitionTo(BootState.Ready); break;
            }
        }

        /// <summary>
        /// Переводит машину в указанный стэйт, сбрасывает флаги и запускает работу шага.
        /// </summary>
        private void TransitionTo(BootState state)
        {
            _bootState = state;
            _bootStepCompleted = false;
            _bootStepFailed = false;

            switch (state)
            {
                case BootState.CoveringScreen: StartCoverScreenStep(); break;
                case BootState.PreBootstrap: StartPreBootstrapStep(); break;
                case BootState.InitializingCore: StartInitializeCoreStep(); break;
                case BootState.PostBootstrap: StartPostBootstrapStep(); break;
                case BootState.Ready: StartReadyStep(); break;
            }
        }

        private void StartCoverScreenStep()
        {
            // Первый показ — мгновенный (без фейда). Если экран не назначен, ShowScreen вернёт
            // завершённую задачу, и шаг просто мгновенно считается выполненным.
            RunAsyncStep(token => ShowScreen(true));
        }

        private void StartPreBootstrapStep()
        {
            if (bootstrapper == null)
            {
                _bootStepCompleted = true;
                return;
            }

            RunAsyncStep(token => bootstrapper.PreInitialize(_container, token));
        }

        private void StartInitializeCoreStep()
        {
            // Синхронный шаг — выполняем здесь же и сразу сигналим о завершении.
            try
            {
                InitializeCoreSync();
                _bootStepCompleted = true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _bootStepFailed = true;
                _bootStepCompleted = true;
            }
        }

        private void StartPostBootstrapStep()
        {
            if (bootstrapper == null)
            {
                _bootStepCompleted = true;
                return;
            }

            RunAsyncStep(token => bootstrapper.PostInitialize(_container, token));
        }

        private void StartReadyStep()
        {
            // Финальный стэйт: запускаем навигацию на дефолтную страницу.
            // NavigateTo сам снимет экран загрузки в конце (если не выставлен _isForceScreen).
            NavigateTo(_defaultPage).Forget(Debug.LogException);
        }

        /// <summary>
        /// Запускает асинхронный шаг и по его завершении выставляет <see cref="_bootStepCompleted"/>.
        /// Само выставление флага происходит на главном потоке (UniTask продолжается в нём),
        /// поэтому <see cref="TickBootStateMachine"/> увидит его в ближайшем <see cref="Update"/>.
        /// </summary>
        private void RunAsyncStep(Func<CancellationToken, UniTask> stepFactory)
        {
            RunAsyncStepInternal(stepFactory).Forget();
        }

        private async UniTaskVoid RunAsyncStepInternal(Func<CancellationToken, UniTask> stepFactory)
        {
            var token = _bootCts.Token;
            try
            {
                await stepFactory(token);
                if (_isDestroyed) return;
                _bootStepCompleted = true;
            }
            catch (OperationCanceledException)
            {
                // OnDestroy дернул CTS — выходим тихо.
            }
            catch (Exception e)
            {
                if (_isDestroyed) return;
                Debug.LogException(e);
                _bootStepFailed = true;
                _bootStepCompleted = true;
            }
        }

        /// <summary>
        /// Синхронная инициализация: контейнер, сервисы, страницы, попапы.
        /// Соответствует прежнему телу <see cref="Awake"/> после фиксации главпотока и UI-слоёв.
        /// </summary>
        private void InitializeCoreSync()
        {
            if (sharedObjects is { Length: >0 })
            {
                foreach (var sharedObject in sharedObjects) _container.Add(sharedObject);
            }

            var internalServices = InternalServicesRegister.GetAllServices();
            if (appServiceRegister != null)
            {
                var externalServices = appServiceRegister.GetAllServices();
                _services = new IAppService[internalServices.Length + externalServices.Length];
                
                var index = 0;
                
                for (; index < internalServices.Length; index++)
                {
                    var service = internalServices[index];
                    _services[index] = service;
                }

                for (var i = 0; i < externalServices.Length; i++)
                {
                    var service = externalServices[i];
                    _services[index] = service;
                    index++;
                }
                
            }
            else
            {
                _services = InternalServicesRegister.GetAllServices();
            }
            
            _updatableServices = _services.OfType<IAppServiceUpdate>().ToArray();
            _appManipulatorBuilders = _services.OfType<IAppManipulatorBuilder>().ToArray();
            _hasUpdatable = _updatableServices.Length > 0;

            foreach (var service in _services) _container.Add(service);
            foreach (var service in _services) _container.TryProvideFields(service);
            foreach (var service in _services) service.OnInject(_container);
            foreach (var service in _services) _container.TryInjectFields(service);
            foreach (var service in _services) service.Initialize();

            foreach (var page in allPages) page.gameObject.SetActive(false);
            foreach (var page in allPages) page.AppRunner = this;
            foreach (var page in allPages) page.Mount(_pagesLayer);
            foreach (var page in allPages) page.PreInitialize();
            foreach (var popup in allPopups) popup.PreInitialize();
            foreach (var page in allPages) _pagesDict.Add(page.PageUid, page);
            foreach (var popup in allPopups) _popupsDict.Add(popup.PopupUid, popup);
            foreach (var page in allPages) if (page.HasController) _container.TryInjectFields(page.Controller);
            foreach (var popup in allPopups) _container.TryInjectFields(popup);
            foreach (var page in allPages) page.Controller?.Initialize();
            foreach (var popup in allPopups) if (popup.HasController) popup.Controller.Initialize();
        }

        /// <summary>
        /// Выполняет все накопленные в <see cref="_mainThreadQueue"/> действия.
        /// </summary>
        /// <remarks>
        /// Снимок очереди берётся под локом, исполнение происходит вне лока —
        /// это позволяет действиям безопасно ставить в очередь новые задачи
        /// (они будут обработаны на следующем кадре).
        /// Исключения из отдельного действия логируются и не прерывают
        /// обработку остальных элементов снимка.
        /// </remarks>
        private void PumpMainThreadQueue()
        {
            // Быстрая проверка без лока — типичный путь, когда очередь пуста.
            if (_mainThreadQueue.Count == 0) return;

            Action[] snapshot;
            lock (_mainThreadQueueLock)
            {
                if (_mainThreadQueue.Count == 0) return;
                snapshot = _mainThreadQueue.ToArray();
                _mainThreadQueue.Clear();
            }

            for (var i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Аналог <see cref="PumpMainThreadQueue"/>, вызываемый из <see cref="OnDestroy"/>.
        /// Гарантирует, что висящие <see cref="UniTaskCompletionSource"/> завершатся
        /// (по отмене), а не оставят awaiters навсегда заблокированными.
        /// </summary>
        private void DrainMainThreadQueueOnDestroy()
        {
            Action[] snapshot;
            lock (_mainThreadQueueLock)
            {
                if (_mainThreadQueue.Count == 0) return;
                snapshot = _mainThreadQueue.ToArray();
                _mainThreadQueue.Clear();
            }

            for (var i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        // ─── Loading screen (опционален) ─────────────────────────────────────

        /// <summary>
        /// Виден ли сейчас экран загрузки. Всегда <c>false</c>, если экран не назначен
        /// (см. <see cref="_hasScreen"/>).
        /// </summary>
        private bool IsScreenVisible => _hasScreen && screen.IsVisible;

        /// <summary>
        /// Показывает экран загрузки, если он назначен; иначе — no-op
        /// (возвращает <see cref="UniTask.CompletedTask"/>).
        /// </summary>
        /// <param name="instant">Если <c>true</c> — без анимации появления.</param>
        private UniTask ShowScreen(bool instant = false)
            => _hasScreen ? screen.Show(_loadingScreen, instant) : UniTask.CompletedTask;

        /// <summary>
        /// Скрывает экран загрузки, если он назначен; иначе — no-op
        /// (возвращает <see cref="UniTask.CompletedTask"/>).
        /// </summary>
        private UniTask HideScreen()
            => _hasScreen ? screen.Hide() : UniTask.CompletedTask;

        // ─── Public API ──────────────────────────────────────────────────────
        
        /// <summary>
        /// Назначает страницу по умолчанию — ту, что открывается при старте и через
        /// <see cref="SwitchToDefaultPage"/>.
        /// </summary>
        /// <remarks>Если страница с указанным <paramref name="pageUid"/> не найдена — вызов игнорируется с ошибкой в лог.</remarks>
        /// <param name="pageUid">Идентификатор страницы, которая станет стартовой.</param>
        public void SetDefaultPage(PageUID pageUid)
        {
            if (!_pagesDict.TryGetValue(pageUid, out var page))
            {
                Debug.LogError($"Page {pageUid} is not exist");
                return;
            }

            _defaultPage = page;
        }
        
        /// <summary>
        /// Принудительно удерживает экран загрузки поднятым независимо от навигации.
        /// </summary>
        /// <remarks>
        /// Пока включено — экран не скрывается по завершении переходов между страницами.
        /// Если экран загрузки не назначен (см. <see cref="_hasScreen"/>) — метод лишь
        /// запоминает флаг и ничего не показывает.
        /// </remarks>
        /// <param name="isEnabled"><c>true</c> — удерживать экран; <c>false</c> — разрешить его скрытие.</param>
        public void SetForceScreen(bool isEnabled)
        {
            _isForceScreen = isEnabled;

            // Экран опционален: держать/скрывать нечего.
            if (!_hasScreen) return;

            if (isEnabled)
            {
                if (!screen.gameObject.activeSelf)
                    screen.Show(_loadingScreen).Forget(Debug.LogException);
            }
            else
            {
                if (screen.gameObject.activeSelf && !_isChangingPage)
                    screen.Hide().SuppressCancellationThrow().Forget();
            }
        }
        
        /// <summary>
        /// Закрывает текущий верхний попап (если есть) и открывает указанный,
        /// помещая его на вершину стека.
        /// </summary>
        /// <remarks>Если попап с указанным <paramref name="popupUid"/> не найден — вызов игнорируется с ошибкой в лог.</remarks>
        /// <param name="popupUid">Идентификатор попапа, который нужно открыть.</param>
        public void SwitchPopup(PopupUID popupUid)
        {
            if (!_popupsDict.TryGetValue(popupUid, out var popup))
            {
                Debug.LogError($"Popup {popupUid} is not exist.");
                return;
            }

            SwitchPopupInternal(popup).Forget(Debug.LogException);
        }

        /// <summary>Проверяет, является ли указанная страница текущей активной.</summary>
        /// <param name="pageUid">Идентификатор проверяемой страницы.</param>
        /// <returns><c>true</c>, если страница сейчас активна.</returns>
        public bool IsActive(PageUID pageUid)
        {
            return _currentPage != null && _currentPage.PageUid == pageUid;
        }

        /// <summary>Проверяет, является ли текущая активная страница страницей по умолчанию.</summary>
        /// <returns><c>true</c>, если активна дефолтная страница.</returns>
        public bool IsActiveDefault()
        {
            return _currentPage != null && _currentPage == _defaultPage;
        }

        /// <summary>Проверяет, открыт ли указанный попап (находится ли он в стеке).</summary>
        /// <param name="popupType">Идентификатор проверяемого попапа.</param>
        /// <returns><c>true</c>, если попап присутствует в стеке открытых.</returns>
        public bool IsActive(PopupUID popupType)
        {
            foreach (var popup in _popupStack)
                if (popup.PopupUid == popupType)
                    return true;
            return false;
        }

        /// <summary>Проверяет, открыт ли хотя бы один попап.</summary>
        /// <returns><c>true</c>, если стек попапов не пуст.</returns>
        public bool IsActiveAnyPopup()
        {
            return _popupStack.Count > 0;
        }

        /// <summary>Открывает попап поверх текущего стека; новый попап получает фокус.</summary>
        /// <remarks>Если попап с указанным <paramref name="popupType"/> не найден — вызов игнорируется с ошибкой в лог.</remarks>
        /// <param name="popupType">Идентификатор попапа для открытия.</param>
        public void OpenPopup(PopupUID popupType)
        {
            if (!_popupsDict.TryGetValue(popupType, out var popup))
            {
                Debug.LogError($"Popup {popupType} is not exist.");
                return;
            }

            OpenPopupInternal(popup).Forget(Debug.LogException);
        }

        /// <summary>Закрывает верхний попап в стеке; фокус возвращается предыдущему попапу, если он есть.</summary>
        /// <remarks>Если стек попапов пуст — вызов игнорируется.</remarks>
        public void CloseActivePopup()
        {
            if (_popupStack.Count == 0) return;

            CloseActivePopupInternal().Forget(Debug.LogException);
        }

        /// <summary>Возвращается на предыдущую страницу из стека переходов.</summary>
        /// <remarks>Если стек переходов пуст — вызов игнорируется.</remarks>
        /// <param name="withScreen">Показывать ли экран загрузки на время перехода (при отсутствии экрана — игнорируется).</param>
        /// <param name="ignoreIfBusy">
        /// Если <c>true</c> — при уже идущей навигации запрос отбрасывается; если <c>false</c> —
        /// откладывается и проигрывается каскадом после текущего перехода.
        /// </param>
        public void SwitchToPrevPage(bool withScreen = false, bool ignoreIfBusy = true)
        {
            if (_pageTransitionStack.Count < 1) return;
            var pageUid = _pageTransitionStack.Pop();
            SwitchToPage(pageUid, withScreen, ignoreIfBusy);
        }
        
        /// <summary>Выполняет переход на указанную страницу.</summary>
        /// <remarks>
        /// Если навигация уже идёт, поведение зависит от <paramref name="ignoreIfBusy"/>:
        /// при <c>true</c> запрос отбрасывается, при <c>false</c> — откладывается и подхватывается
        /// каскадом сразу после завершения текущего перехода (без скрытия экрана между ними).
        /// Если страница с указанным <paramref name="pageUid"/> не найдена — вызов игнорируется с ошибкой в лог.
        /// </remarks>
        /// <param name="pageUid">Идентификатор целевой страницы.</param>
        /// <param name="withScreen">Показывать ли экран загрузки на время перехода (при отсутствии экрана — игнорируется).</param>
        /// <param name="ignoreIfBusy">Отбрасывать (<c>true</c>) или откладывать (<c>false</c>) запрос при идущей навигации.</param>
        public void SwitchToPage(PageUID pageUid, bool withScreen = false, bool ignoreIfBusy = true)
        {
            if (!_pagesDict.TryGetValue(pageUid, out var page))
            {
                Debug.LogError($"Page {pageUid.ToString()} is not exist.");
                return;
            }

            if (_isChangingPage)
            {
                if (ignoreIfBusy)
                {
                    Debug.LogWarning($"Page switch to {page} ignored: navigation in progress.");
                    return;
                }

                // Запоминаем отложенный переход — он будет подхвачен циклом в NavigateTo
                // сразу после завершения текущей навигации, без скрытия экрана между ними.
                _pendingPage = page;
                _pendingWithScreen = withScreen;
                _hasPendingPage = true;

                // Если новый запрос со скрином, а экран ещё не поднят — поднимаем его немедленно,
                // не дожидаясь конца текущей навигации. Без экрана ShowScreen — no-op.
                if (withScreen && !IsScreenVisible)
                    ShowScreen().Forget(Debug.LogException);

                return;
            }

            NavigateTo(page, withScreen).Forget(Debug.LogException);
        }

        /// <summary>Переходит на страницу по умолчанию (см. <see cref="SetDefaultPage"/>).</summary>
        /// <param name="withScreen">Показывать ли экран загрузки на время перехода (при отсутствии экрана — игнорируется).</param>
        /// <param name="ignoreIfBusy">Отбрасывать (<c>true</c>) или откладывать (<c>false</c>) запрос при идущей навигации.</param>
        public void SwitchToDefaultPage(bool withScreen = false, bool ignoreIfBusy = true)
        {
            SwitchToPage(_defaultPage.PageUid, withScreen, ignoreIfBusy);
        }

        // Internal

        internal void RegisterAppView(IAppView appView)
        {
            var soundLibrary = appView.OverrideSoundLibrary == null ? uiSoundLibrary : appView.OverrideSoundLibrary;
            
            if (soundLibrary != null) appView.Root.Query<Button>().ForEach(btn =>
            {
                if (_appManipulatorBuilders is { Length: > 0 })
                {
                    foreach (var builder in _appManipulatorBuilders)
                    {
                        builder.OnBuildButtonManipulator(appView, btn, _payloadBuilder);
                    }
                }
                
                if (btn.ClassListContains("signal-button")) btn.AddManipulator(new SignalClickManipulator(_payloadBuilder.End()));
            });
            
            foreach (var builder in _appManipulatorBuilders)
            {
                builder.OnBuildManipulators(appView);
            }
        }
        
        // ─── Main-thread marshaling ──────────────────────────────────────────

        /// <summary>
        /// Выполняет <paramref name="action"/> в главном потоке Unity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Поведение в зависимости от потока вызова:
        /// <list type="bullet">
        ///   <item><description>
        ///     Если вызов уже происходит в главном потоке — действие выполняется синхронно,
        ///     возвращается <see cref="UniTask.CompletedTask"/>. Это позволяет избежать
        ///     лишнего откладывания на следующий кадр в типовом сценарии.
        ///   </description></item>
        ///   <item><description>
        ///     Иначе действие ставится в потокобезопасную очередь и будет выполнено
        ///     ближайшим вызовом <see cref="Update"/>. Возвращённая <see cref="UniTask"/>
        ///     завершится после фактического выполнения действия.
        ///   </description></item>
        /// </list>
        /// </para>
        /// <para>
        /// Если компонент уже уничтожен (<see cref="_isDestroyed"/>), возвращается
        /// отменённая <see cref="UniTask"/> без постановки в очередь — это предотвращает
        /// «утечку» подвисших awaiters.
        /// </para>
        /// </remarks>
        /// <param name="action">Действие для выполнения в главном потоке. Не должно быть <c>null</c>.</param>
        /// <returns><see cref="UniTask"/>, завершающаяся после выполнения действия.</returns>
        private UniTask RunOnMainThreadAsync(Action action)
        {
            if (action == null) return UniTask.CompletedTask;

            if (_isDestroyed) return UniTask.FromCanceled();

            // Быстрый путь — мы уже в главном потоке: исполняем синхронно.
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                return UniTask.CompletedTask;
            }

            // Медленный путь — откладываем выполнение до ближайшего Update().
            var tcs = new UniTaskCompletionSource();
            lock (_mainThreadQueueLock)
            {
                if (_isDestroyed)
                {
                    tcs.TrySetCanceled();
                    return tcs.Task;
                }

                _mainThreadQueue.Enqueue(() =>
                {
                    if (_isDestroyed)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        action();
                        tcs.TrySetResult();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        tcs.TrySetException(e);
                    }
                });
            }

            return tcs.Task;
        }

        // ─── Boot state machine ──────────────────────────────────────────────

        /// <summary>Этапы инициализации приложения.</summary>
        private enum BootState
        {
            /// <summary>Машина ещё не запущена (до <see cref="Awake"/>).</summary>
            NotStarted,

            /// <summary>Поднимается экран загрузки. Страницы и сервисы ещё не тронуты.</summary>
            CoveringScreen,

            /// <summary>Выполняется <see cref="AppBootstrapper.PreInitialize"/>, если бутстраппер задан.</summary>
            PreBootstrap,

            /// <summary>Регистрация сервисов в DI и инициализация страниц/попапов.</summary>
            InitializingCore,

            /// <summary>Выполняется <see cref="AppBootstrapper.PostInitialize"/>, если бутстраппер задан.</summary>
            PostBootstrap,

            /// <summary>Финальное состояние. При входе снимается экран и происходит навигация на дефолтную страницу.</summary>
            Ready
        }

        /// <summary>Текущий этап загрузки.</summary>
        private BootState _bootState = BootState.NotStarted;

        /// <summary>
        /// Флаг завершения текущего шага. Выставляется в <c>true</c> асинхронной операцией
        /// (или сразу для синхронных шагов) и опрашивается в <see cref="Update"/>.
        /// Сбрасывается при входе в следующий стэйт.
        /// </summary>
        private bool _bootStepCompleted;

        /// <summary>Признак ошибки в текущем шаге; останавливает машину на месте.</summary>
        private bool _bootStepFailed;

        /// <summary>Источник токена отмены для асинхронных шагов; отменяется в <see cref="OnDestroy"/>.</summary>
        private CancellationTokenSource _bootCts;

        // ─── Private async logic ─────────────────────────────────────────────

        /// <summary>
        /// Внутренняя реализация переключения попапа: закрывает текущий верхний попап (если есть)
        /// и открывает новый, помещая его в стек.
        /// </summary>
        /// <remarks>
        /// Защищена флагом <see cref="_isChangingPopup"/>: если переход уже выполняется,
        /// новый вызов будет проигнорирован с предупреждением в лог.
        /// Флаг сбрасывается в блоке <c>finally</c>, гарантируя корректное
        /// состояние даже при возникновении исключений.
        /// </remarks>
        /// <param name="newPopup">Попап, который нужно открыть.</param>
        private async UniTask SwitchPopupInternal(AppPopup newPopup)
        {
            if (_isChangingPopup)
            {
                Debug.LogWarning($"Popup switch to {newPopup} ignored: popup transition in progress.");
                return;
            }

            _isChangingPopup = true;

            try
            {
                if (_popupStack.Count > 0)
                {
                    var current = _popupStack.Pop();

                    if (current.HasController)
                    {
                        await current.Controller.OnUnfocus();
                        await current.Controller.OnHide();
                        await current.Controller.OnDeactivate();
                    }
                    current.gameObject.SetActive(false);
                    var closedType = current.PopupUid;
                    await RunOnMainThreadAsync(() => InvokePopupClosed(closedType));
                }

                _popupStack.Push(newPopup);

                newPopup.gameObject.SetActive(true);
                
                if (newPopup.HasController)
                {
                    await newPopup.Controller.OnActivate();
                    await newPopup.Controller.OnShow();
                }
                
                var openedType = newPopup.PopupUid;
                await RunOnMainThreadAsync(() => InvokePopupOpened(openedType));
            }
            finally
            {
                _isChangingPopup = false;
            }

            if (newPopup.HasController)
            {
                newPopup.Controller.OnFocus().Forget();
            }
        }

        /// <summary>
        /// Выполняет переход на страницу с имитацией экрана загрузки заданной длительности.
        /// Используется при первом старте приложения.
        /// </summary>
        /// <remarks>
        /// Если экран загрузки не активен — сначала показывает его,
        /// затем выдерживает паузу <paramref name="duration"/> секунд,
        /// после чего выполняет переход через <see cref="SwitchToPage"/>.
        /// </remarks>
        private async UniTask SwitchWithFakeLoading(PageUID pageUid, float duration)
        {
            if (_hasScreen && !screen.gameObject.activeSelf) await screen.Show(_loadingScreen);
            await UniTask.Delay(TimeSpan.FromSeconds(duration));
            SwitchToPage(pageUid);
        }

        /// <summary>
        /// Внутренняя реализация открытия попапа поверх стека.
        /// Текущий верхний попап теряет фокус, новый получает фокус после открытия.
        /// </summary>
        /// <remarks>
        /// Защищена флагом <see cref="_isChangingPopup"/>: если переход уже выполняется,
        /// новый вызов будет проигнорирован с предупреждением в лог.
        /// Флаг сбрасывается в блоке <c>finally</c>.
        /// </remarks>
        /// <param name="popup">Попап для открытия.</param>
        private async UniTask OpenPopupInternal(AppPopup popup)
        {
            if (_isChangingPopup)
            {
                Debug.LogWarning($"Popup {popup} ignored: popup transition in progress.");
                return;
            }

            _isChangingPopup = true;

            try
            {
                if (_popupStack.Count > 0)
                {
                    var current = _popupStack.Peek();
                    
                    if (current.HasController)
                    {
                        await current.Controller.OnUnfocus();
                    }
                }

                _popupStack.Push(popup);

                popup.gameObject.SetActive(true);
                popup.Mount(_popupsLayer);
                
                if (popup.HasController)
                {
                    await popup.Controller.OnActivate();
                    await popup.Controller.OnShow();
                }
                
                var openedType = popup.PopupUid;
                await RunOnMainThreadAsync(() => InvokePopupOpened(openedType));
            }
            finally
            {
                _isChangingPopup = false;
            }

            if (popup.HasController)
            {
                popup.Controller.OnFocus().Forget();
            }
        }

        /// <summary>
        /// Внутренняя реализация закрытия верхнего попапа в стеке.
        /// После закрытия фокус передаётся предыдущему попапу в стеке, если он существует.
        /// </summary>
        /// <remarks>
        /// Если флаг <see cref="_isChangingPopup"/> установлен — вызов немедленно прерывается.
        /// Флаг сбрасывается в блоке <c>finally</c>.
        /// </remarks>
        private async UniTask CloseActivePopupInternal()
        {
            if (_isChangingPopup) return;

            _isChangingPopup = true;
            AppPopup previous = null;

            try
            {
                var popup = _popupStack.Pop();

                if (popup.HasController)
                {
                    await popup.Controller.OnUnfocus();
                    await popup.Controller.OnHide();
                    await popup.Controller.OnDeactivate();
                    popup.Unmount();
                }
                popup.gameObject.SetActive(false);
                var closedType = popup.PopupUid;
                await RunOnMainThreadAsync(() => InvokePopupClosed(closedType));

                if (_popupStack.Count > 0)
                {
                    previous = _popupStack.Peek();
                }
            }
            finally
            {
                _isChangingPopup = false;
            }

            if (previous != null)
            {
                if (previous.HasController)
                {
                    previous.Controller.OnFocus().Forget();
                }
            }
        }

        /// <summary>
        /// Принудительно закрывает все попапы в стеке.
        /// Верхний попап сначала теряет фокус, затем все попапы последовательно скрываются и деактивируются.
        /// </summary>
        /// <remarks>
        /// Вызывается автоматически перед каждым переходом между страницами в <see cref="NavigateTo"/>.
        /// Если стек пуст — вызов игнорируется.
        /// Флаг <see cref="_isChangingPopup"/> устанавливается на всё время операции
        /// и сбрасывается в блоке <c>finally</c>.
        /// </remarks>
        private async UniTask CloseAllPopups()
        {
            if (_popupStack.Count == 0)
                return;

            _isChangingPopup = true;

            try
            {
                var focusedPopup = _popupStack.Peek();
                
                if (focusedPopup.HasController)
                {
                    await focusedPopup.Controller.OnUnfocus();
                }

                while (_popupStack.Count > 0)
                {
                    var popup = _popupStack.Pop();
                    if (popup.HasController)
                    {
                        await popup.Controller.OnHide();
                        await popup.Controller.OnDeactivate();
                    }
                    popup.gameObject.SetActive(false);
                    var closedType = popup.PopupUid;
                    await RunOnMainThreadAsync(() => InvokePopupClosed(closedType));
                }
            }
            finally
            {
                _isChangingPopup = false;
            }
        }

        /// <summary>
        /// Пытается забрать отложенный запрос на переход, очищая слот.
        /// </summary>
        /// <remarks>
        /// Возвращает <c>false</c>, если слот пуст или если отложенная страница
        /// совпадает с текущей (в этом случае переход бессмысленен — слот всё равно очищается).
        /// </remarks>
        private bool TryTakePending(out AppPage page, out bool withScreen)
        {
            page = null;
            withScreen = false;

            if (!_hasPendingPage) return false;

            var next = _pendingPage;
            var nextScreen = _pendingWithScreen;

            _pendingPage = null;
            _pendingWithScreen = false;
            _hasPendingPage = false;

            if (next == null || next == _currentPage) return false;

            page = next;
            withScreen = nextScreen;
            return true;
        }

        /// <summary>
        /// Выполняет асинхронный переход на указанную страницу.
        /// </summary>
        /// <remarks>
        /// <para>Последовательность операций для одного перехода:</para>
        /// <list type="number">
        ///   <item><description>Проверка флага <see cref="_isChangingPage"/> — повторный вызов игнорируется.</description></item>
        ///   <item><description>Закрытие всех открытых попапов через <see cref="CloseAllPopups"/>.</description></item>
        ///   <item><description>Скрытие и деактивация текущей страницы.</description></item>
        ///   <item><description>Опциональный показ экрана загрузки при <c>useScreen</c> = <c>true</c>.</description></item>
        ///   <item><description>Активация и показ новой страницы.</description></item>
        ///   <item><description>Вызов события <see cref="OnPageChanged"/> в главном потоке.</description></item>
        /// </list>
        /// <para>
        /// После завершения перехода проверяется слот <see cref="_hasPendingPage"/>:
        /// если в нём есть запрос, цикл повторяется уже с новой целью, причём
        /// экран загрузки <b>не скрывается между итерациями</b> — это даёт плавный
        /// каскад «грузим A → пришёл запрос на B → дожимаем A → без выключения экрана уходим в B».
        /// Скрытие экрана происходит только когда отложенных запросов больше нет.
        /// </para>
        /// <para>
        /// Флаг <see cref="_isChangingPage"/> сбрасывается в блоке <c>finally</c>,
        /// что гарантирует корректное состояние даже при исключениях.
        /// </para>
        /// </remarks>
        /// <param name="page">Целевая страница для перехода.</param>
        /// <param name="withScreen">
        /// Если <c>true</c> — показывает экран загрузки на время перехода,
        /// даже если он не был активен на момент вызова.
        /// </param>
        private async UniTask NavigateTo(AppPage page, bool withScreen = false)
        {
            if (_isChangingPage)
            {
                Debug.LogWarning($"Page switch to {page} ignored: navigation in progress.");
                return;
            }

            if (page == _currentPage) return;

            _isChangingPage = true;
            try
            {
                var targetPage = page;
                var useScreen = withScreen;

                while (true)
                {
                    if (_popupStack.Count > 0) await CloseAllPopups();

                    if (_currentPage != null)
                    {
                        if (_currentPage.HasController)
                        {
                            await _currentPage.Controller.OnHide();
                        }

                        // Экран поднимаем только если он ещё не виден; в каскаде он уже
                        // может быть поднят (либо предыдущей итерацией, либо немедленным
                        // Show из SwitchToPage при отложенном запросе со скрином).
                        // Гард по _hasScreen важен: без экрана незачем выдерживать паузу 1с.
                        if (_hasScreen && useScreen && !IsScreenVisible)
                        {
                            await ShowScreen();
                            await UniTask.Delay(TimeSpan.FromSeconds(1f));
                        }

                        if (_currentPage.HasController)
                        {
                            await _currentPage.Controller.OnDeactivate();
                        }
                        _currentPage.gameObject.SetActive(false);
                        _currentPage.Unmount();
                    }

                    var prevPage = PageUID.None;
                    
                    if (_currentPage != null)
                    {
                        prevPage = _currentPage.PageUid; 
                        _pageTransitionStack.Push(prevPage);
                    }
                    
                    _currentPage = targetPage;
                    var isNew = _currentPage.Mount(_pagesLayer);
                    if (isNew)
                    {
                        OnPageMounted?.Invoke(_currentPage.PageUid, _currentPage.Root);
                    }   
                    _currentPage.gameObject.SetActive(true);
                    await _currentPage.Activate();
                    
                    if (_currentPage.HasController)
                    {
                        await _currentPage.Controller.OnShow();
                    }

                    var changedType = _currentPage.PageUid;
                    await RunOnMainThreadAsync(() => InvokePageChanged(prevPage, changedType));

                    // Если за время перехода появился отложенный запрос — обрабатываем его
                    // прямо сейчас, не скрывая экран загрузки между итерациями.
                    if (TryTakePending(out var nextPage, out var nextWithScreen))
                    {
                        targetPage = nextPage;
                        useScreen = nextWithScreen;
                        continue;
                    }

                    // Отложенных запросов нет — пытаемся скрыть экран...
                    if (!_isForceScreen && IsScreenVisible)
                        await HideScreen().SuppressCancellationThrow();

                    // ...но во время await Hide мог прилететь новый отложенный запрос.
                    // Если так — снова запускаем итерацию.
                    if (TryTakePending(out nextPage, out nextWithScreen))
                    {
                        targetPage = nextPage;
                        useScreen = nextWithScreen;
                        continue;
                    }

                    break;
                }
            }
            finally
            {
                _isChangingPage = false;
                if (!_isForceScreen && IsScreenVisible)
                    await HideScreen().SuppressCancellationThrow();
            }
        }
    }
}