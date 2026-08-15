using UnityEngine.UIElements;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Точка в вёрстке, куда разворачивается <see cref="AppFragment"/>. Одна страница может
    /// нести несколько хостов — например, HUD и поле мини-игры отдельно.
    /// </summary>
    /// <remarks>
    /// Типизированный элемент, а не соглашение по имени: место для подстановки помечается
    /// осознанно, и <c>host-id</c> должен быть виден в UI Builder рядом с элементом.
    /// <code>
    /// &lt;ac:FragmentHost host-id="content" /&gt;
    /// </code>
    /// Хост сам ничего не решает и ничего не хранит — вью держит реестр и монтирует в него.
    /// </remarks>
    [UxmlElement]
    public partial class FragmentHost : VisualElement
    {
        /// <summary>Идентификатор хоста. Пусто — хост считается единственным/дефолтным во вью.</summary>
        [UxmlAttribute("host-id")]
        public string HostId { get; set; }

        public FragmentHost()
        {
            // Хост — это контейнер, а не мишень: пикать надо содержимое фрагмента.
            // Растягивается, чтобы фрагмент занял отведённое ему место без правок вёрстки.
            style.flexGrow = 1f;
            pickingMode = PickingMode.Ignore;
        }
    }
}
