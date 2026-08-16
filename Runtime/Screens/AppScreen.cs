using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Screens
{
    /// <summary>
    /// Базовый класс «скрина» — самодостаточного оверлея, живущего ВНЕ App.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ключевой инвариант (рельса): скрин НИКОГДА не касается контейнера зависимостей.
    /// Ни <c>[Inject]</c>, ни <c>container.Get</c>, ни обращений к сервисам. Всё, что ему нужно,
    /// приходит либо из инспектора (вёрстка + опциональный контроллер в наследнике),
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
        /// Опциональная вёрстка во всю полосу кадра: диммер, затемнение. Если оба дерева пусты —
        /// наследник строит оверлей кодом (<see cref="BuildFallbackBackdrop"/> +
        /// <see cref="BuildFallbackContent"/>), чтобы скрин оставался последним рубежом
        /// без зависимости от ассетов и стилей.
        /// </summary>
        [SerializeField] protected VisualTreeAsset fullTree;

        /// <summary>Опциональная вёрстка внутри безопасной зоны: контент скрина.</summary>
        [SerializeField] protected VisualTreeAsset safeTree;

        private VisualElement _parent;
        private VisualElement _root;
        private VisualElement _full;
        private VisualElement _safe;
        private bool _mounted;

        /// <summary>Виден ли скрин прямо сейчас.</summary>
        public bool IsVisible { get; private set; }

        /// <summary>Корень визуала скрина. Валиден только после <see cref="Mount"/>. Ищет по обоим слоям.</summary>
        protected VisualElement Root => _root;

        /// <summary>Слой во всю полосу кадра — диммер и фон.</summary>
        protected VisualElement FullRoot => _full;

        /// <summary>Слой безопасной зоны — контент.</summary>
        protected VisualElement SafeRoot => _safe;

        /// <summary>
        /// Монтирует скрин в переданный слой. Идемпотентно: повторный вызов — no-op.
        /// Вызывается один раз до старта boot-машины.
        /// </summary>
        public void Mount(VisualElement parent)
        {
            if (_mounted) return;
            _mounted = true;
            _parent = parent;

            // Обёртка во всю полосу кадра; внутри два слоя. Диммер обязан доходить до выреза,
            // поэтому живёт в полноэкранном слое, а контент — в безопасном.
            _root = new VisualElement { name = GetType().Name };
            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.right = 0;
            _root.style.top = 0;
            _root.style.bottom = 0;
            _root.style.display = DisplayStyle.None;
            _root.pickingMode = PickingMode.Ignore;

            // Fallback строим целиком и только когда вёрстки нет вовсе: наполовину код,
            // наполовину uxml — это две разные системы координат в одном скрине.
            var noTrees = fullTree == null && safeTree == null;

            VisualElement fullContent = fullTree != null ? fullTree.Instantiate() : null;
            VisualElement safeContent = safeTree != null ? safeTree.Instantiate() : null;

            if (noTrees)
            {
                fullContent = BuildFallbackBackdrop();
                safeContent = BuildFallbackContent();
            }

            _full = AddLayer("__full", fullContent);
            _safe = AddLayer("__safe", safeContent);

            _parent.Add(_root);

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
        /// Строит фон кодом, когда вёрстка не задана: диммер во всю полосу кадра.
        /// Это последний рубеж — он не должен зависеть ни от uxml, ни от стилей, ни от контейнера.
        /// </summary>
        protected abstract VisualElement BuildFallbackBackdrop();

        /// <summary>
        /// Строит контент кодом — он ляжет в безопасную зону, поверх фона.
        /// Разделение обязательно: затемнение должно доходить до выреза, а текст и кнопки — нет.
        /// </summary>
        protected abstract VisualElement BuildFallbackContent();

        /// <summary>Добавляет слой с содержимым. <c>null</c>-содержимое — слоя не будет.</summary>
        private VisualElement AddLayer(string suffix, VisualElement content)
        {
            if (content == null) return null;

            var layer = new VisualElement { name = GetType().Name + suffix };
            layer.style.position = Position.Absolute;
            layer.style.left = 0;
            layer.style.right = 0;
            layer.style.top = 0;
            layer.style.bottom = 0;
            layer.pickingMode = PickingMode.Ignore;

            content.style.flexGrow = 1;
            layer.Add(content);
            _root.Add(layer);
            return layer;
        }

        /// <summary>Отступы безопасной зоны — их раздаёт AppRunner при изменении экрана.</summary>
        internal void ApplySafeInsets(float left, float right, float top, float bottom)
        {
            if (_safe == null) return;

            _safe.style.left = left;
            _safe.style.right = right;
            _safe.style.top = top;
            _safe.style.bottom = bottom;
        }

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
        /// Контейнер контента скрина. Безопасную зону раздаёт <see cref="AppRunner"/> общему
        /// контейнеру слоёв, поэтому здесь остаётся только центрирование содержимого.
        /// </summary>
        protected static VisualElement BuildContentBox()
        {
            var box = new VisualElement { name = "content" };
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
