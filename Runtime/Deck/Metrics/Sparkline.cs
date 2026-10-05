using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Спарклайн трека метрики. Рисуется векторно (Painter2D) прямо из кольца истории — без копий
    /// и без элементов на точку. Перерисовка — только когда пришла новая точка (<see cref="SetSource"/>
    /// с изменённой версией) или изменился размер.
    /// </summary>
    [UxmlElement]
    public partial class Sparkline : VisualElement
    {
        private float[] _history;
        private int _oldest;
        private int _count;

        public Sparkline()
        {
            AddToClassList("appdeck-spark");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        [UxmlAttribute]
        public Color LineColor { get; set; } = new Color(1f, 0.85f, 0.4f, 0.9f);

        /// <summary>Сменить данные. Ссылка на кольцо, не копия: рисование читает его в момент перерисовки.</summary>
        public void SetSource(float[] history, int oldest, int count)
        {
            _history = history;
            _oldest = oldest;
            _count = count;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext ctx)
        {
            if (_history == null || _count < 2) return;

            Rect rect = contentRect;
            if (rect.width <= 2f || rect.height <= 2f) return;

            int length = _history.Length;
            float min = float.MaxValue, max = float.MinValue;

            for (var i = 0; i < _count; i++)
            {
                float v = _history[(_oldest + i) % length];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            float range = max - min;
            if (range < 1e-6f)
            {
                range = 1f;
                min -= 0.5f;
            }

            Painter2D painter = ctx.painter2D;
            painter.strokeColor = LineColor;
            painter.lineWidth = 1.2f;
            painter.lineJoin = LineJoin.Round;
            painter.BeginPath();

            float stepX = rect.width / (length - 1);
            float startX = rect.xMax - (_count - 1) * stepX;

            for (var i = 0; i < _count; i++)
            {
                float v = _history[(_oldest + i) % length];
                float x = startX + i * stepX;
                float y = rect.yMax - (v - min) / range * (rect.height - 2f) - 1f;

                if (i == 0) painter.MoveTo(new Vector2(x, y));
                else painter.LineTo(new Vector2(x, y));
            }

            painter.Stroke();
        }
    }
}
