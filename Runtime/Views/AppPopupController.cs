using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    public abstract class AppPopupController : MonoBehaviour
    {
        public VisualElement Root { get; set; }
        public virtual void Initialize() { }
        public virtual UniTask OnActivate()   => UniTask.CompletedTask;
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
        public virtual UniTask OnShow()       => UniTask.CompletedTask;
        public virtual UniTask OnHide()       => UniTask.CompletedTask;
        public virtual UniTask OnFocus()      => UniTask.CompletedTask;
        public virtual UniTask OnUnfocus()    => UniTask.CompletedTask;
    }
}
