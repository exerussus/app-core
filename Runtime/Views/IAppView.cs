using UnityEngine.UIElements;
using Exerussus.AppCore.Audio;

namespace Exerussus.AppCore.Views
{
    public interface IAppView
    {
        /// <summary>
        /// Корень вью. Со сплитом вёрстки на полноэкранную и безопасную это обёртка
        /// (<c>VisualElement</c>), а не <c>TemplateContainer</c>: деревьев внутри может быть два.
        /// Поиск по нему видит оба слоя сразу.
        /// </summary>
        public VisualElement Root { get;}
        public UISoundLibrary OverrideSoundLibrary { get;}
    }
}
