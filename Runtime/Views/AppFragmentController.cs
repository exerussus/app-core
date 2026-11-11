using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Контроллер фрагмента. Форма та же, что у <see cref="AppPageController"/>; имя класса —
    /// <c>&lt;Id&gt;FragmentController</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Initialize"/> вызывается при монтировании, когда <see cref="Root"/> уже готов.
    /// Для фрагментов с <c>unmountOnHide</c> монтирование происходит на каждый показ, поэтому
    /// и привязка элементов, и <c>Initialize</c> повторяются на каждый показ: <c>Root</c> каждый
    /// раз новый, и старые ссылки на элементы после сноса недействительны.
    /// </remarks>
    public abstract class AppFragmentController : MonoBehaviour
    {
        public VisualElement Root { get; set; }

        /// <summary>Свой фрагмент.</summary>
        public AppFragment Fragment { get; internal set; }

        /// <summary>Тексты (см. <see cref="AppPageController.Text"/>).</summary>
        protected LocBinder Text => Fragment != null ? Fragment.Text : LocBinder.Passthrough;

        /// <summary>Язык сменился, тексты уже пересобраны (см. <see cref="AppPageController"/>).</summary>
        protected virtual void OnLanguageChanged() { }

        internal void NotifyLanguageChanged() => OnLanguageChanged();

        /// <summary>Привязка именованных элементов. Переопределяется сгенерированным partial-файлом.</summary>
        protected virtual void BindElements() { }

        /// <summary>Подписки и настройка. Root и элементы уже валидны.</summary>
        public virtual void Initialize() { }

        /// <summary>Фрагмент показан в хосте.</summary>
        public virtual UniTask OnActivate() => UniTask.CompletedTask;

        /// <summary>Фрагмент скрыт (или вот-вот будет снесён).</summary>
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;

        private readonly MountScope _mountScope = new();

        /// <summary>
        /// Закрепляет подписку за вёрсткой: она освободится, когда вёрстка будет снесена
        /// (сразу после <see cref="OnUnmount"/>). Подписки из <c>Initialize</c> отдаются сюда —
        /// тогда повторное монтирование не копит их.
        /// </summary>
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
