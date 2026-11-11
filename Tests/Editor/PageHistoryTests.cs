using Exerussus.AppCore.Internal;
using Exerussus.AppCore.Navigation;
using NUnit.Framework;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// История «назад»: возврат снимает запись и не кладёт текущую страницу,
    /// подсмотр цели историю не меняет, ёмкость ограничена.
    /// </summary>
    public sealed class PageHistoryTests
    {
        private static readonly PageId A = new("history_a");
        private static readonly PageId B = new("history_b");
        private static readonly PageId C = new("history_c");
        private static readonly PageId D = new("history_d");

        /// <summary>
        /// Проигрывает переход так, как это делает навигация: для возврата цель берётся
        /// из вершины истории, затем переход фиксируется.
        /// </summary>
        private static PageId Go(PageHistory history, PageId current, PageId target)
        {
            history.Commit(current, target, isBack: false);
            return target;
        }

        private static PageId Back(PageHistory history, PageId current)
        {
            Assert.IsTrue(history.TryPeek(out var target), "истории нет — возвращаться некуда");
            history.Commit(current, target, isBack: true);
            return target;
        }

        [Test]
        public void ForwardThenBackTwice_ReturnsToFirst()
        {
            var history = new PageHistory(8);
            var current = Go(history, PageId.None, A);
            current = Go(history, current, B);
            current = Go(history, current, C);

            current = Back(history, current);
            Assert.AreEqual(B, current);

            current = Back(history, current);
            Assert.AreEqual(A, current);
            Assert.AreEqual(0, history.Count);
        }

        [Test]
        public void Back_DoesNotPushCurrentPage()
        {
            var history = new PageHistory(8);
            var current = Go(history, PageId.None, A);
            current = Go(history, current, B);
            current = Go(history, current, C);

            Back(history, current);

            Assert.AreEqual(1, history.Count);
            Assert.IsTrue(history.TryPeek(out var top));
            Assert.AreEqual(A, top);
        }

        [Test]
        public void StartNavigation_PushesNothing()
        {
            var history = new PageHistory(8);
            Go(history, PageId.None, A);

            Assert.AreEqual(0, history.Count);
            Assert.IsFalse(history.TryPeek(out var top));
            Assert.AreEqual(PageId.None, top);
        }

        [Test]
        public void Peek_KeepsEntry()
        {
            // Отброшенный из-за занятой навигации «назад» не должен терять запись.
            var history = new PageHistory(8);
            var current = Go(history, PageId.None, A);
            Go(history, current, B);

            Assert.IsTrue(history.TryPeek(out var first));
            Assert.IsTrue(history.TryPeek(out var second));
            Assert.AreEqual(first, second);
            Assert.AreEqual(1, history.Count);
        }

        [Test]
        public void BackToNonTopTarget_IsTreatedAsForward()
        {
            var history = new PageHistory(8);
            var current = Go(history, PageId.None, A);
            current = Go(history, current, B);

            history.Commit(current, D, isBack: true);

            Assert.AreEqual(2, history.Count);
            Assert.IsTrue(history.TryPeek(out var top));
            Assert.AreEqual(B, top);
        }

        [Test]
        public void Overflow_DropsOldest()
        {
            var history = new PageHistory(2);
            var current = Go(history, PageId.None, A);
            current = Go(history, current, B);
            current = Go(history, current, C);
            current = Go(history, current, D);

            Assert.AreEqual(2, history.Count);

            current = Back(history, current);
            Assert.AreEqual(C, current);

            current = Back(history, current);
            Assert.AreEqual(B, current);

            Assert.IsFalse(history.TryPeek(out _));
        }

        [Test]
        public void Clear_EmptiesHistory()
        {
            var history = new PageHistory(4);
            var current = Go(history, PageId.None, A);
            Go(history, current, B);

            history.Clear();

            Assert.AreEqual(0, history.Count);
            Assert.IsFalse(history.TryPeek(out _));
        }
    }
}
