using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    public abstract class AppPageController : MonoBehaviour
    {
        public VisualElement Root { get; set; }

        /// <summary>
        /// Своя страница. Проставляется ею же в PreInitialize — чтобы контроллер мог, например,
        /// переключать фрагменты, не разыскивая страницу через GetComponent.
        /// </summary>
        public AppPage Page { get; internal set; }
        public virtual void Initialize() {}
        public virtual UniTask OnActivate() => UniTask.CompletedTask;
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
        public virtual UniTask OnShow() => UniTask.CompletedTask;
        public virtual UniTask OnHide() => UniTask.CompletedTask;
    }
}
