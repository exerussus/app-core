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

        /// <summary>
        /// Отложенный запрос — возврат «назад». Цель тогда не хранится, а берётся из истории
        /// в момент, когда до запроса дойдёт очередь: текущий переход ещё допишет историю.
        /// </summary>
        private bool _pendingIsBack;

        /// <summary>Текущая активная страница. <c>null</c> до первого перехода.</summary>
        private AppPage _currentPage;

        /// <summary>Текущая активная страница. <c>null</c> до стартовой навигации.</summary>
        public AppPage CurrentPage => _currentPage;

        /// <summary>Страница, открываемая при старте приложения (первый элемент <see cref="allPages"/>).</summary>
        private AppPage _defaultPage;

        /// <summary>
        /// Флаг, блокирующий одновременное выполнение нескольких переходов между страницами.
        /// Устанавливается в <c>true</c> на время асинхронных операций навигации.
        /// </summary>
        private bool _isChangingPage;

        /// <summary>Идёт переход между страницами: <see cref="CurrentPage"/> ещё старая, её вёрстка уже снята.</summary>
        public bool IsChangingPage => _isChangingPage;

        /// <summary>Словарь для быстрого поиска страницы по типу.</summary>
        private readonly Dictionary<PageId, AppPage> _pagesDict = new();

        /// <summary>История для «назад». Возврат снимает запись, а не кладёт текущую страницу.</summary>
        private readonly PageHistory _pageHistory = new(8);

        
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
        /// Безопасно вызывает <see cref="OnPageChanged"/>, затем <see cref="OnPagePostChanged"/>.
        /// Каждый подписчик — в своём try: упавший не лишает события остальных.
        /// </summary>
        private void InvokePageChanged(PageId from, PageId to)
        {
            var args = (from, to);
            InvokeEach(OnPageChanged, args);
            InvokeEach(OnPagePostChanged, args);
        }

        /// <summary>
        /// Монтирует страницу и добивает всё, что требует готовой вёрстки: безопасную зону,
        /// привязку элементов и Initialize контроллера, манипуляторы кнопок и событие
        /// <see cref="OnPageMounted"/>.
        /// </summary>
        /// <remarks>
        /// Идемпотентно: вся работа выполняется только при первом монтировании, поэтому метод
        /// одинаково безопасен и для прогрева на старте, и для ленивого пути из навигации.
        /// </remarks>
        private void MountPage(AppPage page)
        {
            if (!page.Mount(_pagesLayer)) return;

            if (page.HasController) page.Controller.Setup();
            RegisterAppView(page);

            try { OnPageMounted?.Invoke(page.PageUid, page.Root); }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>
        /// Назначает страницу по умолчанию — ту, что открывается при старте и через
        /// <see cref="SwitchToDefaultPage"/>.
        /// </summary>
        /// <remarks>Если страница с указанным <paramref name="pageUid"/> не найдена — вызов игнорируется с ошибкой в лог.</remarks>
        /// <param name="pageUid">Идентификатор страницы, которая станет стартовой.</param>
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
        /// <remarks>
        /// Если стек переходов пуст — вызов игнорируется. Возврат снимает запись со стека, а
        /// текущую страницу в него не кладёт: A→B→C, «назад», «назад» приводит на A. Запись
        /// снимается, только когда переход реально начался: отброшенный из-за занятости запрос
        /// её не теряет.
        /// </remarks>
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
            if (!_pageHistory.TryPeek(out var pageUid)) return;

            if (!_pagesDict.TryGetValue(pageUid, out var page))
            {
                Debug.LogError($"Page {pageUid.ToString()} is not exist.");
                return;
            }

            if (_isChangingPage)
            {
                if (ignoreIfBusy)
                {
                    Debug.LogWarning($"Back to {page} ignored: navigation in progress.");
                    return;
                }

                SetPending(null, withScreen, isBack: true);
                return;
            }

            NavigateTo(page, withScreen, isBack: true).Forget(LogFault);
        }

        /// <summary>
        /// Запоминает отложенный переход — он будет подхвачен циклом в <see cref="NavigateTo"/>
        /// сразу после завершения текущей навигации, без скрытия экрана между ними.
        /// Выигрывает последний запрос.
        /// </summary>
        private void SetPending(AppPage page, bool withScreen, bool isBack)
        {
            _pendingPage = page;
            _pendingWithScreen = withScreen;
            _pendingIsBack = isBack;
            _hasPendingPage = true;

            // Если новый запрос со скрином, а экран ещё не поднят — поднимаем его немедленно,
            // не дожидаясь конца текущей навигации. Без экрана ShowScreen — no-op.
            if (withScreen && !IsScreenVisible)
                ShowScreen().Forget(LogFault);
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

                SetPending(page, withScreen, isBack: false);
                return;
            }

            NavigateTo(page, withScreen).Forget(LogFault);
        }

        /// <summary>Переходит на страницу по умолчанию (см. <see cref="SetDefaultPage"/>).</summary>
        /// <param name="withScreen">Показывать ли экран загрузки на время перехода (при отсутствии экрана — игнорируется).</param>
        /// <param name="ignoreIfBusy">Отбрасывать (<c>true</c>) или откладывать (<c>false</c>) запрос при идущей навигации.</param>
        public void SwitchToDefaultPage(bool withScreen = false, bool ignoreIfBusy = true)
        {
            SwitchToPage(_defaultPage.PageUid, withScreen, ignoreIfBusy);
        }

        /// <summary>
        /// Пытается забрать отложенный запрос на переход, очищая слот.
        /// </summary>
        /// <remarks>
        /// Возвращает <c>false</c>, если слот пуст или если отложенная страница
        /// совпадает с текущей (в этом случае переход бессмысленен — слот всё равно очищается).
        /// </remarks>
        private bool TryTakePending(out AppPage page, out bool withScreen, out bool isBack)
        {
            page = null;
            withScreen = false;
            isBack = false;

            if (!_hasPendingPage) return false;

            var next = _pendingPage;
            var nextScreen = _pendingWithScreen;
            var nextIsBack = _pendingIsBack;

            _pendingPage = null;
            _pendingWithScreen = false;
            _pendingIsBack = false;
            _hasPendingPage = false;

            // Возврат: цель — вершина истории на текущий момент.
            if (nextIsBack)
            {
                next = null;
                if (_pageHistory.TryPeek(out var backUid)) _pagesDict.TryGetValue(backUid, out next);
            }

            if (next == null || next == _currentPage) return false;

            page = next;
            withScreen = nextScreen;
            isBack = nextIsBack;
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
        /// <param name="isBack">
        /// Переход — возврат «назад»: снимает запись истории вместо того, чтобы класть в неё
        /// текущую страницу.
        /// </param>
        private async UniTask NavigateTo(AppPage page, bool withScreen = false, bool isBack = false)
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
                var back = isBack;

                while (true)
                {
                    // Страница без вёрстки не откроется: проверяем ДО ухода с текущей, иначе
                    // приложение осталось бы вовсе без страницы.
                    if (!targetPage.CanMount)
                    {
                        Debug.LogError($"[AppCore] Переход на страницу \"{targetPage.PageId}\" отменён: у неё не задана вёрстка.", targetPage);

                        if (TryTakePending(out var skipPage, out var skipScreen, out var skipBack))
                        {
                            targetPage = skipPage;
                            useScreen = skipScreen;
                            back = skipBack;
                            continue;
                        }

                        break;
                    }

                    if (_popupStack.Count > 0 || _isChangingPopup) await CloseAllPopups();

                    if (_currentPage != null)
                    {
                        if (_currentPage.HasController)
                        {
                            await _currentPage.Controller.OnHide();
                            ThrowIfDestroyed();
                        }

                        // Экран поднимаем только если он ещё не виден; в каскаде он уже
                        // может быть поднят (либо предыдущей итерацией, либо немедленным
                        // Show из SwitchToPage при отложенном запросе со скрином).
                        // Гард по _hasScreen важен: без экрана незачем выдерживать паузу 1с.
                        // Пауза по неотмасштабированному времени: при timeScale = 0 переход
                        // иначе висел бы вечно.
                        if (_hasScreen && useScreen && !IsScreenVisible)
                        {
                            await ShowScreen();
                            await UniTask.Delay(TimeSpan.FromSeconds(1f), DelayType.UnscaledDeltaTime, cancellationToken: _lifetimeToken);
                        }

                        if (_currentPage.HasController)
                        {
                            await _currentPage.Controller.OnDeactivate();
                            ThrowIfDestroyed();
                        }
                        _currentPage.gameObject.SetActive(false);
                        _currentPage.Unmount();
                    }

                    var prevPage = PageId.None;
                    if (_currentPage != null) prevPage = _currentPage.PageUid;

                    // Переход реально начался — только теперь трогаем историю.
                    _pageHistory.Commit(prevPage, targetPage.PageUid, back);

                    // Попапы, открытые за время ухода со страницы (из её OnHide/OnDeactivate или
                    // сервисом), новой странице не принадлежат — закрываем их до её активации.
                    if (_popupStack.Count > 0 || _isChangingPopup) await CloseAllPopups();

                    _currentPage = targetPage;
                    MountPage(_currentPage);
                    _currentPage.gameObject.SetActive(true);
                    await _currentPage.Activate();
                    ThrowIfDestroyed();

                    if (_currentPage.HasController)
                    {
                        await _currentPage.Controller.OnShow();
                        ThrowIfDestroyed();
                    }

                    InvokePageChanged(prevPage, _currentPage.PageUid);

                    // Если за время перехода появился отложенный запрос — обрабатываем его
                    // прямо сейчас, не скрывая экран загрузки между итерациями.
                    if (TryTakePending(out var nextPage, out var nextWithScreen, out var nextIsBack))
                    {
                        targetPage = nextPage;
                        useScreen = nextWithScreen;
                        back = nextIsBack;
                        continue;
                    }

                    // Отложенных запросов нет — пытаемся скрыть экран...
                    if (!_isForceScreen && IsScreenVisible)
                        await HideScreen().SuppressCancellationThrow();

                    // ...но во время await Hide мог прилететь новый отложенный запрос.
                    // Если так — снова запускаем итерацию.
                    if (TryTakePending(out nextPage, out nextWithScreen, out nextIsBack))
                    {
                        targetPage = nextPage;
                        useScreen = nextWithScreen;
                        back = nextIsBack;
                        continue;
                    }

                    break;
                }
            }
            finally
            {
                _isChangingPage = false;
                if (!_lifetimeToken.IsCancellationRequested && !_isForceScreen && IsScreenVisible)
                    await HideScreen().SuppressCancellationThrow();
            }
        }
    }
}
