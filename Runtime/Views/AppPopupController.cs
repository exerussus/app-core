using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Контроллер попапа. Имя класса — <c>&lt;Id&gt;PopupController</c>; как и у страницы,
    /// именованные элементы приходят сгенерированным partial-файлом.
    /// </summary>
    public abstract class AppPopupController : MonoBehaviour
    {
        public VisualElement Root { get; set; }

        /// <summary>
        /// Свой попап. Проставляется им же в PreInitialize — чтобы контроллер мог, например,
        /// переключать фрагменты, не разыскивая попап через GetComponent.
        /// </summary>
        public AppPopup Popup { get; internal set; }

        /// <summary>Тексты (см. <see cref="AppPageController.Text"/>).</summary>
        protected LocBinder Text => Popup != null ? Popup.Text : LocBinder.Passthrough;

        /// <summary>Язык сменился, тексты уже пересобраны (см. <see cref="AppPageController"/>).</summary>
        protected virtual void OnLanguageChanged() { }

        internal void NotifyLanguageChanged() => OnLanguageChanged();

        /// <summary>Привязка именованных элементов. Переопределяется сгенерированным partial-файлом.</summary>
        protected virtual void BindElements() { }

        public virtual void Initialize() { }
        public virtual UniTask OnActivate()   => UniTask.CompletedTask;
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
        public virtual UniTask OnShow()       => UniTask.CompletedTask;
        public virtual UniTask OnHide()       => UniTask.CompletedTask;
        public virtual UniTask OnFocus()      => UniTask.CompletedTask;
        public virtual UniTask OnUnfocus()    => UniTask.CompletedTask;

        private readonly MountScope _mountScope = new();

        /// <summary>
        /// Закрепляет подписку за вёрсткой: она освободится, когда вёрстка будет снесена
        /// (сразу после <see cref="OnUnmount"/>). Подписки из <c>Initialize</c> отдаются сюда —
        /// тогда повторное монтирование не копит их.
        /// </summary>
        /// <remarks>
        /// Уход с вью вёрстку не сносит (она только прячется), поэтому здесь освобождение
        /// происходит при уничтожении объекта вью.
        /// </remarks>
        protected void Track(IDisposable subscription) => _mountScope.Track(subscription);

        /// <summary>Токен, отменяемый при сносе вёрстки (см. <see cref="Track"/>).</summary>
        protected CancellationToken MountToken => _mountScope.Token;

        /// <summary>
        /// Вёрстка сносится. Вызывается до освобождения подписок из <see cref="Track"/>,
        /// элементы ещё доступны.
        /// </summary>
        protected virtual void OnUnmount() { }

        internal void Setup()
        {
            _mountScope.Begin();
            BindElements();
            Initialize();
        }

        /// <summary>Снос вёрстки: <see cref="OnUnmount"/>, затем отмена токена и подписок. Идемпотентно.</summary>
        internal void ReleaseMount()
        {
            if (!_mountScope.IsMounted) return;

            try { OnUnmount(); }
            catch (Exception e) { Debug.LogException(e, this); }

            _mountScope.Release(this);
        }
    }
}
