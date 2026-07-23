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
        /// Безопасно вызывает событие <see cref="OnPopupOpened"/>.
        /// Исключения из подписчиков перехватываются и логируются через <see cref="LogException"/>.
        /// </summary>
        private void InvokePopupOpened(PopupId popupUid)
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
        private void InvokePopupClosed(PopupId popupUid)
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

        
        /// <summary>
        /// Закрывает текущий верхний попап (если есть) и открывает указанный,
        /// помещая его на вершину стека.
        /// </summary>
        /// <remarks>Если попап с указанным <paramref name="popupUid"/> не найден — вызов игнорируется с ошибкой в лог.</remarks>
        /// <param name="popupUid">Идентификатор попапа, который нужно открыть.</param>
        public void SwitchPopup(PopupId popupUid)
        {
            if (!_popupsDict.TryGetValue(popupUid, out var popup))
            {
                Debug.LogError($"Popup {popupUid} is not exist.");
                return;
            }

            SwitchPopupInternal(popup).Forget(Debug.LogException);
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
        /// <remarks>Если попап с указанным <paramref name="popupType"/> не найден — вызов игнорируется с ошибкой в лог.</remarks>
        /// <param name="popupType">Идентификатор попапа для открытия.</param>
        public void OpenPopup(PopupId popupType)
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
                if (popup.Mount(_popupsLayer)) RegisterSafeArea(popup.SafeArea);
                
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
    }
}
