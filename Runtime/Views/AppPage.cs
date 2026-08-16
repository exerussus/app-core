using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Audio;
using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Базовый класс страницы приложения для UI Toolkit.
    /// Держит два VisualTreeAsset и монтирует их в переданный слой при активации.
    /// </summary>
    /// <remarks>
    /// Вёрстка разделена на два дерева: <c>fullTree</c> — во всю полосу кадра (фон, арт,
    /// всё, что обязано доходить до выреза), <c>safeTree</c> — внутри безопасной зоны
    /// (интерактив, текст). Любое из двух можно не задавать; оба сразу — нельзя.
    /// </remarks>
    public class AppPage : MonoBehaviour, IAppView
    {
        [SerializeField] private string pageId;

        [Tooltip("Вёрстка во всю полосу кадра: фон, арт, всё что должно доходить до выреза. Необязательна.")]
        [SerializeField] private VisualTreeAsset fullTree;

        [Tooltip("Вёрстка внутри безопасной зоны: интерактив, текст. Необязательна.")]
        [SerializeField] private VisualTreeAsset safeTree;

        [SerializeField] private UISoundLibrary overrideSoundLibrary;
        [SerializeField] private AppPageController controller;
        private bool _hasController;

        private readonly ViewRoot _view = new();
        private readonly FragmentSlots _fragments = new();

        public string PageId => pageId;
        public AppPageController Controller => controller;
        public PageId PageUid { get; private set; }
        public AppRunner AppRunner { get; internal set; }
        public UISoundLibrary OverrideSoundLibrary => overrideSoundLibrary;

        public bool HasController => _hasController;

        /// <summary>Корневой элемент, созданный при монтировании. Null до вызова Mount.</summary>
        public VisualElement Root => _view.Root;

        /// <summary>Слой полноэкранной вёрстки. Null, если дерево не задано.</summary>
        public VisualElement FullRoot => _view.Full;

        /// <summary>Слой безопасной зоны. Null, если дерево не задано.</summary>
        public VisualElement SafeRoot => _view.Safe;

        /// <summary>Собирает вёрстку и добавляет в переданный слой.</summary>
        public bool Mount(VisualElement parent)
        {
            if (_view.IsBuilt) return false;

            if (!_view.Build(pageId, fullTree, safeTree))
            {
                Debug.LogError($"[AppCore] Странице \"{pageId}\" не задано ни одного VisualTreeAsset.");
                return false;
            }

            Root.AddToClassList("page");
            parent.Add(Root);

            if (controller != null) controller.Root = Root;
            Root.style.display = DisplayStyle.None;

            // Безопасная зона раздаётся централизованно: реестр живёт в AppRunner,
            // одна запись на слой при каждом реальном изменении экрана.
            AppRunner?.RegisterSafeArea(_view);

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
        /// фрагмента, иначе — единственный хост страницы. Хост может стоять в любом из двух
        /// слоёв, и фрагмент унаследует его поведение.
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
