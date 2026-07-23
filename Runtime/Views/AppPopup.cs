using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Layout;
using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore.Views
{
    /// <summary>
    /// Базовый класс попапа для UI Toolkit.
    /// Монтируется в popupsLayer поверх страниц как абсолютный оверлей.
    /// </summary>
    public class AppPopup : MonoBehaviour
    {
        [SerializeField] private string popupId;
        [SerializeField] private VisualTreeAsset visualTree;
        [SerializeField] private AppPopupController controller;

        private bool _hasController;

        public PopupId PopupUid { get; private set; }
        public string PopupId => popupId;
        public AppPopupController Controller => controller;
        public bool HasController => _hasController;

        public TemplateContainer Root { get; private set; }

        /// <summary>
        /// Контейнер безопасной зоны попапа (элемент с именем <c>safeArea</c> в UXML).
        /// Кэшируется при первом монтировании. <c>null</c>, если вёрстка его не содержит.
        /// </summary>
        public VisualElement SafeArea { get; private set; }

        private bool _registered;

        /// <summary>Монтирует попап в слой (при первом вызове) и показывает его.</summary>
        /// <returns><c>true</c>, если попап смонтирован именно сейчас (первый раз).</returns>
        public bool Mount(VisualElement parent)
        {
            var isNew = false;

            if (Root == null)
            {
                isNew = true;
                Root = visualTree.Instantiate();
                Root.style.position = Position.Absolute;
                Root.style.left = 0;
                Root.style.right = 0;
                Root.style.top = 0;
                Root.style.bottom = 0;
                parent.Add(Root);
                SafeArea = SafeAreaLayout.Find(Root);
                if (_hasController) controller.Root = Root;
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
            PopupUid = new PopupId(popupId);
            Debug.Log($"[DEBUG] Loaded popup {PopupUid}", this);
        }
    }
}
