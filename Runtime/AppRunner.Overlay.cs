using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Build;

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
            var left = corner is VersionOverlayCorner.TopLeft or VersionOverlayCorner.BottomLeft;
            var top = corner is VersionOverlayCorner.TopLeft or VersionOverlayCorner.TopRight;

            if (left) style.left = 0;
            else style.right = 0;

            if (top) style.top = 0;
            else style.bottom = 0;

            _contentRoot.Add(_versionOverlay);
            _versionOverlay.BringToFront();
        }

        /// <summary>Поднимает строку версии над только что показанным скрином.</summary>
        internal void KeepVersionOverlayOnTop()
        {
            _versionOverlay?.BringToFront();
        }
    }
}
