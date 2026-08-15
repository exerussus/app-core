using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    public abstract class AppPopupController : MonoBehaviour
    {
        public VisualElement Root { get; set; }

        /// <summary>
        /// Свой попап. Проставляется им же в PreInitialize — чтобы контроллер мог, например,
        /// переключать фрагменты, не разыскивая попап через GetComponent.
        /// </summary>
        public AppPopup Popup { get; internal set; }

        public virtual void Initialize() { }
        public virtual UniTask OnActivate()   => UniTask.CompletedTask;
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
        public virtual UniTask OnShow()       => UniTask.CompletedTask;
        public virtual UniTask OnHide()       => UniTask.CompletedTask;
        public virtual UniTask OnFocus()      => UniTask.CompletedTask;
        public virtual UniTask OnUnfocus()    => UniTask.CompletedTask;
    }
}
