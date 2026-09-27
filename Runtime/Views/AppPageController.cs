using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

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

        /// <summary>Привязка именованных элементов. Переопределяется сгенерированным partial-файлом.</summary>
        protected virtual void BindElements() { }

        /// <summary>При ПЕРВОМ монтировании, после инъекции и привязки элементов.</summary>
        public virtual void Initialize() { }

        public virtual UniTask OnActivate() => UniTask.CompletedTask;
        public virtual UniTask OnDeactivate() => UniTask.CompletedTask;
        public virtual UniTask OnShow() => UniTask.CompletedTask;
        public virtual UniTask OnHide() => UniTask.CompletedTask;

        internal void Setup()
        {
            BindElements();
            Initialize();
        }
    }
}
