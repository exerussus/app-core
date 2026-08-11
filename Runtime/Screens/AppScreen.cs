using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Layout;

namespace Exerussus.AppCore.Screens
{
    /// <summary>
    /// Базовый класс «скрина» — самодостаточного оверлея, живущего ВНЕ App.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ключевой инвариант (рельса): скрин НИКОГДА не касается контейнера зависимостей.
    /// Ни <c>[Inject]</c>, ни <c>container.Get</c>, ни обращений к сервисам. Всё, что ему нужно,
    /// приходит либо из инспектора (<see cref="visualTree"/> + опциональный контроллер в наследнике),
    /// либо аргументами в его собственный <c>Show(...)</c>.
    /// </para>
    /// <para>
    /// Именно поэтому скрин доступен на ЛЮБОМ этапе инициализации App — в том числе когда
    /// падение произошло в самой регистрации DI и контейнера фактически ещё нет. Скрин
    /// монтируется <see cref="AppRunner"/> в <c>SetupUiLayers</c> ДО старта boot-машины.
    /// </para>
    /// <para>
    /// Отличие от System Popup: попап регистрируется в App, проходит DI-инъекцию и участвует
    /// в навигации/стеке. Скрин — нет. Loading/Error/Critical — это скрины.
    /// </para>
    /// </remarks>
    public abstract class AppScreen : MonoBehaviour
    {
        /// <summary>
        /// Опциональный UXML. Если задан — монтируется как визуал скрина.
        /// Если пуст — наследник обязан построить оверлей кодом (<see cref="BuildFallback"/>),
        /// чтобы скрин оставался последним рубежом без зависимости от ассетов/стилей.
        /// </summary>
        [SerializeField] protected VisualTreeAsset visualTree;

        private VisualElement _parent;
        private VisualElement _root;
        private bool _mounted;

        /// <summary>Виден ли скрин прямо сейчас.</summary>
        public bool IsVisible { get; private set; }

        /// <summary>Корень визуала скрина. Валиден только после <see cref="Mount"/>.</summary>
        protected VisualElement Root => _root;

        /// <summary>
        /// Контейнер безопасной зоны скрина (элемент с именем <c>safeArea</c>).
        /// Кэшируется при монтировании. Диммер/фон скрина сознательно остаётся во весь экран,
        /// а внутрь безопасной зоны убирается только контент.
        /// </summary>
        public VisualElement SafeArea { get; private set; }

        /// <summary>
        /// Монтирует скрин в переданный слой. Идемпотентно: повторный вызов — no-op.
        /// Вызывается один раз до старта boot-машины.
        /// </summary>
        public void Mount(VisualElement parent)
        {
            if (_mounted) return;
            _mounted = true;
            _parent = parent;

            if (visualTree != null)
            {
                // Имя корню даёт сам Instantiate() — по имени ассета вёрстки.
                _root = visualTree.Instantiate();
            }
            else
            {
                _root = BuildFallback();
                // А вот код-построенный корень — это диммер с общим для всех скринов именем
                // screen-dimmer: в дереве screensLayer они были бы неразличимы. Уточняем до типа.
                _root.name = GetType().Name;
            }

            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.right = 0;
            _root.style.top = 0;
            _root.style.bottom = 0;
            _root.style.display = DisplayStyle.None;
            _root.pickingMode = PickingMode.Ignore;
            _parent.Add(_root);

            SafeArea = SafeAreaLayout.Find(_root);

            OnMounted(_root);
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Поднимает скрин на самый верх своего слоя и делает видимым.
        /// Приоритет между скринами обеспечивается порядком вызова <c>BringToFront</c>
        /// в <see cref="AppRunner"/> (Loading &lt; Error &lt; Critical).
        /// </summary>
        protected void Reveal()
        {
            gameObject.SetActive(true);
            _root.style.display = DisplayStyle.Flex;
            _root.pickingMode = PickingMode.Position; // перехватываем ввод — под скрином кликать нельзя
            _root.BringToFront();
            IsVisible = true;
        }

        /// <summary>Скрывает скрин и возвращает управление слою под ним.</summary>
        public virtual UniTask Hide()
        {
            if (!IsVisible) return UniTask.CompletedTask;
            _root.style.display = DisplayStyle.None;
            _root.pickingMode = PickingMode.Ignore;
            IsVisible = false;
            gameObject.SetActive(false);
            return UniTask.CompletedTask;
        }

        /// <summary>
        /// Вызывается один раз после монтирования визуала. Здесь наследник находит свои
        /// элементы, вешает обработчики кнопок и (если есть) инициализирует контроллер.
        /// </summary>
        protected abstract void OnMounted(VisualElement root);

        /// <summary>
        /// Строит визуал кодом, когда <see cref="visualTree"/> не задан. Это последний рубеж:
        /// он не должен зависеть ни от uxml, ни от стилей, ни от контейнера.
        /// </summary>
        protected abstract VisualElement BuildFallback();

        // Утилита для код-построенных оверлеев наследников.
        protected static VisualElement BuildDimmer()
        {
            var dim = new VisualElement { name = "screen-dimmer" };
            dim.style.flexGrow = 1;
            dim.style.backgroundColor = new Color(0f, 0f, 0f, 0.85f);
            dim.style.justifyContent = Justify.Center;
            dim.style.alignItems = Align.Center;
            return dim;
        }

        /// <summary>
        /// Контейнер контента, помеченный как безопасная зона. Диммер под ним намеренно
        /// остаётся во весь экран — затемнение обязано доходить до краёв, включая чёлку.
        /// </summary>
        protected static VisualElement BuildSafeAreaBox()
        {
            var box = new VisualElement { name = SafeAreaLayout.ElementName };
            box.style.flexGrow = 1;
            box.style.justifyContent = Justify.Center;
            box.style.alignItems = Align.Center;
            return box;
        }

        protected static Label BuildMessageLabel(string text)
        {
            var label = new Label(text) { name = "screen-message" };
            label.style.color = Color.white;
            label.style.fontSize = 20;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.maxWidth = Length.Percent(80);
            label.style.marginBottom = 16;
            return label;
        }

        protected static Button BuildButton(string text)
        {
            var btn = new Button { text = text };
            btn.style.minWidth = 140;
            btn.style.height = 44;
            btn.style.marginLeft = 6;
            btn.style.marginRight = 6;
            return btn;
        }
    }
}
