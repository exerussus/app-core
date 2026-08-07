using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Диспатчер главного потока: очередь действий, исполняемая в Update.
    /// </summary>
    public partial class AppRunner
    {
        /// <summary>
        /// Идентификатор главного потока Unity, зафиксированный в <see cref="Awake"/>.
        /// Используется для определения, нужно ли перенаправлять действие в очередь
        /// или допустимо выполнить его синхронно.
        /// </summary>
        private int _mainThreadId;

        /// <summary>
        /// Очередь действий, которые должны быть выполнены в главном потоке.
        /// Опустошается в <see cref="Update"/> до выполнения логики обновляемых сервисов.
        /// </summary>
        private readonly Queue<Action> _mainThreadQueue = new();

        /// <summary>Объект-замок для синхронизированного доступа к <see cref="_mainThreadQueue"/>.</summary>
        private readonly object _mainThreadQueueLock = new();

        /// <summary>Буфер слива очереди. Переиспользуется, чтобы кадр с работой не аллоцировал.</summary>
        private readonly List<Action> _mainThreadBuffer = new();

        /// <summary>
        /// Выполняет все накопленные в <see cref="_mainThreadQueue"/> действия.
        /// </summary>
        /// <remarks>
        /// Снимок очереди берётся под локом, исполнение происходит вне лока —
        /// это позволяет действиям безопасно ставить в очередь новые задачи
        /// (они будут обработаны на следующем кадре).
        /// Исключения из отдельного действия логируются и не прерывают
        /// обработку остальных элементов снимка.
        /// </remarks>
        private void PumpMainThreadQueue()
        {
            // Быстрая проверка без лока — типичный путь, когда очередь пуста.
            if (_mainThreadQueue.Count == 0) return;

            // Сливаем в переиспользуемый буфер, а не в новый массив: очередь дёргается
            // из фоновых потоков, и ToArray() на каждом кадре с работой давал бы мусор.
            lock (_mainThreadQueueLock)
            {
                if (_mainThreadQueue.Count == 0) return;
                while (_mainThreadQueue.Count > 0) _mainThreadBuffer.Add(_mainThreadQueue.Dequeue());
            }

            // Выполняем вне лока: действие вправе поставить в очередь новое — оно уйдёт
            // в следующий кадр, а не приведёт к рекурсивному захвату.
            for (var i = 0; i < _mainThreadBuffer.Count; i++)
            {
                try
                {
                    _mainThreadBuffer[i].Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            _mainThreadBuffer.Clear();
        }

        /// <summary>
        /// Аналог <see cref="PumpMainThreadQueue"/>, вызываемый из <see cref="OnDestroy"/>.
        /// Гарантирует, что висящие <see cref="UniTaskCompletionSource"/> завершатся
        /// (по отмене), а не оставят awaiters навсегда заблокированными.
        /// </summary>
        private void DrainMainThreadQueueOnDestroy()
        {
            lock (_mainThreadQueueLock)
            {
                if (_mainThreadQueue.Count == 0) return;
                while (_mainThreadQueue.Count > 0) _mainThreadBuffer.Add(_mainThreadQueue.Dequeue());
            }

            for (var i = 0; i < _mainThreadBuffer.Count; i++)
            {
                try
                {
                    _mainThreadBuffer[i].Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            _mainThreadBuffer.Clear();
        }

        /// <summary>
        /// Выполняет <paramref name="action"/> в главном потоке Unity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Поведение в зависимости от потока вызова:
        /// <list type="bullet">
        ///   <item><description>
        ///     Если вызов уже происходит в главном потоке — действие выполняется синхронно,
        ///     возвращается <see cref="UniTask.CompletedTask"/>. Это позволяет избежать
        ///     лишнего откладывания на следующий кадр в типовом сценарии.
        ///   </description></item>
        ///   <item><description>
        ///     Иначе действие ставится в потокобезопасную очередь и будет выполнено
        ///     ближайшим вызовом <see cref="Update"/>. Возвращённая <see cref="UniTask"/>
        ///     завершится после фактического выполнения действия.
        ///   </description></item>
        /// </list>
        /// </para>
        /// <para>
        /// Если компонент уже уничтожен (<see cref="_isDestroyed"/>), возвращается
        /// отменённая <see cref="UniTask"/> без постановки в очередь — это предотвращает
        /// «утечку» подвисших awaiters.
        /// </para>
        /// </remarks>
        /// <param name="action">Действие для выполнения в главном потоке. Не должно быть <c>null</c>.</param>
        /// <returns><see cref="UniTask"/>, завершающаяся после выполнения действия.</returns>
        private UniTask RunOnMainThreadAsync(Action action)
        {
            if (action == null) return UniTask.CompletedTask;

            if (_isDestroyed) return UniTask.FromCanceled();

            // Быстрый путь — мы уже в главном потоке: исполняем синхронно.
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                return UniTask.CompletedTask;
            }

            // Медленный путь — откладываем выполнение до ближайшего Update().
            var tcs = new UniTaskCompletionSource();
            lock (_mainThreadQueueLock)
            {
                if (_isDestroyed)
                {
                    tcs.TrySetCanceled();
                    return tcs.Task;
                }

                _mainThreadQueue.Enqueue(() =>
                {
                    if (_isDestroyed)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        action();
                        tcs.TrySetResult();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        tcs.TrySetException(e);
                    }
                });
            }

            return tcs.Task;
        }
    }
}
