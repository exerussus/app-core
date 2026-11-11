using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Стек попапов: открытие, закрытие и переключение.
    /// </summary>
    public partial class AppRunner
    {
        /// <summary>
        /// Флаг, блокирующий одновременное выполнение нескольких переходов между попапами.
        /// Устанавливается в <c>true</c> на время асинхронных операций с попапами.
        /// </summary>
        private bool _isChangingPopup;

        /// <summary>
        /// Стек открытых попапов. Верхний элемент — текущий активный попап,
        /// получающий фокус ввода.
        /// </summary>
        private readonly Stack<AppPopup> _popupStack = new();

        /// <summary>Словарь для быстрого поиска попапа по типу.</summary>
        private readonly Dictionary<PopupId, AppPopup> _popupsDict = new();

        /// <summary>
        /// Вызывается после открытия попапа.
        /// </summary>
        /// <remarks>
        /// Событие гарантированно вызывается в главном потоке Unity.
        /// Все исключения из подписчиков перехватываются и логируются.
        /// </remarks>
        public event Action<PopupId> OnPopupOpened;

        /// <summary>
        /// Вызывается после закрытия попапа.
        /// </summary>
        /// <remarks>
        /// Событие гарантированно вызывается в главном потоке Unity.
        /// Все исключения из подписчиков перехватываются и логируются.
        /// </remarks>
        public event Action<PopupId> OnPopupClosed;

        /// <summary>
        /// Безопасно вызывает событие <see cref="OnPopupOpened"/>: каждый подписчик — в своём try.
        /// </summary>
        private void InvokePopupOpened(PopupId popupUid) => InvokeEach(OnPopupOpened, popupUid);

        /// <summary>
        /// Безопасно вызывает событие <see cref="OnPopupClosed"/>: каждый подписчик — в своём try.
        /// </summary>
        /// <param name="popupUid">Попап, который был закрыт.</param>
        private void InvokePopupClosed(PopupId popupUid) => InvokeEach(OnPopupClosed, popupUid);

        /// <summary>Предикат ожидания текущей операции с попапами. Создаётся один раз.</summary>
        private Func<bool> _isPopupBusy;

        /// <summary>
        /// Монтирует попап и добивает всё, что требует готовой вёрстки. Зеркало
        /// <c>MountPage</c>: попапы всегда ленивые, их первое монтирование — это первое открытие.
        /// </summary>
        private void MountPopup(AppPopup popup)
        {
            if (!popup.Mount(_popupsLayer)) return;

            if (popup.HasController) popup.Controller.Setup();
            RegisterAppView(popup);
        }

        /// <summary>
        /// Закрывает текущий верхний попап (если есть) и открывает указанный,
        /// помещая его на вершину стека.
        /// </summary>
        /// <remarks>
        /// Если попап с указанным <paramref name="popupUid"/> не найден — вызов игнорируется с ошибкой в лог.
        /// Попап, уже стоящий наверху, повторно не открывается; попап, открытый ниже по стеку, —
        /// отказ с предупреждением.
        /// </remarks>
        /// <param name="popupUid">Идентификатор попапа, который нужно открыть.</param>
        public void SwitchPopup(PopupId popupUid)
        {
            if (!_popupsDict.TryGetValue(popupUid, out var popup))
            {
                Debug.LogError($"Popup {popupUid} is not exist.");
                return;
            }

            SwitchPopupInternal(popup).Forget(LogFault);
        }

        /// <summary>Проверяет, открыт ли указанный попап (находится ли он в стеке).</summary>
        /// <param name="popupType">Идентификатор проверяемого попапа.</param>
        /// <returns><c>true</c>, если попап присутствует в стеке открытых.</returns>
        public bool IsActive(PopupId popupType)
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
        /// <remarks>
        /// Если попап с указанным <paramref name="popupType"/> не найден — вызов игнорируется с ошибкой в лог.
        /// Попап, уже стоящий наверху, повторно не открывается; попап, открытый ниже по стеку, —
        /// отказ с предупреждением: один попап не может лежать в стеке дважды.
        /// </remarks>
        /// <param name="popupType">Идентификатор попапа для открытия.</param>
        public void OpenPopup(PopupId popupType)
        {
            if (!_popupsDict.TryGetValue(popupType, out var popup))
            {
                Debug.LogError($"Popup {popupType} is not exist.");
                return;
            }

            OpenPopupInternal(popup).Forget(LogFault);
        }

        /// <summary>Закрывает верхний попап в стеке; фокус возвращается предыдущему попапу, если он есть.</summary>
        /// <remarks>Если стек попапов пуст — вызов игнорируется.</remarks>
        public void CloseActivePopup()
        {
            if (_popupStack.Count == 0) return;

            CloseActivePopupInternal().Forget(LogFault);
        }

        /// <summary>Лежит ли попап в стеке. Энумератор стека — структура, без аллокаций.</summary>
        private bool IsInPopupStack(AppPopup popup)
        {
            foreach (var opened in _popupStack)
                if (ReferenceEquals(opened, popup))
                    return true;
            return false;
        }

        /// <summary>
        /// Проверка повторного открытия: наверху — молча ничего не делаем, ниже по стеку —
        /// предупреждение и отказ.
        /// </summary>
        /// <returns><c>true</c>, если попап уже открыт и открывать его не нужно.</returns>
        private bool RejectReopen(AppPopup popup)
        {
            if (_popupStack.Count == 0) return false;
            if (ReferenceEquals(_popupStack.Peek(), popup)) return true;
            if (!IsInPopupStack(popup)) return false;

            Debug.LogWarning($"[AppCore] Попап \"{popup.PopupId}\" уже открыт ниже по стеку — повторное открытие отклонено.", popup);
            return true;
        }

        /// <summary>
        /// Открывает один попап поверх стека: объект, вёрстка (при первом открытии — со
        /// <c>Setup</c> контроллера и обвязкой кнопок), <c>OnActivate</c> → <c>OnShow</c>, событие.
        /// Фокусом не занимается — это дело вызывающей операции.
        /// </summary>
        /// <returns><c>false</c>, если у попапа нет вёрстки: ошибка уже в логе, в стек он не попал.</returns>
        private async UniTask<bool> ShowPopupCore(AppPopup popup)
        {
            popup.gameObject.SetActive(true);
            MountPopup(popup);

            if (popup.Root == null)
            {
                // Пустой попап в стеке перехватывал бы «назад», а закрыть его было бы нечем.
                popup.gameObject.SetActive(false);
                return false;
            }

            _popupStack.Push(popup);

            if (popup.HasController)
            {
                await popup.Controller.OnActivate();
                ThrowIfDestroyed();
                await popup.Controller.OnShow();
                ThrowIfDestroyed();
            }

            InvokePopupOpened(popup.PopupUid);
            return true;
        }

        /// <summary>
        /// Закрывает один попап, уже снятый со стека: <c>OnHide</c> → <c>OnDeactivate</c>, снятие
        /// вёрстки с экрана, объект, событие. <c>OnUnfocus</c> — дело вызывающей операции.
        /// </summary>
        private async UniTask HidePopupCore(AppPopup popup)
        {
            if (popup.HasController)
            {
                await popup.Controller.OnHide();
                ThrowIfDestroyed();
                await popup.Controller.OnDeactivate();
                ThrowIfDestroyed();
            }

            // Вёрстка попапа живёт на панели независимо от GameObject: без Unmount он оставался
            // бы поверх страницы и забирал ввод, а в стеке его уже нет — закрыть нечем.
            popup.Unmount();
            popup.gameObject.SetActive(false);
            InvokePopupClosed(popup.PopupUid);
        }

        /// <summary>Отдаёт фокус верхнему попапу стека, если он есть.</summary>
        private void FocusTopPopup()
        {
            if (_popupStack.Count == 0) return;

            var top = _popupStack.Peek();
            if (top.HasController) top.Controller.OnFocus().Forget(LogFault);
        }

        /// <summary>
        /// Внутренняя реализация переключения попапа: закрывает текущий верхний попап (если есть)
        /// и открывает новый, помещая его в стек.
        /// </summary>
        /// <remarks>
        /// Защищена флагом <see cref="_isChangingPopup"/>: если операция с попапами уже выполняется,
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

            if (RejectReopen(newPopup)) return;

            _isChangingPopup = true;

            try
            {
                if (_popupStack.Count > 0)
                {
                    var current = _popupStack.Pop();
                    if (current.HasController) await current.Controller.OnUnfocus();
                    ThrowIfDestroyed();
                    await HidePopupCore(current);
                }

                await ShowPopupCore(newPopup);
            }
            finally
            {
                _isChangingPopup = false;
            }

            FocusTopPopup();
        }

        /// <summary>
        /// Внутренняя реализация открытия попапа поверх стека.
        /// Текущий верхний попап теряет фокус, новый получает фокус после открытия.
        /// </summary>
        /// <remarks>
        /// Защищена флагом <see cref="_isChangingPopup"/>: если операция с попапами уже выполняется,
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

            if (RejectReopen(popup)) return;

            _isChangingPopup = true;

            try
            {
                if (_popupStack.Count > 0)
                {
                    var current = _popupStack.Peek();
                    if (current.HasController) await current.Controller.OnUnfocus();
                    ThrowIfDestroyed();
                }

                await ShowPopupCore(popup);
            }
            finally
            {
                _isChangingPopup = false;
            }

            // Фокус — верхнему: новому попапу, а если открыть его не удалось — прежнему,
            // который уже успел получить OnUnfocus.
            FocusTopPopup();
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
            if (_popupStack.Count == 0) return;

            _isChangingPopup = true;

            try
            {
                var popup = _popupStack.Pop();
                if (popup.HasController) await popup.Controller.OnUnfocus();
                ThrowIfDestroyed();
                await HidePopupCore(popup);
            }
            finally
            {
                _isChangingPopup = false;
            }

            FocusTopPopup();
        }

        /// <summary>
        /// Принудительно закрывает все попапы в стеке.
        /// Верхний попап сначала теряет фокус, затем все попапы последовательно скрываются и деактивируются.
        /// </summary>
        /// <remarks>
        /// Вызывается навигацией перед уходом со страницы и перед активацией новой (<see cref="NavigateTo"/>).
        /// Идущую операцию с попапами не перебивает, а дожидается: иначе её продолжение положило бы
        /// попап в уже очищенный стек поверх новой страницы.
        /// Флаг <see cref="_isChangingPopup"/> устанавливается на всё время операции
        /// и сбрасывается в блоке <c>finally</c>.
        /// </remarks>
        private async UniTask CloseAllPopups()
        {
            while (_isChangingPopup)
            {
                _isPopupBusy ??= () => _isChangingPopup;
                await UniTask.WaitWhile(_isPopupBusy, cancellationToken: _lifetimeToken);
            }

            if (_popupStack.Count == 0)
                return;

            _isChangingPopup = true;

            try
            {
                var focusedPopup = _popupStack.Peek();

                if (focusedPopup.HasController)
                {
                    await focusedPopup.Controller.OnUnfocus();
                    ThrowIfDestroyed();
                }

                while (_popupStack.Count > 0)
                {
                    await HidePopupCore(_popupStack.Pop());
                }
            }
            finally
            {
                _isChangingPopup = false;
            }
        }
    }
}
