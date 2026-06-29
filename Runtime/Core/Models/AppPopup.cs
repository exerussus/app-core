
using App.Abstractions;
using UnityEngine;
using UnityEngine.UIElements;


namespace App.Core
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

        public PopupUID PopupUid { get; private set; }
        public string PopupId => popupId;
        public AppPopupController Controller => controller;
        public bool HasController => _hasController;

        public TemplateContainer Root { get; private set; }
        private bool _registered;

        public void Mount(VisualElement parent)
        {
            if (Root == null)
            {
                Root = visualTree.Instantiate();
                Root.style.position = Position.Absolute;
                Root.style.left = 0;
                Root.style.right = 0;
                Root.style.top = 0;
                Root.style.bottom = 0;
                parent.Add(Root);
                if (_hasController) controller.Root = Root;
            }
            
            Root.style.display = DisplayStyle.Flex;
        }

        public void Unmount()
        {
            Root.style.display = DisplayStyle.None;
        }

        public void PreInitialize()
        {
            _hasController = controller != null;
            PopupUid = new PopupUID(PopupId);
            Debug.Log($"[DEBUG] Loaded popup {PopupUid}", this);
        }
    }
}