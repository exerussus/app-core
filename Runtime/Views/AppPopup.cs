using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Audio;
using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Базовый класс попапа для UI Toolkit.
    /// Монтируется в popupsLayer поверх страниц как абсолютный оверлей.
    /// </summary>
    /// <remarks>
    /// Вёрстка, как и у страницы, разделена: диммер и затемнение — в <c>fullTree</c>, иначе
    /// у выреза останется незатемнённая полоска; содержимое попапа — в <c>safeTree</c>.
    /// </remarks>
    public class AppPopup : MonoBehaviour, IAppView
    {
        [SerializeField] private string popupId;

        [Tooltip("Вёрстка во всю полосу кадра: диммер, затемнение фона. Необязательна.")]
        [SerializeField] private VisualTreeAsset fullTree;

        [Tooltip("Вёрстка внутри безопасной зоны: содержимое попапа. Необязательна.")]
        [SerializeField] private VisualTreeAsset safeTree;

        [SerializeField] private UISoundLibrary overrideSoundLibrary;
        [SerializeField] private AppPopupController controller;

        private bool _hasController;

        private readonly ViewRoot _view = new();
        private readonly FragmentSlots _fragments = new();

        public PopupId PopupUid { get; private set; }
        public string PopupId => popupId;
        public AppPopupController Controller => controller;
        public bool HasController => _hasController;
        public AppRunner AppRunner { get; internal set; }

        /// <summary>Своя библиотека звуков попапа. Пусто — берётся общая из <see cref="AppRunner"/>.</summary>
        public UISoundLibrary OverrideSoundLibrary => overrideSoundLibrary;

        public VisualElement Root => _view.Root;

        /// <summary>Слой полноэкранной вёрстки. Null, если дерево не задано.</summary>
        public VisualElement FullRoot => _view.Full;

        /// <summary>Слой безопасной зоны. Null, если дерево не задано.</summary>
        public VisualElement SafeRoot => _view.Safe;

        /// <summary>Монтирует попап в слой (при первом вызове) и показывает его.</summary>
        /// <returns><c>true</c>, если попап смонтирован именно сейчас (первый раз).</returns>
        public bool Mount(VisualElement parent)
        {
            var isNew = false;

            if (!_view.IsBuilt)
            {
                if (!_view.Build(popupId, fullTree, safeTree))
                {
                    Debug.LogError($"[AppCore] Попапу \"{popupId}\" не задано ни одного VisualTreeAsset.");
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
            Root.style.display = DisplayStyle.None;
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
