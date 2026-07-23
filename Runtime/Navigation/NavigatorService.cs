using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Exerussus.DI;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore;
using Exerussus.AppCore.Services;
using Exerussus.AppCore.Signals;
using Exerussus.AppCore.Input;
using Exerussus.AppCore.Views;
using Cursor = UnityEngine.Cursor;

namespace Exerussus.AppCore.Navigation
{
    public class NavigatorService : IAppService, IAppManipulatorBuilder
    {
        [Inject] private readonly AppRunner _appRunner;
        
        private readonly Dictionary<PageId, PageId> _backHooks = new ();
        private readonly HashSet<PageId> _cursorLockHooks = new ();
        private readonly HashSet<PageId> _cursorUnlockHooks = new ();
        
        private PageId _pageUid;
        
        public void OnInject(DependenciesContainer container)
        {
            if (container.Has<InputAdapter>())
            {
                var adapter = container.Get<InputAdapter>();
                adapter.OnBackPressed += OnBackPressed;
            }
        }

        private void OnBackPressed()
        {
            if (_pageUid.IsEmpty()) return;
            if (_backHooks.TryGetValue(_pageUid, out var backPageUid))
            {
                if (backPageUid == PageId.None)
                {
                    _appRunner.SwitchToPrevPage();
                }
                else _appRunner.SwitchToPage(backPageUid);
            }
        }

        public UniTask Initialize(System.Threading.CancellationToken token)
        {
            UISignal.OnAnyButtonPressedSubscribe(OnAnyButtonPressed);
            _appRunner.OnPageMounted += OnPageMounted;
            _appRunner.OnPageChanged += OnPageChanged;
            return UniTask.CompletedTask;
        }

        public void OnBuildButtonManipulator(IAppView appView, Button button, PayloadBuilder payloadBuilder)
        {
            foreach (var (className, pageUid) in NavigationLink.LinksPages)
            {
                if (button.ClassListContains(className))
                {
                    button.AddToClassList("signal-button");
                    var payload = payloadBuilder.CreatePayload();
                    payload.Set(NavigationLink.NavigateToKey, pageUid.Id);
                    return;
                }
            }
            
            foreach (var (className, popupId) in NavigationLink.LinksPopups)
            {
                if (button.ClassListContains(className))
                {
                    button.AddToClassList("signal-button");
                    var payload = payloadBuilder.CreatePayload();
                    payload.Set(NavigationLink.NavigateToKey, popupId.Id);
                    return;
                }
            }
        }

        private void OnPageChanged((PageId prev, PageId current) ctx)
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

        private void OnAnyButtonPressed(ButtonPressed buttonPressed)
        {
            if (!buttonPressed.Payload.IsValid()) return;

            if (buttonPressed.Payload.TryGet(NavigationLink.NavigateToKey, out var value))
            {
                if (value == 0)
                {
                    if (_appRunner.IsActiveAnyPopup())
                    {
                        _appRunner.CloseActivePopup();
                    }
                    else
                    {
                        _appRunner.SwitchToPrevPage();
                    }
                }
                else if (value > 0)
                {
                    var pageUid = PageId.FromRaw(value);
                    _appRunner.SwitchToPage(pageUid);
                }
                else
                {
                    var popupUid = PopupId.FromRaw(value);
                    _appRunner.OpenPopup(popupUid);
                }
            }
        }

        private void OnPageMounted(PageId pageUid, VisualElement pageRoot)
        {
            TryInitBackAction(pageUid, pageRoot);
            TryInitCursorSettings(pageUid, pageRoot);
        }

        private void TryInitBackAction(PageId pageUid, VisualElement pageRoot)
        {
            var backActionHook = pageRoot.Q<VisualElement>("back__action-hook");
            if (backActionHook == null) return;
            
            foreach (var className in backActionHook.GetClasses())
            {
                if (NavigationLink.LinksPages.TryGetValue(className, out var toPage))
                {
                    _backHooks.Add(pageUid, toPage);
                    return;
                }
            }

            if (backActionHook.ClassListContains("to-back-page__navigation"))
            {
                _backHooks.Add(pageUid, PageId.None);
                return;
            }
        }

        private void TryInitCursorSettings(PageId pageUid, VisualElement pageRoot)
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
