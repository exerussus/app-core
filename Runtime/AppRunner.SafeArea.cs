using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Layout;
using Exerussus.AppCore.Screens;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Кадр и безопасная зона: единственная точка их применения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Два уровня вжатия. <c>contentRoot</c> — полоса кадра: только поле обрезки, ничего больше;
    /// в ней живут слои страниц, попапов и скринов, и всё, что должно доходить до выреза
    /// (фон, диммер), рисуется именно тут. Безопасную зону получает отдельный слой <c>safe</c>
    /// внутри каждого вью — по реестру, который наполняется при монтировании.
    /// </para>
    /// <para>
    /// Разделять пришлось именно потому, что одним отступом это не описать: фон обязан
    /// доходить до выреза, а текст и кнопки — нет. Одним контейнером получается либо то, либо другое.
    /// </para>
    /// </remarks>
    public partial class AppRunner
    {
        /// <summary>Общий контейнер слоёв — полоса кадра. Безопасную зону получают слои внутри вью.</summary>
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
        /// Безопасные слои смонтированных вью. Реестр вернулся вместе со сплитом вёрстки:
        /// отступы выреза теперь получает не общий контейнер, а слой <c>safe</c> внутри
        /// каждого вью — иначе фон страницы не смог бы доходить до выреза.
        /// Список только растёт: вью не размонтируются до конца сессии.
        /// </summary>
        private readonly List<ViewRoot> _safeAreaViews = new();

        /// <summary>Скрины идут отдельным списком: у них своя обёртка, не <see cref="ViewRoot"/>.</summary>
        private readonly List<AppScreen> _safeAreaScreens = new();

        /// <summary>
        /// Ставит вью на раздачу отступов безопасной зоны. Вызывается при монтировании,
        /// ровно один раз на вью. Текущие отступы применяются сразу — иначе вью, смонтированное
        /// после последнего изменения экрана, ждало бы следующего.
        /// </summary>
        internal void RegisterSafeArea(ViewRoot view)
        {
            if (view == null || view.Safe == null) return;

            _safeAreaViews.Add(view);

            if (!ScreenMetrics.HasValue) return;
            var i = ScreenMetrics.Insets;
            view.ApplySafeInsets(i.Left, i.Right, i.Top, i.Bottom);
        }

        /// <inheritdoc cref="RegisterSafeArea(ViewRoot)"/>
        internal void RegisterSafeArea(AppScreen screen)
        {
            if (screen == null) return;

            _safeAreaScreens.Add(screen);

            if (!ScreenMetrics.HasValue) return;
            var i = ScreenMetrics.Insets;
            screen.ApplySafeInsets(i.Left, i.Right, i.Top, i.Bottom);
        }

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

            // contentRoot вжимается ТОЛЬКО полем кадра: это полоса, а не безопасная зона.
            // Безопасную зону получают отдельные слои внутри каждого вью — иначе фон страницы
            // не смог бы доходить до выреза.
            // Сдвигаем границами, а не padding: у абсолютно спозиционированных детей точка
            // отсчёта — padding box родителя, и отступ лежит ВНУТРИ него.
            _contentRoot.style.left = bar;
            _contentRoot.style.right = bar;
            _contentRoot.style.top = 0;
            _contentRoot.style.bottom = 0;

            // Безопасные слои: один проход по реестру смонтированных вью, только по факту
            // изменения экрана. Список короткий (страницы, попапы, скрины) и только растёт.
            for (var i = 0; i < _safeAreaViews.Count; i++)
                _safeAreaViews[i].ApplySafeInsets(insets.Left, insets.Right, insets.Top, insets.Bottom);

            for (var i = 0; i < _safeAreaScreens.Count; i++)
                _safeAreaScreens[i].ApplySafeInsets(insets.Left, insets.Right, insets.Top, insets.Bottom);

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
