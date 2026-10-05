using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Вкладка AppDeck. Встроенные — Console, Actions, Metrics; проект добавляет свои через
    /// <see cref="AppDeck.AddTab"/>.
    /// </summary>
    /// <remarks>
    /// Скрытая вкладка ничего не стоит: вёрстка строится при первом показе (<see cref="Build"/>),
    /// а <see cref="Tick"/> зовётся только у видимой вкладки при открытом окне. Всё, что вкладка
    /// обновляет, она обновляет в Tick с нужной ей частотой — сама решает, сколько раз в секунду.
    /// </remarks>
    public abstract class DeckTab
    {
        /// <summary>Уникальный id (для запоминания активной вкладки и <see cref="AppDeck.ShowTab"/>).</summary>
        public abstract string Id { get; }

        public abstract string Title { get; }

        /// <summary>Порядок в шапке: меньше — левее.</summary>
        public virtual int Order => 0;

        /// <summary>Корень вкладки. Есть только после первого показа.</summary>
        public VisualElement Root { get; internal set; }

        public bool IsBuilt => Root != null;

        public bool IsVisible { get; internal set; }

        /// <summary>Построить вёрстку в <paramref name="root"/>. Один раз, при первом показе.</summary>
        protected internal abstract void Build(VisualElement root);

        protected internal virtual void OnShow() { }

        protected internal virtual void OnHide() { }

        /// <summary>Кадр, пока вкладка видна. <paramref name="deltaTime"/> — unscaled.</summary>
        protected internal virtual void Tick(float deltaTime) { }

        /// <summary>Окно открылось на этой вкладке: поставить фокус (поле ввода, поиск).</summary>
        protected internal virtual void Focus() { }

        /// <summary>Вернуть фокус после клика по миру (выбор цели), не трогая каретку. По умолчанию — <see cref="Focus"/>.</summary>
        protected internal virtual void Refocus() => Focus();

        /// <summary>Фокус в текстовом поле вкладки — игре не читать клавиатуру.</summary>
        protected internal virtual bool IsTyping => false;

        /// <summary>Нажат Escape при открытом окне. true — вкладка обработала сама (закрыла подсказки); false — окно закроется.</summary>
        protected internal virtual bool HandleEscape() => false;

        /// <summary>Вкладку сняли (<see cref="AppDeck.RemoveTab"/>). Отпустить подписки.</summary>
        protected internal virtual void Dispose() { }
    }
}
