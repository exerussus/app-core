using System;
using UnityEngine;

namespace Exerussus.AppCore.Layout
{
    /// <summary>
    /// Правило обрезки кадра: держать картинку в референсной пропорции, когда окно шире неё.
    /// Чистые данные, никакой камеры и никаких сайд-эффектов — из них
    /// <see cref="ScreenMetrics"/> считает долю ширины, которую занимает полоса кадра.
    /// </summary>
    public readonly struct FramePolicy : IEquatable<FramePolicy>
    {
        /// <summary>Обрезка выключена: полоса всегда во весь экран.</summary>
        public static readonly FramePolicy Disabled = default;

        /// <summary>Целевое соотношение сторон (ширина / высота). 0 — правило неактивно.</summary>
        public readonly float ReferenceAspect;

        /// <summary>
        /// Нижняя граница доли ширины. Страховка от вырожденной полосы на экстремально
        /// широком окне: лучше нарушить пропорцию, чем показать щель в несколько пикселей.
        /// </summary>
        public readonly float MinShare;

        public FramePolicy(float referenceAspect, float minShare)
        {
            ReferenceAspect = referenceAspect > 0f ? referenceAspect : 0f;
            MinShare = Mathf.Clamp(minShare, 0.05f, 1f);
        }

        public bool IsActive => ReferenceAspect > 0f;

        /// <summary>
        /// Доля ширины экрана, которую занимает кадр, для текущего соотношения сторон.
        /// Уже, чем референс, оставляем как есть: вытянутому экрану поля не нужны,
        /// разметка растягивается по высоте сама.
        /// </summary>
        public float ResolveShare(float currentAspect)
        {
            if (!IsActive || currentAspect <= ReferenceAspect) return 1f;
            return Mathf.Max(ReferenceAspect / currentAspect, MinShare);
        }

        public bool Equals(FramePolicy other)
        {
            // Точное сравнение намеренно: значения не считаются, а присваиваются целиком,
            // и любое отличие обязано инвалидировать кэш метрик.
            return ReferenceAspect.Equals(other.ReferenceAspect) && MinShare.Equals(other.MinShare);
        }

        public override bool Equals(object obj) => obj is FramePolicy other && Equals(other);

        public override int GetHashCode() => (ReferenceAspect, MinShare).GetHashCode();

        public static bool operator ==(FramePolicy a, FramePolicy b) => a.Equals(b);

        public static bool operator !=(FramePolicy a, FramePolicy b) => !a.Equals(b);
    }
}
