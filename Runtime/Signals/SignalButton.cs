using UnityEngine.UIElements;

namespace Exerussus.AppCore.Signals
{
    /// <summary>
    /// Кнопка, поднимающая сигнал <see cref="AppSignals"/> по клику.
    /// </summary>
    /// <remarks>
    /// <code>
    /// &lt;ac:SignalButton signal="shop.buy" arg="gold_pack" text="Купить" /&gt;
    /// </code>
    /// Пустой <c>signal</c> — берётся имя элемента. Обычной кнопке достаточно класса
    /// <c>signal-button</c>: она поднимет сигнал с id, равным своему имени, без аргумента.
    /// </remarks>
    [UxmlElement]
    public partial class SignalButton : Button
    {
        /// <summary>Id сигнала. Пусто — имя элемента.</summary>
        [UxmlAttribute("signal")]
        public string Signal { get; set; }

        /// <summary>Строковый аргумент сигнала. Необязателен.</summary>
        [UxmlAttribute("arg")]
        public string Arg { get; set; }

        public SignalButton()
        {
            clicked += OnClicked;
        }

        private void OnClicked()
        {
            var id = string.IsNullOrEmpty(Signal) ? name : Signal;
            AppSignals.Raise(id, string.IsNullOrEmpty(Arg) ? null : Arg);
        }
    }
}
