using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Контроллер фрагмента. Форма та же, что у <see cref="AppPageController"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Initialize"/> вызывается при монтировании, когда <see cref="Root"/> уже готов.
    /// Для фрагментов с <c>unmountOnHide</c> монтирование происходит на каждый показ, поэтому
    /// и <c>Initialize</c> вызывается на каждый показ: <c>Root</c> каждый раз новый, и старые
    /// ссылки на элементы после сноса недействительны. У обычных фрагментов — ровно один раз.
    /// </remarks>
    public abstract class AppFragmentController : MonoBehaviour
    {
        public VisualElement Root { get; set; }

        /// <summary>Привязка к вёрстке: поиск элементов, подписки. Root уже валиден.</summary>
        public virtual void Initialize() {}

        /// <summary>Фрагмент показан в хосте.</summary>
        public virtual UniTask OnActivate() => UniTask.CompletedTask;

        /// <summary>Фрагмент скрыт (или вот-вот будет снесён).</summary>
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
    }
}
