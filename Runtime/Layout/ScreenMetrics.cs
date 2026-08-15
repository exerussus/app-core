using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Layout
{
    /// <summary>
    /// Единственное место, которое читает <see cref="Screen"/>. Считает долю кадра и отступы
    /// безопасной зоны в логических точках панели.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Опрос дешёвый, реакция дорогая: чтение <c>Screen</c> — четыре нативных геттера без
    /// аллокаций, а вот запись в <c>style.padding*</c> тянет проход раскладки. Поэтому здесь
    /// только расчёт и признак «что-то изменилось»; применять — задача <see cref="AppRunner"/>.
    /// </para>
    /// <para>
    /// Кэш фиксируется ТОЛЬКО после успешного расчёта. Если панель ещё не привязана или layout
    /// не посчитан, метод возвращает <c>false</c> и попробует снова на следующем кадре, а не
    /// «запомнит» неприменённое состояние.
    /// </para>
    /// <para>
    /// Безопасная зона считается относительно ПОЛОСЫ кадра, а не всего экрана. При обрезке вырез
    /// физически оказывается в чёрных полях, и отступ под него не нужен — пересечение это
    /// разруливает само, а при <c>share == 1</c> формула вырождается в обычную.
    /// </para>
    /// </remarks>
    public static class ScreenMetrics
    {
        private static Rect _lastSafeArea;
        private static ScreenOrientation _lastOrientation;
        private static int _lastWidth;
        private static int _lastHeight;
        private static float _lastPanelWidth;
        private static float _lastPanelHeight;
        private static FramePolicy _lastPolicy;

        /// <summary>Правило обрезки кадра. Смена инвалидирует кэш.</summary>
        public static FramePolicy Policy
        {
            get => _policy;
            set
            {
                if (_policy == value) return;
                _policy = value;
                Invalidate();
            }
        }

        private static FramePolicy _policy;

        /// <summary>Был ли хоть раз выполнен успешный расчёт.</summary>
        public static bool HasValue { get; private set; }

        /// <summary>Растёт на каждый успешный пересчёт. Дешёвый способ узнать «данные другие».</summary>
        public static int Version { get; private set; }

        /// <summary>Доля ширины экрана, занятая кадром. 1 — обрезки нет.</summary>
        public static float Share { get; private set; } = 1f;

        /// <summary>Ширина одного чёрного поля в логических точках панели. 0 — обрезки нет.</summary>
        public static float FrameBar { get; private set; }

        /// <summary>Отступы безопасной зоны в логических точках, относительно полосы кадра.</summary>
        public static SafeAreaInsets Insets { get; private set; }

        /// <summary>
        /// Пересчитывает метрики, если изменились экран, ориентация, размер панели или правило кадра.
        /// В обычном кадре — несколько сравнений и выход, без аллокаций.
        /// </summary>
        /// <param name="panelProbe">Любой элемент, привязанный к целевой панели (корень PanelRenderer).</param>
        /// <returns><c>true</c>, если данные обновились и их надо переприменить.</returns>
        public static bool TryRefresh(VisualElement panelProbe)
        {
            var safeArea = Screen.safeArea;
            var orientation = Screen.orientation;
            var width = Screen.width;
            var height = Screen.height;

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

            if (HasValue
                && width == _lastWidth
                && height == _lastHeight
                && orientation == _lastOrientation
                && safeArea == _lastSafeArea
                && panelWidth.Equals(_lastPanelWidth)
                && panelHeight.Equals(_lastPanelHeight)
                && _policy == _lastPolicy) return false;

            // Логических точек на физический пиксель. Полоса занимает долю share и по экрану,
            // и по панели, поэтому обрезка на коэффициент не влияет — только на то,
            // какая часть выреза попадает в кадр.
            var scaleX = panelWidth / width;
            var scaleY = panelHeight / height;

            var share = _policy.ResolveShare((float)width / height);

            // Полоса кадра в физических пикселях: по центру, во всю высоту.
            var bandLeft = (1f - share) * 0.5f * width;
            var bandRight = bandLeft + share * width;

            // Screen считает Y снизу, UI Toolkit — сверху, поэтому top и bottom меняются местами.
            Insets = new SafeAreaInsets(
                Mathf.Max(0f, safeArea.xMin - bandLeft) * scaleX,
                Mathf.Max(0f, bandRight - safeArea.xMax) * scaleX,
                Mathf.Max(0f, height - safeArea.yMax) * scaleY,
                Mathf.Max(0f, safeArea.yMin) * scaleY);

            Share = share;
            FrameBar = bandLeft * scaleX;

            // Кэш фиксируем только здесь — после фактического успеха.
            _lastSafeArea = safeArea;
            _lastOrientation = orientation;
            _lastWidth = width;
            _lastHeight = height;
            _lastPanelWidth = panelWidth;
            _lastPanelHeight = panelHeight;
            _lastPolicy = _policy;
            HasValue = true;
            Version++;
            return true;
        }

        /// <summary>
        /// Сбрасывает кэш расчёта. Нужен на старте <see cref="AppRunner"/>: статика переживает
        /// перезагрузку сцены (и Play Mode без Domain Reload), а панель у новой сцены может быть
        /// другой — с другим масштабом.
        /// </summary>
        public static void Invalidate()
        {
            HasValue = false;
            _lastSafeArea = default;
            _lastOrientation = default;
            _lastWidth = 0;
            _lastHeight = 0;
            _lastPanelWidth = 0f;
            _lastPanelHeight = 0f;
            _lastPolicy = default;
            Share = 1f;
            FrameBar = 0f;
            Insets = default;
        }
    }
}
