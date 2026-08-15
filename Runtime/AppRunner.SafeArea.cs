using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Layout;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Кадр и безопасная зона: единственная точка их применения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Раньше отступы раздавались по реестру контейнеров <c>safeArea</c>, найденных в вёрстке
    /// каждой страницы, попапа и скрина. Теперь всё вжимает один общий контейнер
    /// <c>contentRoot</c>, внутри которого живут все три слоя. Страницы и попапы и так
    /// растянуты на 100%, поэтому они получают и безопасную зону, и полосу кадра
    /// без единого действия со своей стороны.
    /// </para>
    /// <para>
    /// Полоса кадра и безопасная зона складываются в ОДНО смещение контейнера: два вложенных
    /// элемента дали бы два прохода раскладки там, где достаточно одного.
    /// </para>
    /// </remarks>
    public partial class AppRunner
    {
        /// <summary>Общий контейнер слоёв. Именно ему раздаются отступы кадра и безопасной зоны.</summary>
        private VisualElement _contentRoot;

        /// <summary>
        /// Чёрные поля по бокам при обрезке кадра. Закрывают то, что основная камера не рисует.
        /// Панель — screen-space overlay, она рендерится на весь экран независимо от
        /// <c>camera.rect</c>, поэтому вторая камера-подложка не нужна: поля закрашивает UI.
        /// </summary>
        private VisualElement _frameMaskLeft;

        private VisualElement _frameMaskRight;

        /// <summary>Референсное разрешение панели, снятое ДО подмены настроек. Источник пропорции кадра.</summary>
        private Vector2Int _referenceResolution;

        private PanelSettings _panelSettingsOriginal;

        /// <summary>
        /// Рантайм-копия настроек панели. Ассет на диске не трогаем принципиально: правки
        /// ScriptableObject-ассета Unity НЕ откатывает при выходе из Play (в отличие от правок
        /// сцены), поэтому один краш или остановка домена — и в репозиторий уезжает
        /// referenceResolution, посчитанный под чьё-то окно. Плюс ассет может быть общим
        /// для нескольких панелей.
        /// </summary>
        private PanelSettings _panelSettingsClone;

        private int _appliedReferenceWidth = -1;

        /// <summary>
        /// Снимает референсное разрешение и подставляет панели рантайм-копию настроек.
        /// Вызывается в Awake ДО регистрации UI-колбэка, чтобы первый же корень пришёл уже
        /// от копии, а не от исходного ассета.
        /// </summary>
        private void SetupFramePanelSettings()
        {
            if (!frameToReferenceAspect || _panelRenderer == null) return;

            var settings = _panelRenderer.panelSettings;
            if (settings == null) return;

            _referenceResolution = settings.referenceResolution;
            if (_referenceResolution.x <= 0 || _referenceResolution.y <= 0) return;

            _panelSettingsOriginal = settings;
            _panelSettingsClone = Instantiate(settings);
            _panelSettingsClone.name = settings.name + " (Runtime Frame)";
            _panelRenderer.panelSettings = _panelSettingsClone;
        }

        /// <summary>Возвращает панели исходные настройки и убивает копию.</summary>
        private void ReleaseFramePanelSettings()
        {
            if (_panelSettingsClone == null) return;

            // Ссылку возвращаем ДО уничтожения копии, иначе рендерер на кадр останется
            // с уничтоженным объектом.
            if (_panelRenderer != null && _panelSettingsOriginal != null)
                _panelRenderer.panelSettings = _panelSettingsOriginal;

            Destroy(_panelSettingsClone);
            _panelSettingsClone = null;
        }

        /// <summary>
        /// Растягивает референсное разрешение копии на ширину поля кадра.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Без этого полоса имеет референсную ПРОПОРЦИЮ, но не референсный РАЗМЕР: при
        /// <c>ScreenMatchMode.MatchWidthOrHeight</c> масштаб панели — взвешенная смесь ширины
        /// и высоты, и на широком окне полоса меряет себя в меньшем числе логических точек.
        /// Вёрстка на процентах этого не замечает, а фиксированные px становятся крупнее
        /// относительно кадра.
        /// </para>
        /// <para>
        /// Расширив референс в <c>1/share</c> раз, получаем масштаб <c>H / refH</c> при ЛЮБОМ
        /// match: полоса выходит ровно <c>refW × refH</c> логических точек. Доля <c>share</c>
        /// зависит только от размеров экрана, а не от панели, поэтому обратной связи нет —
        /// после записи размер панели меняется, метрики пересчитываются один раз, целевая
        /// ширина совпадает с уже применённой, и запись не повторяется.
        /// </para>
        /// </remarks>
        private void ApplyFrameReferenceResolution()
        {
            if (_panelSettingsClone == null) return;

            var share = ScreenMetrics.Share;
            var width = share >= 1f
                ? _referenceResolution.x
                : Mathf.RoundToInt(_referenceResolution.x / share);

            if (width == _appliedReferenceWidth) return;
            _appliedReferenceWidth = width;

            _panelSettingsClone.referenceResolution = new Vector2Int(width, _referenceResolution.y);
        }

        /// <summary>
        /// Покадровая проверка метрик экрана. В обычном кадре — несколько сравнений и выход,
        /// без аллокаций и без единой записи в стиль. Переприменение происходит только при
        /// реальном изменении экрана, ориентации, размера панели или правила кадра.
        /// </summary>
        private void TickScreenMetrics()
        {
            if (!ScreenMetrics.TryRefresh(_root)) return;
            ApplyScreenMetrics();
        }

        /// <summary>Раскладывает посчитанные метрики в границы контейнера и ширину полей.</summary>
        private void ApplyScreenMetrics()
        {
            if (_contentRoot == null) return;

            var bar = ScreenMetrics.FrameBar;
            var insets = ScreenMetrics.Insets;

            // Сдвигаем сам контейнер, а НЕ раздаём ему padding. Padding здесь не работает:
            // у абсолютно спозиционированных детей (а все три слоя такие) точка отсчёта —
            // padding box родителя, и отступ лежит ВНУТРИ него. Слои с left:0 встали бы
            // по внутренней границе рамки, а не отступа, и содержимое уехало бы под поля кадра.
            _contentRoot.style.left = bar + insets.Left;
            _contentRoot.style.right = bar + insets.Right;
            _contentRoot.style.top = insets.Top;
            _contentRoot.style.bottom = insets.Bottom;

            if (_frameMaskLeft == null || _frameMaskRight == null) return;

            // Без обрезки поля не просто нулевой ширины, а сняты из раскладки:
            // display:none выключает и отрисовку, и участие в пике.
            var framed = bar > 0f;
            var display = framed ? DisplayStyle.Flex : DisplayStyle.None;

            _frameMaskLeft.style.width = bar;
            _frameMaskRight.style.width = bar;
            _frameMaskLeft.style.display = display;
            _frameMaskRight.style.display = display;

            // Последним: запись меняет размер панели, а значит породит ещё один пересчёт метрик.
            // Он сойдётся на следующем кадре — целевая ширина будет уже применённой.
            ApplyFrameReferenceResolution();
        }
    }
}
