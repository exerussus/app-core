using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Views;
using Exerussus.AppCore.Internal;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Навигация по страницам: переходы, стек возврата и отложенные запросы.
    /// </summary>
    public partial class AppRunner
    {
        
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

        /// <summary>Текущая активная страница. <c>null</c> до первого перехода.</summary>
        private AppPage _currentPage;

        /// <summary>Страница, открываемая при старте приложения (первый элемент <see cref="allPages"/>).</summary>
        private AppPage _defaultPage;

        /// <summary>
        /// Флаг, блокирующий одновременное выполнение нескольких переходов между страницами.
        /// Устанавливается в <c>true</c> на время асинхронных операций навигации.
        /// </summary>
        private bool _isChangingPage;

        /// <summary>Словарь для быстрого поиска страницы по типу.</summary>
        private readonly Dictionary<PageId, AppPage> _pagesDict = new();

        private readonly BoundedStack<PageId> _pageTransitionStack = new(8);

        
        /// <summary>
        /// Вызывается один раз при первом монтировании страницы в дерево UI:
        /// передаёт её <see cref="PageId"/> и корневой <see cref="VisualElement"/>.
        /// </summary>
        public event Action<PageId, VisualElement> OnPageMounted;

        
        /// <summary>
        /// Вызывается после завершения перехода на новую страницу.
        /// </summary>
        /// <remarks>
        /// Событие гарантированно вызывается в главном потоке Unity.
        /// Все исключения из подписчиков перехватываются и логируются,
        /// не прерывая вызов остальных подписчиков.
        /// </remarks>
        public event Action<(PageId prev, PageId current)> OnPageChanged;

        /// <summary>
        /// Дополнительное событие смены страницы, вызываемое сразу после <see cref="OnPageChanged"/>
        /// в том же кадре и в главном потоке. Удобно, когда нужен порядок «сначала базовые
        /// подписчики, затем пост-обработчики».
        /// </summary>
        public event Action<(PageId prev, PageId current)> OnPagePostChanged;

        /// <summary>
        /// Безопасно вызывает событие <see cref="OnPageChanged"/>.
        /// Исключения из подписчиков перехватываются и логируются через <see cref="LogException"/>.
        /// </summary>
        private void InvokePageChanged(PageId from, PageId to)
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
        /// Назначает страницу по умолчанию — ту, что открывается при старте и через
        /// <see cref="SwitchToDefaultPage"/>.
        /// </summary>
        /// <remarks>Если страница с указанным <paramref name="pageUid"/> не найдена — вызов игнорируется с ошибкой в лог.</remarks>
        /// <param name="pageUid">Идентификатор страницы, которая станет стартовой.</param>
        /// <summary>
        /// Монтирует страницу и добивает всё, что требует готовой вёрстки: безопасную зону,
        /// Initialize контроллера, манипуляторы кнопок и событие <see cref="OnPageMounted"/>.
        /// </summary>
        /// <remarks>
        /// Идемпотентно: вся работа выполняется только при первом монтировании, поэтому метод
        /// одинаково безопасен и для прогрева на старте, и для ленивого пути из навигации.
        /// </remarks>
        private void MountPage(AppPage page)
        {
            if (!page.Mount(_pagesLayer)) return;

            RegisterSafeArea(page.SafeArea);
            page.Controller?.Initialize();
            RegisterAppView(page);
            OnPageMounted?.Invoke(page.PageUid, page.Root);
        }

        public void SetDefaultPage(PageId pageUid)
        {
            if (!_pagesDict.TryGetValue(pageUid, out var page))
            {
                Debug.LogError($"Page {pageUid} is not exist");
                return;
            }

            _defaultPage = page;
        }

        /// <summary>Проверяет, является ли указанная страница текущей активной.</summary>
        /// <param name="pageUid">Идентификатор проверяемой страницы.</param>
        /// <returns><c>true</c>, если страница сейчас активна.</returns>
        public bool IsActive(PageId pageUid)
        {
            return _currentPage != null && _currentPage.PageUid == pageUid;
        }

        /// <summary>Проверяет, является ли текущая активная страница страницей по умолчанию.</summary>
        /// <returns><c>true</c>, если активна дефолтная страница.</returns>
        public bool IsActiveDefault()
        {
            return _currentPage != null && _currentPage == _defaultPage;
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
            if (!IsAppReady)
            {
                Debug.LogWarning("SwitchToPrevPage до готовности App проигнорирован.");
                return;
            }
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
        public void SwitchToPage(PageId pageUid, bool withScreen = false, bool ignoreIfBusy = true)
        {
            // Рельса: навигация по System-страницам — только после Ready. До этого страницами
            // распоряжается сама boot-машина (стартовый NavigateTo). Попапы/скрины при этом
            // доступны и раньше — для гейтов из сервисов.
            if (!IsAppReady)
            {
                Debug.LogWarning($"SwitchToPage {pageUid} до готовности App проигнорирован. Используйте OnAppReady / WaitUntilReadyAsync.");
                return;
            }

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

        /// <summary>
        /// Выполняет переход на страницу с имитацией экрана загрузки заданной длительности.
        /// Используется при первом старте приложения.
        /// </summary>
        /// <remarks>
        /// Если экран загрузки не активен — сначала показывает его,
        /// затем выдерживает паузу <paramref name="duration"/> секунд,
        /// после чего выполняет переход через <see cref="SwitchToPage"/>.
        /// </remarks>
        private async UniTask SwitchWithFakeLoading(PageId pageUid, float duration)
        {
            if (_hasScreen && !loadingScreen.gameObject.activeSelf) await loadingScreen.Show(_screensLayer);
            await UniTask.Delay(TimeSpan.FromSeconds(duration));
            SwitchToPage(pageUid);
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

                    var prevPage = PageId.None;
                    
                    if (_currentPage != null)
                    {
                        prevPage = _currentPage.PageUid; 
                        _pageTransitionStack.Push(prevPage);
                    }
                    
                    _currentPage = targetPage;
                    MountPage(_currentPage);
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
