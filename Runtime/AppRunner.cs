using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Exerussus.DI;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
using Exerussus.AppCore.Boot;
using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Views;
using Exerussus.AppCore.Screens;
using Exerussus.AppCore.Services;
using Exerussus.AppCore.Audio;
using Exerussus.AppCore.Input;
using Exerussus.AppCore.Layout;
using Object = UnityEngine.Object;

namespace Exerussus.AppCore
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
    [RequireComponent(typeof(PanelRenderer))]
    public partial class AppRunner : MonoBehaviour
    {
        [Tooltip("Настройки навигации (PageId/PopupId и их генерация). ОБЯЗАТЕЛЕН: без него AppRunner не стартует.")]
        [FormerlySerializedAs("navigationSetting")]
        [SerializeField] private NavigationSettings navigationSettings;

        [Tooltip("Реестр внешних (проектных) сервисов приложения. Необязателен: если пуст, поднимаются только внутренние сервисы.")]
        [FormerlySerializedAs("appServiceRegister")]
        [SerializeField] private AppServiceRegistry appServiceRegistry;

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
        [FormerlySerializedAs("screen")]
        [SerializeField] private LoadingScreen loadingScreen;

        [Tooltip("Скрин закрываемой ошибки. Самодостаточен, доступен на любом этапе бута. НЕОБЯЗАТЕЛЕН.")]
        [SerializeField] private ErrorScreen errorScreen;

        [Tooltip("Скрин критического сбоя (reboot/quit). Показывается ядром на Failed. НЕОБЯЗАТЕЛЕН: без него фолбэком служит код-оверлей самого скрина, а если и он не назначен — голый оверлей на слое скринов.")]
        [SerializeField] private CriticalScreen criticalScreen;

        [Tooltip("Таймаут одного асинхронного шага бута в секундах (watchdog). 0 = выключено. По истечении — Failed с причиной «шаг завис».")]
        [SerializeField] private float stepTimeoutSeconds;

        [Tooltip("Показывать ли кнопку перезапуска на критическом скрине при падении бута.")]
        [SerializeField] private bool allowRebootOnFail = true;

        /// <summary>
        /// Опциональный бутстраппер. Если задан — его <see cref="AppBootstrapper.PreInitialize"/>
        /// вызывается перед инициализацией сервисов, а <see cref="AppBootstrapper.PostInitialize"/>
        /// — после. Поле может быть <c>null</c>.
        /// </summary>
        [Tooltip("Прогревать вёрстку всех страниц на старте. Выключено (по умолчанию) — страница " +
                 "инстанцирует свой UXML при первом переходе на неё: быстрее старт и меньше памяти, " +
                 "но первый переход чуть дороже.")]
        [SerializeField] private bool prewarmPages;

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

        [Tooltip("Держать интерфейс в референсной пропорции панели, когда окно шире неё: по бокам " +
                 "появляются чёрные поля, содержимое вжимается в центральную полосу. Пропорция берётся " +
                 "из Reference Resolution ассета Panel Settings — настраивать здесь нечего.")]
        [SerializeField] private bool frameToReferenceAspect = true;

        [Tooltip("Нижняя граница доли ширины, занятой кадром. Страховка от вырожденной полосы " +
                 "на экстремально широком окне.")]
        [SerializeField, Range(0.05f, 1f)] private float frameMinShare = 0.2f;

        [Tooltip("Цвет полей по бокам кадра.")]
        [SerializeField] private Color frameBarColor = Color.black;

        /// <summary>
        /// Все страницы приложения. Первый элемент массива считается страницей по умолчанию
        /// и открывается автоматически при старте.
        /// </summary>
        private AppPage[] allPages;

        /// <summary>Все попапы приложения, доступные для открытия через <see cref="OpenPopup"/> и <see cref="SwitchPopup"/>.</summary>
        private AppPopup[] allPopups;

        private PanelRenderer _panelRenderer;

        /// <summary>
        /// Подписан ли <see cref="OnUIReload"/>. Отписка в <see cref="OnDestroy"/> идёт под этим
        /// флагом: Awake может выйти раньше регистрации по невалидной конфигурации.
        /// </summary>
        private bool _uiCallbackRegistered;

        /// <summary>
        /// Построены ли слои. PanelRenderer дёргает колбэк повторно (OnEnable, LiveReload),
        /// а AppRunner строит слои и гоняет бут ровно один раз — как это делал UIDocument-путь.
        /// </summary>
        private bool _uiBuilt;

        /// <summary>Корень панели PanelRenderer. Используется как проба панели при расчёте безопасной зоны.</summary>
        private VisualElement _root;

        /// <summary>Слой скринов: поверх страниц и попапов. Loading/Error/Critical живут здесь.</summary>
        private VisualElement _screensLayer;

        private VisualElement _pagesLayer;

        private VisualElement _popupsLayer;

        /// <summary>Контейнер инверсии зависимостей, хранящий все зарегистрированные сервисы.</summary>
        private DependenciesContainer _container;

        private bool _hasUpdatable;

        private IAppServiceUpdate[] _updatableServices;

        private IAppService[] _services;

        /// <summary>
        /// Флаг завершения работы компонента. Устанавливается в <see cref="OnDestroy"/>,
        /// после чего постановка новых действий в очередь главного потока становится бессмысленной
        /// и приводит к немедленному завершению ожидающих <see cref="UniTask"/> с отменой.
        /// </summary>
        private bool _isDestroyed;

        private void Awake()
        {
            if (navigationSettings == null)
            {
                Debug.LogError($"Navigation settings is null. Please, set NavigationSettings asset.");
                return;
            }

            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            // Экран загрузки и скрины опциональны: фиксируем наличие один раз.
            // Если поле пустое — соответствующий код становится no-op.
            _hasScreen = loadingScreen != null;
            _hasErrorScreen = errorScreen != null;
            _hasCriticalScreen = criticalScreen != null;

            // Кэш расчёта статический и переживает перезагрузку сцены (и Play Mode без Domain
            // Reload). Сбрасываем, чтобы отступы пересчитались под панель именно этой сцены.
            ScreenMetrics.Invalidate();

            if (navigationSettings != null) NavigationLink.Initialize(navigationSettings);
            
            _bootCts = new CancellationTokenSource();
            
            // Логгер обязателен: без него политика Warn при конфликте ключей молчит в NullDiLogger,
            // и повторная регистрация типа затирает ссылку без единого следа в консоли.
            _container = new(logger: UnityDiLogger.Instance);
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

            // Дальше нужен корень панели, а PanelRenderer отдаёт его только колбэком — синхронного
            // rootVisualElement у него нет. Поэтому слои и старт бут-машины уезжают в OnUIReload.
            // Регистрация строго последней: до неё стоят ранние return по невалидной конфигурации,
            // и бут не должен стартовать на них.
            _panelRenderer = GetComponent<PanelRenderer>();

            // Порядок важен: сначала снимаем референс и подменяем настройки копией, и только
            // потом строим правило — иначе пропорция считалась бы по уже расширенному референсу.
            SetupFramePanelSettings();
            ScreenMetrics.Policy = ResolveFramePolicy();

            _panelRenderer.RegisterUIReloadCallback(OnUIReload);
            _uiCallbackRegistered = true;
        }

        /// <summary>
        /// Собирает правило обрезки кадра. Пропорция берётся из референса, снятого
        /// <c>SetupFramePanelSettings</c> ДО подмены настроек: читать его у панели повторно
        /// нельзя — там уже расширенная копия. Если референса нет или обрезка выключена,
        /// правило неактивно и полоса всегда во весь экран.
        /// </summary>
        private FramePolicy ResolveFramePolicy()
        {
            if (!frameToReferenceAspect) return FramePolicy.Disabled;
            if (_referenceResolution.x <= 0 || _referenceResolution.y <= 0) return FramePolicy.Disabled;

            return new FramePolicy((float)_referenceResolution.x / _referenceResolution.y, frameMinShare);
        }

        /// <summary>
        /// Корень панели готов. Вызывается на OnEnable и на каждом LiveReload вёрстки; строим
        /// ровно один раз — повторные вызовы отсекаются <see cref="_uiBuilt"/>.
        /// </summary>
        private void OnUIReload(PanelRenderer renderer, VisualElement root)
        {
            if (_uiBuilt || _isDestroyed || root == null) return;
            _uiBuilt = true;

            SetupUiLayers(root);

            // Стартуем машину. Дальше всё движется через Update.
            TransitionTo(BootState.CoveringScreen);
        }

        /// <summary>Создаёт и монтирует UI-слои. Чисто синхронная подготовка до старта машины.</summary>
        private void SetupUiLayers(VisualElement root)
        {
            _root = root;

            // Общий контейнер всех слоёв. Только он и двигается под кадр и безопасную зону —
            // страницы и попапы растянуты на 100% и получают вжатие даром.
            // Стартует во весь корень; ApplyScreenMetrics меняет ему left/right/top/bottom.
            // Именно смещение, а не padding: слои внутри абсолютные, а padding у абсолютных
            // детей не отсчитывается — они привязаны к padding box, внутри которого он и лежит.
            _contentRoot = new VisualElement { name = "contentRoot" };
            _contentRoot.style.position = Position.Absolute;
            _contentRoot.style.left = 0;
            _contentRoot.style.right = 0;
            _contentRoot.style.top = 0;
            _contentRoot.style.bottom = 0;
            _contentRoot.pickingMode = PickingMode.Ignore;

            _screensLayer = new VisualElement { name = "screensLayer" };
            _screensLayer.style.position = Position.Absolute;
            _screensLayer.style.left = 0;
            _screensLayer.style.right = 0;
            _screensLayer.style.top = 0;
            _screensLayer.style.bottom = 0;
            _screensLayer.style.flexGrow = 1;
            _screensLayer.pickingMode = PickingMode.Ignore;

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

            _contentRoot.Add(_pagesLayer);
            _contentRoot.Add(_popupsLayer);
            _contentRoot.Add(_screensLayer);

            // Слой скринов — самый верхний. Все скрины (Loading/Error/Critical) живут здесь.
            // Приоритет между ними держим порядком BringToFront при показе: Loading < Error < Critical.
            _screensLayer.BringToFront();

            root.Add(_contentRoot);

            // Поля кадра — СНАРУЖИ contentRoot и после него: они обязаны быть поверх всего
            // и не участвовать в его padding'е. Pick им оставлен по умолчанию (Position):
            // клик в чёрное поле не должен проваливаться в мир под панелью.
            _frameMaskLeft = CreateFrameMask("frameMaskLeft", left: true);
            _frameMaskRight = CreateFrameMask("frameMaskRight", left: false);
            root.Add(_frameMaskLeft);
            root.Add(_frameMaskRight);

            // Монтируем все скрины сразу, до старта boot-машины, а не при первом показе.
            // Ссылку на раннер проставляем ДО Mount: внутри него идёт регистрация безопасной зоны.
            if (_hasScreen)
            {
                loadingScreen.AppRunner = this;
                loadingScreen.Mount(_screensLayer);
            }

            if (_hasErrorScreen)
            {
                errorScreen.Mount(_screensLayer);
                RegisterSafeArea(errorScreen);
            }

            if (_hasCriticalScreen)
            {
                criticalScreen.Mount(_screensLayer);
                RegisterSafeArea(criticalScreen);
            }
        }

        private VisualElement CreateFrameMask(string name, bool left)
        {
            var mask = new VisualElement { name = name };
            mask.style.position = Position.Absolute;
            mask.style.top = 0;
            mask.style.bottom = 0;
            if (left) mask.style.left = 0;
            else mask.style.right = 0;
            mask.style.width = 0;
            mask.style.backgroundColor = frameBarColor;
            mask.style.display = DisplayStyle.None;
            return mask;
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

            if (_uiCallbackRegistered)
            {
                _uiCallbackRegistered = false;
                if (_panelRenderer != null) _panelRenderer.UnregisterUIReloadCallback(OnUIReload);
            }

            ReleaseFramePanelSettings();

            if (_bootCts != null)
            {
                _bootCts.Cancel();
                _bootCts.Dispose();
                _bootCts = null;
            }

            DrainMainThreadQueueOnDestroy();

            // снимаем ожидающих готовности, если бут не дошёл до Ready
            if (!IsAppReady) _readySource.TrySetCanceled();

            // teardown в обратном порядке инициализации: подписки/хендлы сервисов,
            // созданных позже, гасятся раньше зависимых от них.
            if (_services != null)
                for (var i = _services.Length - 1; i >= 0; i--)
                {
                    try { _services[i].Destroy(); }
                    catch (Exception e) { Debug.LogException(e); }
                }
        }

        private void Update()
        {
            PumpMainThreadQueue();

            // Кадр и безопасная зона обслуживаются независимо от стадии бута: они нужны и экрану
            // загрузки, и критическому скрину, то есть ещё до готовности приложения.
            TickScreenMetrics();

            TickBootStateMachine();

            // Сервисы тикают только после полной инициализации.
            if (_bootState != BootState.Ready) return;

            if (!_hasUpdatable) return;
            foreach (var updatableService in _updatableServices) updatableService.Update();
        }
    }
}
