using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Screens
{
    public abstract class LoadingScreenController : MonoBehaviour
    {
        public abstract void OnMount(VisualElement parent);
        public abstract UniTask OnShow(VisualElement parent, bool instant = false);
        public abstract UniTask OnHide();
    }
}
