using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore.Internal
{
    /// <summary>
    /// История страниц для возврата «назад». Чистая логика без сцены — её правила
    /// проверяются EditMode-тестами.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Прямой переход кладёт в историю страницу, с которой ушли. Возврат на вершину
    /// истории её снимает и ничего не кладёт: иначе текущая страница возвращалась бы
    /// в стек, и «назад» качался бы между двумя последними страницами (A→B→C→B→C…).
    /// </para>
    /// <para>
    /// Цель возврата читается через <see cref="TryPeek"/> без снятия: запись снимается
    /// только в <see cref="Commit"/>, когда переход реально начался. Отброшенный или
    /// отложенный запрос историю не теряет.
    /// </para>
    /// </remarks>
    internal sealed class PageHistory
    {
        private readonly BoundedStack<PageId> _stack;

        public PageHistory(int capacity) => _stack = new BoundedStack<PageId>(capacity);

        /// <summary>Число записей в истории.</summary>
        public int Count => _stack.Count;

        /// <summary>Куда вернёт «назад». Историю не меняет.</summary>
        public bool TryPeek(out PageId page)
        {
            if (_stack.Count == 0)
            {
                page = PageId.None;
                return false;
            }

            page = _stack.Peek();
            return true;
        }

        /// <summary>
        /// Фиксирует начавшийся переход <paramref name="from"/> → <paramref name="to"/>.
        /// </summary>
        /// <param name="from">Страница, с которой уходим. <see cref="PageId.None"/> — стартовый переход.</param>
        /// <param name="to">Целевая страница.</param>
        /// <param name="isBack">
        /// Переход — возврат «назад». Снимает вершину, если она совпадает с целью; если
        /// не совпадает (историю успели переписать), переход считается прямым.
        /// </param>
        public void Commit(PageId from, PageId to, bool isBack)
        {
            if (isBack && _stack.Count > 0 && _stack.Peek() == to)
            {
                _stack.Pop();
                return;
            }

            if (!from.IsEmpty()) _stack.Push(from);
        }

        /// <summary>Очищает историю.</summary>
        public void Clear() => _stack.Clear();
    }
}
