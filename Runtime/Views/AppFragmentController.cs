using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

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

        /// <summary>Привязка именованных элементов. Переопределяется сгенерированным partial-файлом.</summary>
        protected virtual void BindElements() { }

        /// <summary>Подписки и настройка. Root и элементы уже валидны.</summary>
        public virtual void Initialize() { }

        /// <summary>Фрагмент показан в хосте.</summary>
        public virtual UniTask OnActivate() => UniTask.CompletedTask;

        /// <summary>Фрагмент скрыт (или вот-вот будет снесён).</summary>
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;

        internal void Setup()
        {
            BindElements();
            Initialize();
        }
    }
}
