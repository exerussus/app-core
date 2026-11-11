using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Контроллер страницы. Имя класса — <c>&lt;Id&gt;PageController</c>, файл лежит рядом с uxml
    /// страницы. Генератор создаёт его <c>partial</c>: вторая половина,
    /// <c>&lt;Id&gt;PageController.Elements.cs</c>, держит поля всех именованных элементов
    /// вёрстки и перегенерируется при каждом импорте uxml.
    /// </summary>
    public abstract class AppPageController : MonoBehaviour
    {
        public VisualElement Root { get; set; }

        /// <summary>
        /// Своя страница. Проставляется ею же в PreInitialize — чтобы контроллер мог, например,
        /// переключать фрагменты, не разыскивая страницу через GetComponent.
        /// </summary>
        public AppPage Page { get; internal set; }

        /// <summary>
        /// Тексты страницы (<see cref="LocBinder"/>): <c>Text.Set(label, "^KEY")</c> ставит строку,
        /// которая сменится вместе с языком. Ключи вёрстки переведены до <see cref="Initialize"/>.
        /// </summary>
        protected LocBinder Text => Page != null ? Page.Text : LocBinder.Passthrough;

        /// <summary>
        /// Язык сменился, тексты вёрстки и заданные через <see cref="Text"/> уже пересобраны.
        /// Переопределяется, если контроллер собирает строки сам (списки, форматирование).
        /// </summary>
        protected virtual void OnLanguageChanged() { }

        internal void NotifyLanguageChanged() => OnLanguageChanged();

        /// <summary>Привязка именованных элементов. Переопределяется сгенерированным partial-файлом.</summary>
        protected virtual void BindElements() { }

        /// <summary>При ПЕРВОМ монтировании, после инъекции и привязки элементов.</summary>
        public virtual void Initialize() { }

        public virtual UniTask OnActivate() => UniTask.CompletedTask;
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
        public virtual UniTask OnShow() => UniTask.CompletedTask;
        public virtual UniTask OnHide() => UniTask.CompletedTask;

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
