namespace Exerussus.AppCore.Views
{
    /// <summary>Вид вью. Определяет папку, суффикс контроллера и реестр id.</summary>
    public enum ViewKind
    {
        Page = 0,
        Popup = 1,
        Fragment = 2,
    }

    /// <summary>
    /// Что делает «назад» (Escape, аппаратная кнопка) на странице. Попап поверх страницы
    /// перехватывает «назад» раньше и просто закрывается — независимо от этой настройки.
    /// </summary>
    public enum PageBackAction
    {
        /// <summary>Ничего: корневые страницы не должны выкидывать пользователя по Escape.</summary>
        None = 0,

        /// <summary>Возврат на предыдущую страницу по стеку переходов.</summary>
        Previous = 1,

        /// <summary>Переход на конкретную страницу.</summary>
        ToPage = 2,
    }

    /// <summary>Что сделать с курсором при входе на страницу.</summary>
    public enum PageCursorMode
    {
        /// <summary>Не трогать — как оставила предыдущая страница.</summary>
        Keep = 0,

        /// <summary>Захватить и спрятать.</summary>
        Lock = 1,

        /// <summary>Отпустить и показать.</summary>
        Unlock = 2,
    }
}
