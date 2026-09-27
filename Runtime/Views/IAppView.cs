using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    public interface IAppView
    {
        /// <summary>
        /// Корень вью. Это обёртка (<c>VisualElement</c>), внутри которой лежат слои
        /// <see cref="FullLayer"/> и <see cref="SafeLayer"/>. Поиск по нему видит оба слоя сразу.
        /// </summary>
        public VisualElement Root { get; }

        /// <summary>Вид вью.</summary>
        public ViewKind Kind { get; }

        /// <summary>Строковый id вью — тот, что лежит в реестре NavigationSettings.</summary>
        public string ViewId { get; }
    }
}
