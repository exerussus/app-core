using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Audio;
using Exerussus.AppCore.Layout;
using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Базовый класс страницы приложения для UI Toolkit.
    /// Держит VisualTreeAsset и монтирует его в переданный контейнер при активации.
    /// </summary>
    public class AppPage : MonoBehaviour, IAppView
    {
        [SerializeField] private string pageId;
        [SerializeField] private VisualTreeAsset visualTree;
        [SerializeField] private UISoundLibrary overrideSoundLibrary;
        [SerializeField] private AppPageController controller;
        private bool _hasController;
        
        public string PageId => pageId;
        public AppPageController Controller => controller;
        public PageId PageUid { get; private set; }
        public AppRunner AppRunner { get; internal set; }
        public UISoundLibrary OverrideSoundLibrary => overrideSoundLibrary;

        public bool HasController => _hasController;

        /// <summary>Корневой элемент, созданный при монтировании. Null до вызова Mount.</summary>
        public TemplateContainer Root { get; private set; }

        /// <summary>
        /// Контейнер безопасной зоны страницы (элемент с именем <c>safeArea</c> в UXML).
        /// Кэшируется при монтировании: поиск выполняется ровно один раз за жизнь страницы.
        /// <c>null</c>, если вёрстка такого контейнера не содержит — тогда страница просто
        /// не участвует в раскладке safe area.
        /// </summary>
        public VisualElement SafeArea { get; private set; }

        /// <summary>Клонирует UXML и добавляет в переданный слой.</summary>
        public bool Mount(VisualElement parent)
        {
            if (Root != null) return false;
            
            Root = visualTree.Instantiate();
            Root.AddToClassList("page");
            Root.style.flexGrow = 1;
            Root.name = pageId;
            Root.pickingMode = PickingMode.Ignore;
            parent.Add(Root);
            SafeArea = SafeAreaLayout.Find(Root);
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
            PageUid = new PageId(pageId);
        }

        public async UniTask Activate()
        {
            Root.style.display = DisplayStyle.Flex;
            if (_hasController)
            {
                await Controller.OnActivate();
            }
        }
    }
}
