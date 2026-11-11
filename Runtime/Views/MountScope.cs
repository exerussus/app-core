using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Ресурсы контроллера, живущие столько же, сколько его вёрстка: подписки через
    /// <c>Track</c> и токен отмены. Общая часть контроллеров страницы, попапа и фрагмента.
    /// </summary>
    /// <remarks>
    /// Список и источник токена создаются лениво — контроллер, который ими не пользуется,
    /// не платит ничего.
    /// </remarks>
    internal sealed class MountScope
    {
        private List<IDisposable> _tracked;
        private CancellationTokenSource _cts;

        /// <summary>Есть ли смонтированная вёрстка, за которой закреплены ресурсы.</summary>
        public bool IsMounted { get; private set; }

        /// <summary>Токен, отменяемый при сносе вёрстки.</summary>
        public CancellationToken Token => (_cts ??= new CancellationTokenSource()).Token;

        public void Begin() => IsMounted = true;

        public void Track(IDisposable subscription)
        {
            if (subscription == null) return;
            (_tracked ??= new List<IDisposable>()).Add(subscription);
        }

        /// <summary>Отменяет токен и освобождает подписки в обратном порядке.</summary>
        public void Release(UnityEngine.Object context)
        {
            IsMounted = false;

            if (_cts != null)
            {
                var cts = _cts;
                _cts = null;
                try { cts.Cancel(); }
                catch (Exception e) { Debug.LogException(e, context); }
                cts.Dispose();
            }

            if (_tracked == null) return;

            for (var i = _tracked.Count - 1; i >= 0; i--)
            {
                try { _tracked[i]?.Dispose(); }
                catch (Exception e) { Debug.LogException(e, context); }
            }

            _tracked.Clear();
        }
    }
}
