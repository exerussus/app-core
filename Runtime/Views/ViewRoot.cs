using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Обёртка вью: корень во всю полосу кадра плюс два слоя внутри.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Полноэкранная вёрстка и вёрстка безопасной зоны — это два РАЗНЫХ дерева, а не один
    /// UXML с отступами. Фон, диммер и всё, что обязано доходить до выреза, живёт в
    /// <see cref="Full"/>; интерактив и текст — в <see cref="Safe"/>, которому
    /// <see cref="AppRunner"/> раздаёт отступы безопасной зоны.
    /// </para>
    /// <para>
    /// «Во весь экран» здесь означает «во всю полосу кадра», а не весь физический экран:
    /// за полосой лежат чёрные поля обрезки, они рисуются поверх всего намеренно,
    /// и содержимому вью там делать нечего.
    /// </para>
    /// <para>
    /// Корень — обычный <c>VisualElement</c>, а не <c>TemplateContainer</c>: деревьев теперь
    /// два, и ни одно из них не является корнем. Зато <c>Root.Q</c> видит оба сразу, поэтому
    /// хуки, кнопки и хосты фрагментов ищутся одним проходом независимо от того, в каком
    /// слое их положил автор вёрстки.
    /// </para>
    /// </remarks>
    public sealed class ViewRoot
    {
        /// <summary>Корень вью. Занимает всю полосу кадра.</summary>
        public VisualElement Root { get; private set; }

        /// <summary>Слой полноэкранной вёрстки. <c>null</c>, если дерево не задано.</summary>
        public VisualElement Full { get; private set; }

        /// <summary>Слой безопасной зоны. <c>null</c>, если дерево не задано.</summary>
        public VisualElement Safe { get; private set; }

        public bool IsBuilt => Root != null;

        /// <summary>
        /// Собирает обёртку и разворачивает в неё заданные деревья.
        /// Порядок добавления и есть порядок отрисовки: полноэкранное под безопасным.
        /// </summary>
        /// <returns><c>false</c>, если не задано ни одного дерева — вью без вёрстки бессмысленно.</returns>
        public bool Build(string name, VisualTreeAsset fullTree, VisualTreeAsset safeTree)
        {
            if (IsBuilt) return true;
            if (fullTree == null && safeTree == null) return false;

            Root = new VisualElement { name = name };
            Root.style.position = Position.Absolute;
            Root.style.left = 0;
            Root.style.right = 0;
            Root.style.top = 0;
            Root.style.bottom = 0;
            // Обёртка не должна быть целью пика: иначе полноэкранный элемент съедает указатель
            // и до world-space панелей события не доходят. Ignore не наследуется — дети пикаются.
            Root.pickingMode = PickingMode.Ignore;

            if (fullTree != null) Full = AddLayer(name + "__full", fullTree);
            if (safeTree != null) Safe = AddLayer(name + "__safe", safeTree);

            return true;
        }

        /// <summary>
        /// Отступы безопасной зоны. Пишутся в границы слоя, а не в padding: корень дерева
        /// внутри может оказаться <c>position: absolute</c>, и тогда padding его не подвинет.
        /// </summary>
        public void ApplySafeInsets(float left, float right, float top, float bottom)
        {
            if (Safe == null) return;

            Safe.style.left = left;
            Safe.style.right = right;
            Safe.style.top = top;
            Safe.style.bottom = bottom;
        }

        private VisualElement AddLayer(string layerName, VisualTreeAsset tree)
        {
            var layer = new VisualElement { name = layerName };
            layer.style.position = Position.Absolute;
            layer.style.left = 0;
            layer.style.right = 0;
            layer.style.top = 0;
            layer.style.bottom = 0;
            layer.pickingMode = PickingMode.Ignore;

            var instance = tree.Instantiate();
            instance.style.flexGrow = 1;
            instance.pickingMode = PickingMode.Ignore;
            layer.Add(instance);

            Root.Add(layer);
            return layer;
        }
    }
}
