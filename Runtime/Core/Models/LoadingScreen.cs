
using App.Abstractions;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace App.Core
{
    /// <summary>
    /// Экран загрузки. Монтируется поверх всех слоёв и управляет видимостью через opacity.
    /// </summary>
    public class LoadingScreen : MonoBehaviour
    {
        [SerializeField] private VisualTreeAsset visualTree;
        [SerializeField] private LoadingScreenController loadingScreenController;
        
        [SerializeField, Tooltip("Дефолтное время затухания при отсутствии контроллера")] 
        private float fadeSeconds = 0.5f;
        
        private VisualElement _parent;
        private TemplateContainer _root;
        private bool _hasController;
        private bool _isInitialized;
        
        public bool IsVisible { get; private set; }

        private void Mount(VisualElement parent)
        {
            if (_isInitialized) return;
            _isInitialized = true;
            _root = visualTree.Instantiate();
            _root.style.flexGrow = 1;
            _root.style.width = Length.Percent(100);
            _root.style.height = Length.Percent(100);
            _parent = parent;
            _parent.Add(_root);
            _hasController = loadingScreenController != null;
            if (_hasController) loadingScreenController.OnMount(_root);
            IsVisible = true;
        }

        public async UniTask Show(VisualElement parent, bool instant = false)
        {
            gameObject.SetActive(true);
            Mount(parent);
            _root.style.display = DisplayStyle.Flex;

            if (_hasController)
            {
                await loadingScreenController.OnShow(parent, instant);
            }
            else
            {
                if (instant)
                {
                    _root.style.opacity = 1f;
                }
                else
                {
                    await FadeTo(1f);
                }
            }
            
            IsVisible = true;
        }

        public async UniTask Hide()
        {
            if (_hasController)
            {
                await loadingScreenController.OnHide();
            }
            else
            {
                await FadeTo(0f);
            }
            
            gameObject.SetActive(false);
            _root.style.display = DisplayStyle.None;
            IsVisible = false;
        }

        private async UniTask FadeTo(float target)
        {
            if (_root == null) return;
            
            float start   = _root.style.opacity.value;
            float elapsed = 0f;

            while (elapsed < fadeSeconds)
            {
                if (_root == null) return;
                elapsed += Time.deltaTime;
                _root.style.opacity = Mathf.Lerp(start, target, elapsed / fadeSeconds);
                await UniTask.NextFrame();
            }

            _root.style.opacity = target;
        }
    }
}