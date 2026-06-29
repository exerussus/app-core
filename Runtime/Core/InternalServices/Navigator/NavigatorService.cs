
using System.Collections.Generic;
using App.Abstractions;
using App.Core;
using App.UIToolkit.Manipulators;
using Exerussus.DI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace App.Services.Navigator
{
    public class NavigatorService : IAppServiceUpdate
    {
        [Inject] private readonly AppRunner _appRunner;
        
        private readonly Dictionary<PageUID, PageUID> _backHooks = new ();
        private readonly HashSet<PageUID> _cursorLockHooks = new ();
        private readonly HashSet<PageUID> _cursorUnlockHooks = new ();

        private PageUID _pageUid;
        private KeyControl _escapeKey;

        public void Initialize()
        {
            UiSignal.OnAnyButtonPressedSubscribe(OnAnyButtonPressed);
            _appRunner.OnPageMounted += OnPageMounted;
            _appRunner.OnPageChanged += OnPageChanged;
            _escapeKey = Keyboard.current[Key.Escape];
        }

        private void OnPageChanged((PageUID prev, PageUID current) ctx)
        {
            _pageUid = ctx.current;

            if (_cursorLockHooks.Contains(_pageUid))
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (_cursorUnlockHooks.Contains(_pageUid))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        public void Update()
        {
            if (_escapeKey.wasPressedThisFrame)
            {
                if (_pageUid.IsEmpty()) return;
                if (_backHooks.TryGetValue(_pageUid, out var backPageUid))
                {
                    if (backPageUid == PageUID.None)
                    {
                        _appRunner.SwitchToPrevPage();
                    }
                    else _appRunner.SwitchToPage(backPageUid);
                }
            }
        }

        private void OnAnyButtonPressed(ButtonPressed buttonPressed)
        {
            if (!buttonPressed.Payload.IsValid()) return;

            if (buttonPressed.Payload.TryGet(NavigationLink.NavigateToKey, out var value))
            {
                var pageUid = PageUID.FromRaw(value);
                if (pageUid == PageUID.None) _appRunner.SwitchToPrevPage();
                else _appRunner.SwitchToPage(pageUid);
            }
        }

        private void OnPageMounted(PageUID pageUid, VisualElement pageRoot)
        {
            TryInitBackAction(pageUid, pageRoot);
            TryInitCursorSettings(pageUid, pageRoot);
        }

        private void TryInitBackAction(PageUID pageUid, VisualElement pageRoot)
        {
            var backActionHook = pageRoot.Q<VisualElement>("back__action-hook");
            if (backActionHook == null) return;
            
            foreach (var className in backActionHook.GetClasses())
            {
                if (NavigationLink.Links.TryGetValue(className, out var toPage))
                {
                    _backHooks.Add(pageUid, toPage);
                    return;
                }
            }

            if (backActionHook.ClassListContains("to-back-page__navigation"))
            {
                _backHooks.Add(pageUid, PageUID.None);
                return;
            }
        }

        private void TryInitCursorSettings(PageUID pageUid, VisualElement pageRoot)
        {
            var hook = pageRoot.Q<VisualElement>("setting__action-hook");
            
            if (hook == null) return;
            
            if (hook.ClassListContains("cursor-lock"))
            {
                _cursorLockHooks.Add(pageUid);
            }
            else if (hook.ClassListContains("cursor-unlock"))
            {
                _cursorUnlockHooks.Add(pageUid);
            }
        }
    }
}