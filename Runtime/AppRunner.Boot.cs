using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Exerussus.AppCore.Boot;
using Exerussus.AppCore.Screens;
using Exerussus.AppCore.Services;
using Exerussus.DI;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Машина загрузки: последовательность шагов, терминальные состояния и сигнал готовности.
    /// </summary>
    public partial class AppRunner
    {
        
        /// <summary>
        /// Опрашивает флаг <see cref="_bootStepCompleted"/> и переводит машину в следующий стэйт,
        /// если шаг завершён. За один тик выполняется не более одного перехода.
        /// </summary>
        private void TickBootStateMachine()
        {
            if (_bootState == BootState.NotStarted || _bootState == BootState.Ready
                                                   || _bootState == BootState.Failed || _bootState == BootState.Halted) return;
            if (!_bootStepCompleted) return;

            if (_bootHalted)
            {
                // штатный стоп: приложения не будет, но это не ошибка — оверлей ядра не нужен
                TransitionTo(BootState.Halted);
                return;
            }

            if (_bootStepFailed)
            {
                // шаг упал — уходим в терминальное состояние с показом критического скрина
                TransitionTo(BootState.Failed);
                return;
            }

            switch (_bootState)
            {
                case BootState.CoveringScreen: TransitionTo(BootState.PreBootstrap); break;
                case BootState.PreBootstrap: TransitionTo(BootState.RegisteringServices); break;
                // ВАЖНО: core (страницы/попапы) инициализируется ДО асинхронной инициализации
                // сервисов — чтобы попапы/скрины, открываемые из Initialize сервисов (гейт, QR,
                // регистрация), уже были зарегистрированы к моменту вызова.
                case BootState.RegisteringServices: TransitionTo(BootState.InitializingCore); break;
                case BootState.InitializingCore: TransitionTo(BootState.InitializingServices); break;
                case BootState.InitializingServices: TransitionTo(BootState.PostBootstrap); break;
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

            ReportProgress(state);

            switch (state)
            {
                case BootState.CoveringScreen: StartCoverScreenStep(); break;
                case BootState.PreBootstrap: StartPreBootstrapStep(); break;
                case BootState.RegisteringServices: StartRegisterServicesStep(); break;
                case BootState.InitializingCore: StartInitializeCoreStep(); break;
                case BootState.InitializingServices: StartInitializeServicesStep(); break;
                case BootState.PostBootstrap: StartPostBootstrapStep(); break;
                case BootState.Ready: StartReadyStep(); break;
                case BootState.Failed: StartFailedStep(); break;
                case BootState.Halted: StartHaltedStep(); break;
            }
        }

        /// <summary>Сообщает прогресс бута подписчикам (для отрисовки на LoadingScreen).</summary>
        private void ReportProgress(BootState state)
        {
            if (OnBootProgress == null) return;
            if (state < BootState.CoveringScreen || state > BootState.Ready) return;

            var normalized = (float)((int)state - (int)BootState.CoveringScreen) / (BootStepCount - 1);
            try { OnBootProgress.Invoke(new BootProgress(normalized, state)); }
            catch (Exception e) { Debug.LogException(e); }
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

        private void StartRegisterServicesStep()
        {
            // Синхронный шаг: только DI-регистрация и инъекции. Попапов ещё нет.
            try
            {
                RegisterServicesSync();
                _bootStepCompleted = true;
            }
            catch (Exception e)
            {
                FailBootStep("регистрация сервисов", e);
            }
        }

        private void StartInitializeCoreStep()
        {
            // Синхронный шаг: страницы/попапы и их контроллеры. Идёт ДО async-инициализации сервисов.
            try
            {
                InitializeCoreSync();
                _bootStepCompleted = true;
            }
            catch (BootHaltException e)
            {
                HaltBoot(e.Message);
            }
            catch (Exception e)
            {
                FailBootStep("инициализация страниц/попапов", e);
            }
        }

        /// <summary>
        /// Асинхронная инициализация сервисов. Выполняется после core, поэтому сервисы уже могут
        /// открывать System-попапы и показывать скрины. Имя упавшего сервиса попадает в причину.
        /// Штатный <see cref="BootHaltException"/> из сервиса уводит машину в Halted (не ошибка).
        /// </summary>
        private void StartInitializeServicesStep()
        {
            RunAsyncStep(async token =>
            {
                foreach (var service in _services)
                {
                    try
                    {
                        await service.Initialize(token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (BootHaltException) { throw; }   // штатный стоп — не ошибка сервиса
                    catch (Exception e)
                    {
                        // заворачиваем, чтобы в причине падения был виден конкретный сервис
                        throw new Exception($"{service.GetType().Name}.Initialize: {e.Message}", e);
                    }
                }

                foreach (var service in _services)
                {
                    try
                    {
                        await service.PostInitialize(token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (BootHaltException) { throw; }
                    catch (Exception e)
                    {
                        throw new Exception($"{service.GetType().Name}.PostInitialize: {e.Message}", e);
                    }
                }
            }, "инициализация сервисов");
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
            // Финальный стэйт: стартовая навигация → сигнал готовности.
            // Попапы, отложенные на время бута, открываются подписчиками ПОСЛЕ сигнала —
            // стартовый NavigateTo их уже не закроет.
            FinishBootAsync().Forget();
        }

        /// <summary>
        /// Завершение бута: стартовая навигация на дефолтную страницу → выставление
        /// <see cref="IsAppReady"/> и вызов <see cref="OnAppReady"/>. Падение самой навигации
        /// уводит машину в Failed, а не тихо логируется.
        /// </summary>
        private async UniTaskVoid FinishBootAsync()
        {
            try
            {
                // Прогрев (если включён) идёт здесь, а не в InitializingCore: к этому моменту
                // сервисы уже подписаны на OnPageMounted, поэтому событие о монтировании
                // получают все страницы, а не только те, что смонтировались позже.
                if (prewarmPages)
                {
                    foreach (var page in allPages) MountPage(page);
                }

                await NavigateTo(_defaultPage);
                if (_isDestroyed) return;

                IsAppReady = true;
                _readySource.TrySetResult();

                try { OnAppReady?.Invoke(_container); }
                catch (Exception e) { Debug.LogException(e); }
            }
            catch (OperationCanceledException)
            {
                // приложение выгружается — тихий выход
            }
            catch (Exception e)
            {
                if (_isDestroyed) return;
                Debug.LogException(e);
                _bootFailReason = "стартовая навигация: " + e.Message;
                TransitionTo(BootState.Failed);
            }
        }

        /// <summary>
        /// Запускает асинхронный шаг и по его завершении выставляет <see cref="_bootStepCompleted"/>.
        /// Само выставление флага происходит на главном потоке (UniTask продолжается в нём),
        /// поэтому <see cref="TickBootStateMachine"/> увидит его в ближайшем <see cref="Update"/>.
        /// </summary>
        private void RunAsyncStep(Func<CancellationToken, UniTask> stepFactory, string stepName = null)
        {
            RunAsyncStepInternal(stepFactory, stepName).Forget();
        }

        private async UniTaskVoid RunAsyncStepInternal(Func<CancellationToken, UniTask> stepFactory, string stepName)
        {
            var token = _bootCts.Token;
            try
            {
                if (stepTimeoutSeconds > 0f)
                {
                    // Watchdog: гонка шага против таймаута. Зависший await (сеть не ответила)
                    // не оставляет юзера на вечном лоадере — он превращается в видимый Failed.
                    var finished = await UniTask.WhenAny(
                        stepFactory(token),
                        UniTask.Delay(TimeSpan.FromSeconds(stepTimeoutSeconds), cancellationToken: token));

                    if (finished != 0)
                        throw new TimeoutException($"шаг завис дольше {stepTimeoutSeconds:0.#}с");
                }
                else
                {
                    await stepFactory(token);
                }

                if (_isDestroyed) return;
                _bootStepCompleted = true;
            }
            catch (OperationCanceledException)
            {
                // OnDestroy дернул CTS — выходим тихо.
            }
            catch (BootHaltException e)
            {
                if (_isDestroyed) return;
                HaltBoot(e.Message);
            }
            catch (Exception e)
            {
                if (_isDestroyed) return;
                FailBootStep(stepName ?? _bootState.ToString(), e);
            }
        }

        /// <summary>
        /// Синхронная регистрация сервисов в DI: shared-объекты, скрины, сборка списка сервисов,
        /// инъекции и синхронный <see cref="IAppService.PreInitialize"/>. Попапов ещё нет —
        /// открывать их здесь нельзя (для этого есть async-стейт InitializingServices).
        /// </summary>
        private void RegisterServicesSync()
        {
            if (sharedObjects is { Length: >0 })
            {
                foreach (var sharedObject in sharedObjects) _container.Add(sharedObject);
            }

            // Скрины кладём в контейнер, чтобы сервисы могли [Inject] их и показать гейт/крит.
            // Сами скрины контейнер не читают — инвариант «Screen не касается DI» сохраняется.
            if (_hasErrorScreen) _container.Add(errorScreen);
            if (_hasCriticalScreen) _container.Add(criticalScreen);

            var internalServices = InternalServiceRegistry.GetAllServices();
            if (appServiceRegistry != null)
            {
                var externalServices = appServiceRegistry.GetAllServices();
                _services = new IAppService[internalServices.Length + externalServices.Length];

                var index = 0;
                for (; index < internalServices.Length; index++) _services[index] = internalServices[index];
                for (var i = 0; i < externalServices.Length; i++) _services[index++] = externalServices[i];
            }
            else
            {
                _services = internalServices;
            }

            _updatableServices = _services.OfType<IAppServiceUpdate>().ToArray();
            _appManipulatorBuilders = _services.OfType<IAppManipulatorBuilder>().ToArray();
            _hasUpdatable = _updatableServices.Length > 0;

            foreach (var service in _services) _container.Add(service);
            foreach (var service in _services) _container.Provide(service);
            foreach (var service in _services) service.OnInject(_container);
            foreach (var service in _services) _container.Inject(service);
            foreach (var service in _services) service.PreInitialize();
        }

        /// <summary>
        /// Синхронная инициализация core: страницы, попапы, их контроллеры.
        /// Выполняется ДО асинхронной инициализации сервисов, поэтому к моменту, когда сервис
        /// в своём Initialize захочет открыть попап, тот уже в реестре.
        /// </summary>
        private void InitializeCoreSync()
        {
            foreach (var page in allPages) page.gameObject.SetActive(false);
            foreach (var page in allPages) page.AppRunner = this;

            // Идентификаторы: PageUid/PopupUid считаются здесь, до заполнения реестров.
            foreach (var page in allPages) page.PreInitialize();
            foreach (var popup in allPopups) popup.PreInitialize();

            // Явная проверка на дубликаты: Add бросил бы «An item with the same key has already
            // been added», по которому невозможно понять, какие именно объекты виноваты.
            foreach (var page in allPages)
            {
                if (_pagesDict.TryGetValue(page.PageUid, out var clash))
                    throw new Exception($"Дублирующийся id страницы \"{page.PageId}\": объекты '{clash.name}' и '{page.name}'.");

                _pagesDict.Add(page.PageUid, page);
            }

            foreach (var popup in allPopups)
            {
                if (_popupsDict.TryGetValue(popup.PopupUid, out var clash))
                    throw new Exception($"Дублирующийся id попапа \"{popup.PopupId}\": объекты '{clash.name}' и '{popup.name}'.");

                _popupsDict.Add(popup.PopupUid, popup);
            }

            // Инъекции до монтирования: контроллеру для них не нужен Root, а вот
            // Initialize уже обязан видеть и зависимости, и готовую вёрстку — поэтому
            // Initialize вызывается при монтировании (MountPage/MountPopup), а не здесь.
            foreach (var page in allPages) _container.Inject(page);
            foreach (var page in allPages) if (page.HasController) _container.Inject(page.Controller);
            foreach (var popup in allPopups) _container.Inject(popup);
            foreach (var popup in allPopups) if (popup.HasController) _container.Inject(popup.Controller);
        }

        /// <summary>
        /// Единая точка провала шага: лог, фиксация причины и флагов.
        /// Машина увидит флаги в ближайшем Update и уйдёт в <see cref="BootState.Failed"/>.
        /// </summary>
        private void FailBootStep(string stepName, Exception e)
        {
            Debug.LogException(e);
            _bootFailReason = $"{stepName}: {e.Message}";
            _bootStepFailed = true;
            _bootStepCompleted = true;
        }

        /// <summary>
        /// Штатная остановка бута по запросу сервиса. В отличие от <see cref="FailBootStep"/>
        /// не является ошибкой: скрин инициатора уже на экране.
        /// </summary>
        private void HaltBoot(string reason)
        {
            _bootHaltReason = reason;
            _bootHalted = true;
            _bootStepCompleted = true;
        }

        /// <summary>
        /// Терминальное состояние ошибки: лог, показ <see cref="CriticalScreen"/> (reboot/quit)
        /// и событие <see cref="OnBootFailed"/>. Машина дальше не двигается.
        /// </summary>
        private void StartFailedStep()
        {
            var reason = string.IsNullOrEmpty(_bootFailReason) ? "Неизвестная ошибка загрузки" : _bootFailReason;
            Debug.LogError($"[BOOT] Загрузка остановлена: {reason}");

            // готовности уже не будет — снимаем ожидающих WaitUntilReadyAsync
            _readySource.TrySetCanceled();

            ShowBootError(reason);

            try { OnBootFailed?.Invoke(reason); }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>
        /// Терминальное состояние штатной остановки. Экран/скрин инициатора оставляем как есть:
        /// аварийный оверлей ядра НЕ показываем.
        /// </summary>
        private void StartHaltedStep()
        {
            Debug.Log($"[BOOT] Загрузка остановлена штатно: {_bootHaltReason}");
            _readySource.TrySetCanceled();
        }

        /// <summary>
        /// Показывает критическую ошибку бута: критический скрин (reboot/quit), а при его
        /// отсутствии/сбое — голый код-оверлей на слое скринов как последний рубеж.
        /// </summary>
        private void ShowBootError(string reason)
        {
            if (_hasCriticalScreen)
            {
                try
                {
                    criticalScreen.Show(reason, allowRebootOnFail, RestartBoot, null);
                    return;
                }
                catch (Exception e)
                {
                    // напр. скрин ещё не смонтирован — не даём аварийному пути упасть молча
                    Debug.LogException(e);
                }
            }

            ShowBootErrorOverlay(reason);
        }

        /// <summary>
        /// Фолбэк-оверлей на слое скринов — виден даже когда критический скрин не задан
        /// или его показ упал. Собирается кодом сознательно: это последний рубеж,
        /// он не должен зависеть от uxml/стилей/страниц/контейнера.
        /// </summary>
        private void ShowBootErrorOverlay(string message)
        {
            var overlay = new VisualElement { name = "bootErrorOverlay" };
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0;
            overlay.style.right = 0;
            overlay.style.top = 0;
            overlay.style.bottom = 0;
            overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.85f);
            overlay.style.justifyContent = Justify.Center;
            overlay.style.alignItems = Align.Center;
            overlay.pickingMode = PickingMode.Position;

            var label = new Label($"Критическая ошибка\n{message}");
            label.style.color = Color.white;
            label.style.fontSize = 20;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.maxWidth = Length.Percent(80);
            overlay.Add(label);

            _screensLayer.Add(overlay);
            overlay.BringToFront();
        }

        /// <summary>
        /// Перезапуск приложения после критического сбоя: чистая перезагрузка активной сцены.
        /// AppRunner пересоздастся и заново прогонит Awake → boot-машину. Гарантированно
        /// снимает любое частично-инициализированное состояние.
        /// </summary>
        public void RestartBoot()
        {
            var scene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(scene.buildIndex);
        }

        /// <summary>
        /// Ожидание полной готовности приложения (бут завершён, стартовая навигация выполнена).
        /// Безопасная точка для открытия попапов, инициированных во время бута.
        /// Если приложение уже готово — завершается сразу; при падении/остановке/уничтожении — отменой.
        /// </summary>
        public UniTask WaitUntilReadyAsync(CancellationToken token = default)
        {
            if (IsAppReady) return UniTask.CompletedTask;
            if (_isDestroyed) return UniTask.FromCanceled(token);
            return _readySource.Task.AttachExternalCancellation(token);
        }

        /// <summary>Число шагов до Ready — для нормализации прогресса.</summary>
        private const int BootStepCount = (int)BootState.Ready - (int)BootState.CoveringScreen + 1;

        /// <summary>Текущий этап загрузки.</summary>
        private BootState _bootState = BootState.NotStarted;

        /// <summary>
        /// Флаг завершения текущего шага. Выставляется в <c>true</c> асинхронной операцией
        /// (или сразу для синхронных шагов) и опрашивается в <see cref="Update"/>.
        /// Сбрасывается при входе в следующий стэйт.
        /// </summary>
        private bool _bootStepCompleted;

        /// <summary>Признак ошибки в текущем шаге; переводит машину в <see cref="BootState.Failed"/>.</summary>
        private bool _bootStepFailed;

        /// <summary>Человекочитаемая причина падения бута (заполняется в <see cref="FailBootStep"/>).</summary>
        private string _bootFailReason;

        /// <summary>Признак штатной остановки бута (см. <see cref="BootHaltException"/>).</summary>
        private bool _bootHalted;

        /// <summary>Причина штатной остановки — только для лога.</summary>
        private string _bootHaltReason;

        /// <summary>Источник токена отмены для асинхронных шагов; отменяется в <see cref="OnDestroy"/>.</summary>
        private CancellationTokenSource _bootCts;

        /// <summary>Полностью ли готово приложение: бут завершён, стартовая навигация выполнена.</summary>
        public bool IsAppReady { get; private set; }

        private readonly UniTaskCompletionSource _readySource = new();

        /// <summary>Приложение полностью готово (после стартовой навигации). Безопасная точка для отложенных попапов.</summary>
        public event Action<DependenciesContainer> OnAppReady;

        /// <summary>Бут упал терминально. Аргумент — причина. Показан <see cref="CriticalScreen"/>.</summary>
        public event Action<string> OnBootFailed;

        /// <summary>Прогресс бута для отрисовки на LoadingScreen: нормализованное значение 0..1 и человекочитаемая стадия.</summary>
        public event Action<BootProgress> OnBootProgress;
    }
}
