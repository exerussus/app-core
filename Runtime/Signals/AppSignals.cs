using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Exerussus.AppCore.Signals
{
    /// <summary>Сигнал верхнего слоя: строковый id и необязательный строковый аргумент.</summary>
    public readonly struct AppSignal
    {
        public readonly string Id;
        public readonly string Arg;

        public AppSignal(string id, string arg)
        {
            Id = id;
            Arg = arg;
        }

        public override string ToString() => Arg == null ? Id : $"{Id}({Arg})";
    }

    /// <summary>
    /// Шина сигналов верхнего слоя приложения: обвязка, модульные события, кнопки UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сознательно простая: id — строка, аргумент — строка. Для обвязки и событий модулей
    /// этого хватает, а небольшие аллокации на строках здесь допустимы. Шина НЕ рассчитана
    /// на геймплейный поток событий — там свои, типизированные механизмы.
    /// </para>
    /// <para>
    /// Только главный поток. Подписка и отписка из обработчика во время рассылки безопасны:
    /// отписанный обработчик в текущей рассылке больше не вызывается, новый — начинает со следующей.
    /// </para>
    /// </remarks>
    public static class AppSignals
    {
        private sealed class Bucket
        {
            public readonly string Id;
            public readonly List<Action<string>> Handlers = new();
            public int Dispatching;
            public bool HasHoles;

            public Bucket(string id) => Id = id;

            public int ActiveCount
            {
                get
                {
                    var n = 0;
                    for (var i = 0; i < Handlers.Count; i++) if (Handlers[i] != null) n++;
                    return n;
                }
            }

            public void Compact()
            {
                if (Dispatching > 0 || !HasHoles) return;
                Handlers.RemoveAll(h => h == null);
                HasHoles = false;
            }
        }

        private sealed class Subscription : IDisposable
        {
            private Bucket _bucket;
            private Action<string> _handler;
            private List<Action<AppSignal>> _anyList;
            private Action<AppSignal> _anyHandler;

#if UNITY_EDITOR
            private readonly string _file;
            private readonly int _line;
            private readonly string _member;
#endif

            public Subscription(Bucket bucket, Action<string> handler, string file, int line, string member)
            {
                _bucket = bucket;
                _handler = handler;
#if UNITY_EDITOR
                _file = file;
                _line = line;
                _member = member;
#endif
            }

            public Subscription(List<Action<AppSignal>> anyList, Action<AppSignal> anyHandler)
            {
                _anyList = anyList;
                _anyHandler = anyHandler;
            }

            public void Dispose()
            {
                if (_bucket != null)
                {
                    var bucket = _bucket;
                    _bucket = null;

                    var index = bucket.Handlers.IndexOf(_handler);
                    _handler = null;
                    if (index < 0) return;

                    // Во время рассылки индексы трогать нельзя — оставляем дырку и чистим после.
                    if (bucket.Dispatching > 0)
                    {
                        bucket.Handlers[index] = null;
                        bucket.HasHoles = true;
                    }
                    else
                    {
                        bucket.Handlers.RemoveAt(index);
                    }

#if UNITY_EDITOR
                    EditorHook.Unsubscribed?.Invoke(bucket.Id, bucket.ActiveCount, _file, _line, _member);
#endif
                    return;
                }

                if (_anyList != null)
                {
                    var index = _anyList.IndexOf(_anyHandler);
                    if (index >= 0)
                    {
                        if (_anyDispatching > 0)
                        {
                            _anyList[index] = null;
                            _anyHasHoles = true;
                        }
                        else
                        {
                            _anyList.RemoveAt(index);
                        }
                    }

                    _anyList = null;
                    _anyHandler = null;
                }
            }
        }

        private static readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
        private static readonly List<Action<AppSignal>> _any = new();
        private static int _anyDispatching;
        private static bool _anyHasHoles;

        /// <summary>
        /// Сброс между запусками Play Mode без перезагрузки домена: иначе подписки прошлой
        /// сессии пережили бы её и получали сигналы новой.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _buckets.Clear();
            _any.Clear();
            _anyDispatching = 0;
            _anyHasHoles = false;
        }

        /// <summary>Подписка на сигнал с аргументом.</summary>
        public static IDisposable Subscribe(string id, Action<string> handler
#if UNITY_EDITOR
            , [CallerFilePath] string callerFile = ""
            , [CallerLineNumber] int callerLine = 0
            , [CallerMemberName] string callerMember = ""
#endif
        )
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Пустой id сигнала.", nameof(id));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            var bucket = GetOrCreate(id
#if UNITY_EDITOR
                , callerFile, callerLine, callerMember
#endif
            );
            bucket.Handlers.Add(handler);

#if UNITY_EDITOR
            EditorHook.Subscribed?.Invoke(id, bucket.ActiveCount, callerFile, callerLine, callerMember);
            return new Subscription(bucket, handler, callerFile, callerLine, callerMember);
#else
            return new Subscription(bucket, handler, null, 0, null);
#endif
        }

        /// <summary>Подписка на сигнал без аргумента.</summary>
        public static IDisposable Subscribe(string id, Action handler
#if UNITY_EDITOR
            , [CallerFilePath] string callerFile = ""
            , [CallerLineNumber] int callerLine = 0
            , [CallerMemberName] string callerMember = ""
#endif
        )
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Subscribe(id, _ => handler()
#if UNITY_EDITOR
                , callerFile, callerLine, callerMember
#endif
            );
        }

        /// <summary>Подписка на все сигналы подряд — для логов, аналитики, отладки.</summary>
        public static IDisposable SubscribeAll(Action<AppSignal> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _any.Add(handler);
            return new Subscription(_any, handler);
        }

        /// <summary>Поднимает сигнал. Исключения обработчиков логируются и не прерывают рассылку.</summary>
        public static void Raise(string id, string arg = null
#if UNITY_EDITOR
            , [CallerFilePath] string callerFile = ""
            , [CallerLineNumber] int callerLine = 0
            , [CallerMemberName] string callerMember = ""
#endif
        )
        {
            if (string.IsNullOrEmpty(id)) return;

#if UNITY_EDITOR
            List<string> errors = null;
            var bucket = GetOrCreate(id, callerFile, callerLine, callerMember);
            var countBefore = bucket.ActiveCount;
            EditorHook.InvokeBegin?.Invoke(id);
#else
            _buckets.TryGetValue(id, out var bucket);
#endif

            try
            {
                if (bucket != null && bucket.Handlers.Count > 0)
                {
                    bucket.Dispatching++;
                    try
                    {
                        // Верхняя граница фиксируется: подписанные во время рассылки ждут следующей.
                        var count = bucket.Handlers.Count;
                        for (var i = 0; i < count; i++)
                        {
                            var handler = bucket.Handlers[i];
                            if (handler == null) continue;

                            try { handler(arg); }
                            catch (Exception e)
                            {
                                Debug.LogException(e);
#if UNITY_EDITOR
                                (errors ??= new List<string>()).Add(e.GetType().Name + ": " + e.Message);
#endif
                            }
                        }
                    }
                    finally
                    {
                        bucket.Dispatching--;
                        bucket.Compact();
                    }
                }

                if (_any.Count > 0)
                {
                    var signal = new AppSignal(id, arg);
                    _anyDispatching++;
                    try
                    {
                        var count = _any.Count;
                        for (var i = 0; i < count; i++)
                        {
                            var handler = _any[i];
                            if (handler == null) continue;

                            try { handler(signal); }
                            catch (Exception e) { Debug.LogException(e); }
                        }
                    }
                    finally
                    {
                        _anyDispatching--;
                        if (_anyDispatching == 0 && _anyHasHoles)
                        {
                            _any.RemoveAll(h => h == null);
                            _anyHasHoles = false;
                        }
                    }
                }
            }
            finally
            {
#if UNITY_EDITOR
                EditorHook.Invoked?.Invoke(id, arg, countBefore, errors, callerFile, callerLine, callerMember);
                EditorHook.InvokeEnd?.Invoke(id);
#endif
            }
        }

        /// <summary>Сколько обработчиков подписано на сигнал.</summary>
        public static int SubscriberCount(string id)
            => !string.IsNullOrEmpty(id) && _buckets.TryGetValue(id, out var bucket) ? bucket.ActiveCount : 0;

        private static Bucket GetOrCreate(string id
#if UNITY_EDITOR
            , string file, int line, string member
#endif
        )
        {
            if (_buckets.TryGetValue(id, out var bucket)) return bucket;

            bucket = new Bucket(id);
            _buckets.Add(id, bucket);

#if UNITY_EDITOR
            EditorHook.Created?.Invoke(id, file, line, member);
#endif
            return bucket;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Редакторные точки наблюдения — их слушает страница Signal Timeline в Nexus.
        /// В билде отсутствуют целиком.
        /// </summary>
        public static class EditorHook
        {
            /// <summary>Сигнал встретился впервые: (id, file, line, member).</summary>
            public static Action<string, string, int, string> Created;

            /// <summary>Подписка: (id, подписчиков после, file, line, member).</summary>
            public static Action<string, int, string, int, string> Subscribed;

            /// <summary>Отписка: (id, подписчиков после, file, line, member).</summary>
            public static Action<string, int, string, int, string> Unsubscribed;

            /// <summary>Рассылка завершена: (id, arg, подписчиков, ошибки или null, file, line, member).</summary>
            public static Action<string, string, int, List<string>, string, int, string> Invoked;

            /// <summary>Скобки рассылки — для графа вызовов (вложенные Raise из обработчиков).</summary>
            public static Action<string> InvokeBegin;

            public static Action<string> InvokeEnd;
        }
#endif
    }
}
