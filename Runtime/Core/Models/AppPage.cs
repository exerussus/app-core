
using App.Services.Navigator;
using App.UIToolkit.Manipulators;
using App.Abstractions;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace App.Core
{
    /// <summary>
    /// Базовый класс страницы приложения для UI Toolkit.
    /// Держит VisualTreeAsset и монтирует его в переданный контейнер при активации.
    /// </summary>
    public class AppPage : MonoBehaviour
    {
        [SerializeField] private string pageId;
        [SerializeField] private VisualTreeAsset visualTree;
        [SerializeField] private UISoundLibrary overrideSoundLibrary;
        [SerializeField] private AppPageController controller;
        private bool _hasController;
        
        public string PageId => pageId;
        public AppPageController Controller => controller;
        public PageUID PageUid { get; private set; }
        public UISoundLibrary DefaultSoundLibrary { get; set; }
        public SoundAdapter SoundAdapter { get; set; }
        public bool HasController => _hasController;

        /// <summary>Корневой элемент, созданный при монтировании. Null до вызова Mount.</summary>
        public TemplateContainer Root { get; private set; }

        private bool _registered;
        
        /// <summary>Клонирует UXML и добавляет в переданный слой.</summary>
        public bool Mount(VisualElement parent)
        {
            if (Root != null) return false;
            
            Root = visualTree.Instantiate();
            Root.AddToClassList("page");
            Root.style.flexGrow = 1;
            parent.Add(Root);
            if (controller != null) controller.Root = Root;
            Root.style.display = DisplayStyle.None;
            return true;
        }

        public void Unmount()
        {
            Root.style.display = DisplayStyle.None;
        }

        public void PreInitialize()
        {
            _hasController = controller != null;
            PageUid = new PageUID(PageId);
            Debug.Log($"[DEBUG] Loaded page {PageUid}", this);
        }

        public async UniTask Activate()
        {
            Root.style.display = DisplayStyle.Flex;
            if (_hasController)
            {
                await Controller.OnActivate();
            }
            
            if (!_registered)
            {
                _registered = true;
                var soundLibrary = overrideSoundLibrary == null ? DefaultSoundLibrary : overrideSoundLibrary;
                
                if (soundLibrary != null) Root.Query<Button>().ForEach(btn =>
                {
                    btn.AddManipulator(new SoundClickManipulator(SoundAdapter, soundLibrary));
                    if (btn.ClassListContains("signal-button")) btn.AddManipulator(new SignalClickManipulator());
                    else
                    {
                        foreach (var (className, pageUid) in NavigationLink.Links)
                        {
                            if (btn.ClassListContains(className))
                            {
                                btn.ClassListContains("signal-button");
                                btn.AddManipulator(new SignalClickManipulator());
                            }
                        }
                    }
                });
            }
        }
    }
}