using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Слой вёрстки во всю полосу кадра: фон, арт, диммер — всё, что обязано доходить до выреза.
    /// </summary>
    /// <remarks>
    /// Кладётся в uxml вью на верхний уровень, рядом с <see cref="SafeLayer"/>:
    /// <code>
    /// &lt;ac:FullLayer&gt; …фон… &lt;/ac:FullLayer&gt;
    /// &lt;ac:SafeLayer&gt; …кнопки, текст… &lt;/ac:SafeLayer&gt;
    /// </code>
    /// Один файл вместо двух: при монтировании <see cref="ViewRoot"/> находит оба слоя
    /// и раздаёт отступы безопасной зоны только второму.
    /// </remarks>
    [UxmlElement]
    public partial class FullLayer : VisualElement
    {
        public FullLayer() => ViewLayerStyle.Stretch(this);
    }

    /// <summary>
    /// Слой вёрстки внутри безопасной зоны: интерактив и текст. Отступы выреза ему
    /// выставляет <c>AppRunner</c> при каждом реальном изменении экрана.
    /// </summary>
    [UxmlElement]
    public partial class SafeLayer : VisualElement
    {
        public SafeLayer() => ViewLayerStyle.Stretch(this);
    }

    internal static class ViewLayerStyle
    {
        /// <summary>
        /// Слой растянут на всю обёртку и не ловит указатель сам: иначе полноэкранный
        /// слой съел бы события до world-space панелей. Ignore не наследуется — дети пикаются.
        /// </summary>
        public static void Stretch(VisualElement layer)
        {
            layer.style.position = Position.Absolute;
            layer.style.left = 0;
            layer.style.right = 0;
            layer.style.top = 0;
            layer.style.bottom = 0;
            layer.pickingMode = PickingMode.Ignore;
        }
    }
}
