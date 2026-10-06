using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Build;
using Exerussus.AppCore.Layout;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Строка версии: мелкий полупрозрачный текст в углу полосы кадра. Не ловит указатель.
    /// </summary>
    /// <remarks>
    /// Нужна прежде всего прототипам: когда что-то «не обновилось» на хостинге, по строке
    /// сразу видно, какая сборка реально запущена, и её можно сверить с <c>version.json</c>
    /// рядом с билдом.
    /// </remarks>
    public partial class AppRunner
    {
        private Label _versionOverlay;
        private bool _versionLeft;
        private bool _versionTop;

        /// <summary>Элемент строки версии. <c>null</c>, если оверлей выключен.</summary>
        public Label VersionOverlay => _versionOverlay;

        private void SetupVersionOverlay()
        {
            if (buildInfo == null || !buildInfo.ShouldShowOverlay) return;

            _versionOverlay = new Label(buildInfo.FormatOverlay()) { name = "versionOverlay" };
            _versionOverlay.pickingMode = PickingMode.Ignore;

            var style = _versionOverlay.style;
            style.position = Position.Absolute;
            style.fontSize = buildInfo.OverlayFontSize;
            style.color = new Color(1f, 1f, 1f, buildInfo.OverlayOpacity);
            style.unityTextAlign = TextAnchor.MiddleLeft;
            style.marginLeft = style.marginRight = style.marginTop = style.marginBottom = 0;
            style.paddingLeft = style.paddingRight = 6;
            style.paddingTop = style.paddingBottom = 3;

            var corner = buildInfo.OverlayCorner;
            _versionLeft = corner is VersionOverlayCorner.TopLeft or VersionOverlayCorner.BottomLeft;
            _versionTop = corner is VersionOverlayCorner.TopLeft or VersionOverlayCorner.TopRight;

            ApplyVersionOverlayInsets(ScreenMetrics.HasValue ? ScreenMetrics.Insets : default);

            _contentRoot.Add(_versionOverlay);
            _versionOverlay.BringToFront();
        }

        /// <summary>
        /// Строка версии — внутри безопасной зоны: её угол отступает от выреза и скруглений на отступ
        /// своей стороны. Отступы — относительно полосы кадра, а строка живёт в ней же (contentRoot).
        /// </summary>
        private void ApplyVersionOverlayInsets(in SafeAreaInsets insets)
        {
            if (_versionOverlay == null) return;

            var style = _versionOverlay.style;

            if (_versionLeft) style.left = insets.Left;
            else style.right = insets.Right;

            if (_versionTop) style.top = insets.Top;
            else style.bottom = insets.Bottom;
        }

        /// <summary>Поднимает строку версии над только что показанным скрином.</summary>
        internal void KeepVersionOverlayOnTop()
        {
            _versionOverlay?.BringToFront();
        }
    }
}
