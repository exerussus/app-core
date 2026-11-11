using System;
using System.Collections.Generic;
using Exerussus.AppCore.Views;
using NUnit.Framework;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// Ресурсы контроллера на время жизни вёрстки: подписки освобождаются в обратном порядке,
    /// токен отменяется, повторное монтирование не копит старые подписки.
    /// </summary>
    public sealed class MountScopeTests
    {
        private sealed class Probe : IDisposable
        {
            private readonly List<int> _log;
            private readonly int _id;

            public int Disposed { get; private set; }

            public Probe(List<int> log, int id)
            {
                _log = log;
                _id = id;
            }

            public void Dispose()
            {
                Disposed++;
                _log.Add(_id);
            }
        }

        [Test]
        public void Release_DisposesInReverseOrder()
        {
            var log = new List<int>();
            var scope = new MountScope();
            scope.Begin();
            scope.Track(new Probe(log, 1));
            scope.Track(new Probe(log, 2));
            scope.Track(new Probe(log, 3));

            scope.Release(null);

            CollectionAssert.AreEqual(new[] { 3, 2, 1 }, log);
            Assert.IsFalse(scope.IsMounted);
        }

        [Test]
        public void Remount_DoesNotAccumulate()
        {
            var log = new List<int>();
            var scope = new MountScope();

            // Фрагмент с unmountOnHide: каждый показ — новое монтирование и новые подписки.
            var first = new Probe(log, 1);
            scope.Begin();
            scope.Track(first);
            scope.Release(null);

            var second = new Probe(log, 2);
            scope.Begin();
            scope.Track(second);
            scope.Release(null);

            Assert.AreEqual(1, first.Disposed);
            Assert.AreEqual(1, second.Disposed);
            CollectionAssert.AreEqual(new[] { 1, 2 }, log);
        }

        [Test]
        public void Release_CancelsToken_AndNextMountGetsFreshOne()
        {
            var scope = new MountScope();
            scope.Begin();
            var token = scope.Token;

            scope.Release(null);

            Assert.IsTrue(token.IsCancellationRequested);

            scope.Begin();
            Assert.IsFalse(scope.Token.IsCancellationRequested);
        }

        [Test]
        public void Track_IgnoresNull()
        {
            var scope = new MountScope();
            scope.Begin();
            scope.Track(null);

            Assert.DoesNotThrow(() => scope.Release(null));
        }
    }
}
