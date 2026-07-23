using UnityEngine.UIElements;

namespace Exerussus.AppCore.Layout
{
    /// <summary>
    /// Отступы безопасной зоны, пересчитанные из пикселей экрана в пиксели панели UI Toolkit.
    /// </summary>
    public readonly struct SafeAreaInsets
    {
        public readonly float Left;
        public readonly float Right;
        public readonly float Top;
        public readonly float Bottom;

        public SafeAreaInsets(float left, float right, float top, float bottom)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }

        /// <summary>
        /// Раскладывает отступы в padding элемента. Без аллокаций: присваиваются структуры
        /// <see cref="StyleLength"/> через неявное приведение из float.
        /// </summary>
        public void ApplyTo(VisualElement element)
        {
            if (element == null) return;

            element.style.paddingLeft = Left;
            element.style.paddingRight = Right;
            element.style.paddingTop = Top;
            element.style.paddingBottom = Bottom;
        }
    }
}
