using System;
using System.Runtime.CompilerServices;
using Exerussus.Payloads;
using Signals;
using Signals.Routing;
using UnityEngine;

namespace Exerussus.AppCore.Signals
{
    public static class UISignal
    {
        public const string UISignalId = "ui";

        private static SignalHandle                              Handle;
        private static SignalRouter<ButtonPressed, string>       ButtonRouter;
        
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            Handle       = global::Signals.Signal.GetOrCreate(UISignalId);
            ButtonRouter = new SignalRouter<ButtonPressed, string>(
                Handle,
                static evt => evt.Id,      // static lambda — без захвата, кешируется JIT'ом
                StringComparer.Ordinal);   // ordinal быстрее дефолтного string-компаратора
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InvokeButtonPressed(in string id, in Payload payload = default
#if UNITY_EDITOR
            , [CallerFilePath]   string callerFile   = ""
            , [CallerLineNumber] int    callerLine   = 0
            , [CallerMemberName] string callerMember = ""
#endif
        )
        {
            Handle.Invoke(new ButtonPressed { Id = id, Payload = payload }
#if UNITY_EDITOR
                , callerFile, callerLine, callerMember
#endif
            );
        }

        public static int ButtonPressedSubscriberCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ButtonRouter?.SubscriberCount ?? 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IDisposable OnButtonPressedSubscribe(string id, Action onButtonPressed) => ButtonRouter.Subscribe(id, onButtonPressed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IDisposable OnAnyButtonPressedSubscribe(Action<ButtonPressed> onButtonPressed) => Handle.Subscribe(onButtonPressed);

        // Если когда-нибудь понадобится payload — есть и такой:
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IDisposable OnButtonPressedSubscribe(string id, Action<ButtonPressed> onButtonPressed) => ButtonRouter.Subscribe(id, onButtonPressed);
    }
}
