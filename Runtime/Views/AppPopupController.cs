using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

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

        /// <summary>Привязка именованных элементов. Переопределяется сгенерированным partial-файлом.</summary>
        protected virtual void BindElements() { }

        public virtual void Initialize() { }
        public virtual UniTask OnActivate()   => UniTask.CompletedTask;
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
        public virtual UniTask OnShow()       => UniTask.CompletedTask;
        public virtual UniTask OnHide()       => UniTask.CompletedTask;
        public virtual UniTask OnFocus()      => UniTask.CompletedTask;
        public virtual UniTask OnUnfocus()    => UniTask.CompletedTask;

        internal void Setup()
        {
            BindElements();
            Initialize();
        }
    }
}
