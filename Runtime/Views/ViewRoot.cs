using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Обёртка вью: корень во всю полосу кадра и два слоя внутри — <see cref="FullLayer"/>
    /// и <see cref="SafeLayer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Вёрстка вью — ОДИН uxml. На верхнем уровне в нём лежат слои: всё, что обязано доходить
    /// до выреза, — в <see cref="FullLayer"/>, интерактив и текст — в <see cref="SafeLayer"/>.
    /// Любой из слоёв можно опустить. Uxml вовсе без слоёв целиком считается безопасным:
    /// его содержимое переезжает в неявный <see cref="SafeLayer"/>.
    /// </para>
    /// <para>
    /// Дерево разворачивается прямо в корень (<c>CloneTree(Root)</c>), а не в отдельный
    /// <c>TemplateContainer</c>: так стили из <c>&lt;Style&gt;</c> uxml висят на корне и видны
    /// обоим слоям, а <c>Root.Q</c> одним проходом находит элементы в любом из них.
    /// </para>
    /// <para>
    /// «Во весь экран» здесь означает «во всю полосу кадра», а не весь физический экран:
    /// за полосой лежат чёрные поля обрезки, они рисуются поверх всего намеренно.
    /// </para>
    /// </remarks>
    public sealed class ViewRoot
    {
        /// <summary>Корень вью. Занимает всю полосу кадра.</summary>
        public VisualElement Root { get; private set; }

        /// <summary>Слой полноэкранной вёрстки. <c>null</c>, если в uxml его нет.</summary>
        public VisualElement Full { get; private set; }

        /// <summary>Слой безопасной зоны. <c>null</c>, если в uxml его нет.</summary>
        public VisualElement Safe { get; private set; }

        public bool IsBuilt => Root != null;

        /// <summary>Собирает обёртку и разворачивает в неё вёрстку.</summary>
        /// <returns><c>false</c>, если вёрстка не задана — вью без вёрстки бессмысленно.</returns>
        public bool Build(string name, VisualTreeAsset tree)
        {
            if (IsBuilt) return true;
            if (tree == null) return false;

            Root = CreateRoot(name);
            tree.CloneTree(Root);
            ResolveLayers(name);
            return true;
        }

        /// <summary>
        /// Собирает обёртку из готовых слоёв, построенных кодом (фолбэк скринов).
        /// <c>null</c>-слой пропускается.
        /// </summary>
        public void BuildFromContent(string name, VisualElement fullContent, VisualElement safeContent)
        {
            if (IsBuilt) return;

            Root = CreateRoot(name);

            if (fullContent != null)
            {
                Full = new FullLayer { name = name + "__full" };
                fullContent.style.flexGrow = 1;
                Full.Add(fullContent);
                Root.Add(Full);
            }

            if (safeContent != null)
            {
                Safe = new SafeLayer { name = name + "__safe" };
                safeContent.style.flexGrow = 1;
                Safe.Add(safeContent);
                Root.Add(Safe);
            }
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

        private static VisualElement CreateRoot(string name)
        {
            var root = new VisualElement { name = name };
            root.style.position = Position.Absolute;
            root.style.left = 0;
            root.style.right = 0;
            root.style.top = 0;
            root.style.bottom = 0;
            // Обёртка не должна быть целью пика: иначе полноэкранный элемент съедает указатель
            // и до world-space панелей события не доходят. Ignore не наследуется — дети пикаются.
            root.pickingMode = PickingMode.Ignore;
            return root;
        }

        /// <summary>
        /// Находит слои среди прямых детей корня. Порядок отрисовки задаётся порядком в uxml,
        /// но полноэкранный слой обязан лежать под безопасным — выравниваем явно.
        /// </summary>
        private void ResolveLayers(string name)
        {
            var loose = new List<VisualElement>();

            for (var i = 0; i < Root.childCount; i++)
            {
                var child = Root[i];

                if (child is FullLayer full)
                {
                    if (Full == null) Full = full;
                    else UnityEngine.Debug.LogError($"[AppCore] Во вью \"{name}\" больше одного FullLayer — лишний проигнорирован.");
                }
                else if (child is SafeLayer safe)
                {
                    if (Safe == null) Safe = safe;
                    else UnityEngine.Debug.LogError($"[AppCore] Во вью \"{name}\" больше одного SafeLayer — лишний проигнорирован.");
                }
                else
                {
                    loose.Add(child);
                }
            }

            // Элементы вне слоёв — вёрстка без разметки слоёв или забытый хвост. И то и другое
            // трактуем как безопасную зону: текст под вырезом хуже, чем фон с отступом.
            if (loose.Count > 0)
            {
                if (Safe == null)
                {
                    Safe = new SafeLayer();
                    Root.Add(Safe);
                }

                foreach (var element in loose) Safe.Add(element);
            }

            if (Full != null)
            {
                if (string.IsNullOrEmpty(Full.name)) Full.name = name + "__full";
                Full.SendToBack();
            }

            if (Safe != null && string.IsNullOrEmpty(Safe.name)) Safe.name = name + "__safe";
        }
    }
}
