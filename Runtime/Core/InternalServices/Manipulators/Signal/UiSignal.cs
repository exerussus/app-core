using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Exerussus.Payloads;
using Signals;
using Signals.Routing;
using UnityEngine;
using UnityEngine.UIElements;

namespace App.UIToolkit.Manipulators
{
    public static class UiSignal
    {
        public const string UISignalId = "ui";

        private static SignalHandle                              Handle;
        private static SignalRouter<ButtonPressed, string>       ButtonRouter;

        private static readonly List<Action<VisualElement, Payload>> PayloadFactories = new();   
        
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            Debug.Log("[DEBUG] Initializing UiSignal");
            Handle       = Signal.GetOrCreate(UISignalId);
            ButtonRouter = new SignalRouter<ButtonPressed, string>(
                Handle,
                static evt => evt.Id,      // static lambda — без захвата, кешируется JIT'ом
                StringComparer.Ordinal);   // ordinal быстрее дефолтного string-компаратора
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallEditorShutdownHook()
        {
            // -= перед += — идемпотентно. При "no domain reload"
            // InitializeOnLoadMethod всё равно срабатывает раз на домен,
            // но перестраховка дешёвая.
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange change)
        {
            if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode)
            {
                PayloadFactories.Clear();
            }
        }
#endif
        
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

        public static void AddPayloadFactory(Action<VisualElement, Payload> factory)
        {
            PayloadFactories.Add(factory);
        }
        
        public static void RemovePayloadFactory(Action<VisualElement, Payload> factory)
        {
            PayloadFactories.Remove(factory);
        }
        
        public static Payload CollectPayload(VisualElement target)
        {
            var payload = Payload.Create();
            
            foreach (var factory in PayloadFactories) factory.Invoke(target, payload);
            
            return payload;
        }
    }
}