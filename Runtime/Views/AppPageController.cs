using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    public abstract class AppPageController : MonoBehaviour
    {
        public VisualElement Root { get; set; }
        public virtual void Initialize() {}
        public virtual UniTask OnActivate() => UniTask.CompletedTask;
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
        public virtual UniTask OnShow() => UniTask.CompletedTask;
        public virtual UniTask OnHide() => UniTask.CompletedTask;
    }
}
