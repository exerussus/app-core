using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Попап для UI Toolkit. Монтируется в popupsLayer поверх страниц как абсолютный оверлей.
    /// </summary>
    /// <remarks>
    /// Привязка та же, что у страницы: имя GameObject — id, вёрстка —
    /// <c>Assets/App/Popups/&lt;Type&gt;/&lt;id&gt;.uxml</c>, контроллер — <c>&lt;Type&gt;PopupController</c>.
    /// Диммер кладётся в <see cref="FullLayer"/> (иначе у выреза останется незатемнённая
    /// полоска), содержимое — в <see cref="SafeLayer"/>.
    /// </remarks>
    [DisallowMultipleComponent]
    public class AppPopup : MonoBehaviour, IAppView
    {
        [SerializeField] private string popupId;

        [SerializeField] private VisualTreeAsset visualTree;

        [SerializeField] private AppPopupController controller;

        // Наследие 3.x: вёрстка из двух файлов. Читается только миграцией в редакторе.
        [SerializeField, HideInInspector, FormerlySerializedAs("fullTree")] private VisualTreeAsset legacyFullTree;
        [SerializeField, HideInInspector, FormerlySerializedAs("safeTree")] private VisualTreeAsset legacySafeTree;

        private bool _hasController;

        private readonly ViewRoot _view = new();
        private readonly FragmentSlots _fragments = new();

        public PopupId PopupUid { get; private set; }
        public string PopupId => popupId;
        public ViewKind Kind => ViewKind.Popup;
        public string ViewId => popupId;
        public AppPopupController Controller => controller;
        public bool HasController => _hasController;
        public AppRunner AppRunner { get; internal set; }

        public VisualElement Root => _view.Root;

        /// <summary>Слой полноэкранной вёрстки. Null, если его нет в uxml.</summary>
        public VisualElement FullRoot => _view.Full;

        /// <summary>Слой безопасной зоны. Null, если его нет в uxml.</summary>
        public VisualElement SafeRoot => _view.Safe;

        /// <summary>Монтирует попап в слой (при первом вызове) и показывает его.</summary>
        /// <returns><c>true</c>, если попап смонтирован именно сейчас (первый раз).</returns>
        public bool Mount(VisualElement parent)
        {
            var isNew = false;

            if (!_view.IsBuilt)
            {
                if (!_view.Build(popupId, visualTree))
                {
                    if (legacyFullTree != null || legacySafeTree != null)
                        Debug.LogError($"[AppCore] Попап \"{popupId}\" ещё на двух файлах вёрстки (3.x). Запустите Exerussus/App/Migrate to 4.0.", this);
                    else
                        Debug.LogError($"[AppCore] Попапу \"{popupId}\" не задана вёрстка.", this);
                    return false;
                }

                isNew = true;
                parent.Add(Root);

                if (_hasController) controller.Root = Root;

                AppRunner?.RegisterSafeArea(_view);
                _fragments.CollectHosts(Root, AppRunner);
            }

            Root.style.display = DisplayStyle.Flex;
            return isNew;
        }

        public void Unmount()
        {
            if (Root != null) Root.style.display = DisplayStyle.None;
        }

        public void PreInitialize()
        {
            _hasController = controller != null;
            if (_hasController) controller.Popup = this;
            PopupUid = new PopupId(popupId);
            _fragments.CollectFragments(this);
        }

        // ------------------------------------------------------------------ фрагменты

        /// <summary>
        /// Разворачивает фрагмент в хосте. Хост берётся из аргумента, иначе из настроек самого
        /// фрагмента, иначе — единственный хост попапа.
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
