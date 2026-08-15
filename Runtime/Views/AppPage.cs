using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Audio;
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

        private readonly FragmentSlots _fragments = new();

        public string PageId => pageId;
        public AppPageController Controller => controller;
        public PageId PageUid { get; private set; }
        public AppRunner AppRunner { get; internal set; }
        public UISoundLibrary OverrideSoundLibrary => overrideSoundLibrary;

        public bool HasController => _hasController;

        /// <summary>Корневой элемент, созданный при монтировании. Null до вызова Mount.</summary>
        public TemplateContainer Root { get; private set; }

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
            if (controller != null) controller.Root = Root;
            Root.style.display = DisplayStyle.None;
            _fragments.CollectHosts(Root, AppRunner);
            return true;
        }

        public void Unmount()
        {
            Root.style.display = DisplayStyle.None;
        }

        public void PreInitialize()
        {
            _hasController = controller != null;
            if (_hasController) controller.Page = this;
            PageUid = new PageId(pageId);
            _fragments.CollectFragments(this);
        }

        public async UniTask Activate()
        {
            Root.style.display = DisplayStyle.Flex;
            if (_hasController)
            {
                await Controller.OnActivate();
            }
        }

        // ------------------------------------------------------------------ фрагменты

        /// <summary>
        /// Разворачивает фрагмент в хосте. Хост берётся из аргумента, иначе из настроек самого
        /// фрагмента, иначе — единственный хост страницы.
        /// </summary>
        public UniTask ShowFragment(string fragmentId, string hostId = null)
            => _fragments.Show(new FragmentId(fragmentId), hostId);

        /// <inheritdoc cref="ShowFragment(string,string)"/>
        public UniTask ShowFragment(FragmentId fragmentId, string hostId = null)
            => _fragments.Show(fragmentId, hostId);

        /// <summary>Скрывает то, что показано в хосте.</summary>
        public UniTask HideFragment(string hostId = null) => _fragments.Hide(hostId);

        /// <summary>Что сейчас показано в хосте. <c>null</c> — ничего.</summary>
        public AppFragment GetShownFragment(string hostId = null) => _fragments.GetShown(hostId);
    }
}
