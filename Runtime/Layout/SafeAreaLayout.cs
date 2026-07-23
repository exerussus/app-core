using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Layout
{
    /// <summary>
    /// Утилита безопасной зоны: считает отступы и применяет их к элементу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Соглашение по вёрстке: в UXML рядом лежат полноэкранный <c>background</c> и контейнер
    /// с именем <c>safeArea</c>. Фон остаётся во весь экран (в том числе под чёлкой и
    /// скруглениями), а контент живёт внутри <c>safeArea</c>, которому и раздаётся padding.
    /// </para>
    /// <para>
    /// Утилита НЕ хранит список элементов и никого не обходит: реестр и повторное применение —
    /// зона ответственности <see cref="AppRunner"/>, потому что поиск элемента на каждом кадре
    /// недопустим, а кэш должен умирать вместе со сценой. Здесь только чистый расчёт.
    /// </para>
    /// <para>
    /// Важно: рассчитанное значение фиксируется в кэше ТОЛЬКО после успешного пересчёта.
    /// Если панель ещё не привязана или layout не посчитан, метод возвращает <c>false</c>
    /// и попробует снова на следующем кадре, а не «запомнит» неприменённое состояние.
    /// </para>
    /// </remarks>
    public static class SafeAreaLayout
    {
        /// <summary>Имя контейнера безопасной зоны в UXML.</summary>
        public const string ElementName = "safeArea";

        private static Rect _lastSafeArea;
        private static ScreenOrientation _lastOrientation;
        private static int _lastWidth;
        private static int _lastHeight;

        /// <summary>Последние успешно рассчитанные отступы.</summary>
        public static SafeAreaInsets Current { get; private set; }

        /// <summary>Был ли хоть раз выполнен успешный расчёт.</summary>
        public static bool HasValue { get; private set; }

        /// <summary>
        /// Ищет контейнер безопасной зоны в поддереве. Вызывается один раз при монтировании —
        /// результат обязан кэшироваться вызывающей стороной (свойство <c>SafeArea</c>).
        /// </summary>
        /// <returns>Найденный контейнер или <c>null</c>, если вёрстка его не содержит.</returns>
        public static VisualElement Find(VisualElement root)
        {
            return root?.Q<VisualElement>(ElementName);
        }

        /// <summary>
        /// Пересчитывает отступы, если изменились безопасная зона, ориентация или разрешение.
        /// Дёшево: в обычном кадре — четыре сравнения и выход, без аллокаций.
        /// </summary>
        /// <param name="panelProbe">Любой элемент, привязанный к целевой панели (обычно корень UIDocument).</param>
        /// <returns><c>true</c>, если <see cref="Current"/> обновился и отступы надо переприменить.</returns>
        public static bool TryRefresh(VisualElement panelProbe)
        {
            var safeArea = Screen.safeArea;
            var orientation = Screen.orientation;
            var width = Screen.width;
            var height = Screen.height;

            if (HasValue
                && width == _lastWidth
                && height == _lastHeight
                && orientation == _lastOrientation
                && safeArea == _lastSafeArea) return false;

            if (panelProbe == null) return false;

            var panel = panelProbe.panel;
            if (panel == null) return false;                       // элемент ещё не в панели — повторим позже
            if (width <= 0 || height <= 0) return false;

            var resolved = panel.visualTree.resolvedStyle;
            var panelWidth = resolved.width;
            var panelHeight = resolved.height;

            // layout ещё не посчитан: сейчас деление дало бы NaN/0 и разъехавшийся UI
            if (float.IsNaN(panelWidth) || float.IsNaN(panelHeight)) return false;
            if (panelWidth <= 0f || panelHeight <= 0f) return false;

            var scaleX = panelWidth / width;
            var scaleY = panelHeight / height;

            // Screen считает Y снизу, UI Toolkit — сверху, поэтому top и bottom меняются местами.
            Current = new SafeAreaInsets(
                safeArea.xMin * scaleX,
                (width - safeArea.xMax) * scaleX,
                (height - safeArea.yMax) * scaleY,
                safeArea.yMin * scaleY);

            // Кэш фиксируем только здесь — после фактического успеха.
            _lastSafeArea = safeArea;
            _lastOrientation = orientation;
            _lastWidth = width;
            _lastHeight = height;
            HasValue = true;
            return true;
        }

        /// <summary>
        /// Сбрасывает кэш расчёта. Нужен на старте <see cref="AppRunner"/>: статика переживает
        /// перезагрузку сцены (и Play Mode без Domain Reload), а панель у новой сцены может быть
        /// другой — с другим масштабом.
        /// </summary>
        public static void Reset()
        {
            HasValue = false;
            _lastSafeArea = default;
            _lastOrientation = default;
            _lastWidth = 0;
            _lastHeight = 0;
            Current = default;
        }
    }
}
