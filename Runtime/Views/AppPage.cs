using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Страница приложения на UI Toolkit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Страница жёстко привязана к своему id: имя GameObject равно id, вёрстка лежит строго
    /// по пути <c>Assets/App/Pages/&lt;Type&gt;/&lt;id&gt;.uxml</c>, контроллер называется
    /// <c>&lt;Type&gt;PageController</c>. Всё это проставляет инспектор при выборе id — руками
    /// поля не редактируются.
    /// </para>
    /// <para>
    /// Вёрстка — один uxml со слоями <see cref="FullLayer"/> и <see cref="SafeLayer"/>
    /// (см. <see cref="ViewRoot"/>).
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public class AppPage : MonoBehaviour, IAppView
    {
        [SerializeField] private string pageId;

        [SerializeField] private VisualTreeAsset visualTree;

        [SerializeField] private AppPageController controller;

        [Tooltip("Что делает «назад» (Escape / аппаратная кнопка) на этой странице.")]
        [SerializeField] private PageBackAction backAction = PageBackAction.None;

        [Tooltip("Цель «назад», если выбран переход на конкретную страницу.")]
        [SerializeField, PagesDropdown] private string backPageId;

        [Tooltip("Что сделать с курсором при входе на страницу.")]
        [SerializeField] private PageCursorMode cursorMode = PageCursorMode.Keep;

        // Наследие 3.x: вёрстка из двух файлов. Читается только миграцией в редакторе,
        // которая сливает пару в один uxml со слоями и очищает поля.
        [SerializeField, HideInInspector, FormerlySerializedAs("fullTree")] private VisualTreeAsset legacyFullTree;
        [SerializeField, HideInInspector, FormerlySerializedAs("safeTree")] private VisualTreeAsset legacySafeTree;

        private bool _hasController;

        private readonly ViewRoot _view = new();
        private readonly FragmentSlots _fragments = new();

        public string PageId => pageId;
        public ViewKind Kind => ViewKind.Page;
        public string ViewId => pageId;
        public AppPageController Controller => controller;
        public PageId PageUid { get; private set; }
        public AppRunner AppRunner { get; internal set; }

        public PageBackAction BackAction => backAction;
        public PageId BackPageUid { get; private set; }
        public PageCursorMode CursorMode => cursorMode;

        public bool HasController => _hasController;

        /// <summary>Корневой элемент, созданный при монтировании. Null до вызова Mount.</summary>
        public VisualElement Root => _view.Root;

        /// <summary>Слой полноэкранной вёрстки. Null, если его нет в uxml.</summary>
        public VisualElement FullRoot => _view.Full;

        /// <summary>Слой безопасной зоны. Null, если его нет в uxml.</summary>
        public VisualElement SafeRoot => _view.Safe;

        /// <summary>Собирает вёрстку и добавляет в переданный слой.</summary>
        public bool Mount(VisualElement parent)
        {
            if (_view.IsBuilt) return false;

            if (!_view.Build(pageId, visualTree))
            {
                if (legacyFullTree != null || legacySafeTree != null)
                    Debug.LogError($"[AppCore] Страница \"{pageId}\" ещё на двух файлах вёрстки (3.x). Запустите Exerussus/App/Migrate to 4.0.", this);
                else
                    Debug.LogError($"[AppCore] Странице \"{pageId}\" не задана вёрстка.", this);
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
            BackPageUid = backAction == PageBackAction.ToPage && !string.IsNullOrEmpty(backPageId)
                ? new PageId(backPageId)
                : Navigation.PageId.None;
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
