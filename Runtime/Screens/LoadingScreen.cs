using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Layout;

namespace Exerussus.AppCore.Screens
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

        /// <summary>
        /// Контейнер безопасной зоны (элемент с именем <c>safeArea</c> в UXML).
        /// Кэшируется при монтировании. <c>null</c>, если вёрстка его не содержит.
        /// </summary>
        public VisualElement SafeArea { get; private set; }

        /// <summary>
        /// Монтирует визуал в слой скринов. Идемпотентно. Вызывается AppRunner-ом до старта
        /// boot-машины, чтобы безопасная зона раздалась экрану сразу, а не при первом показе.
        /// </summary>
        /// <returns><c>true</c>, если монтирование произошло именно сейчас.</returns>
        public bool Mount(VisualElement parent)
        {
            if (_isInitialized) return false;
            _isInitialized = true;
            _root = visualTree.Instantiate();
            _root.style.flexGrow = 1;
            _root.style.width = Length.Percent(100);
            _root.style.height = Length.Percent(100);
            _parent = parent;
            _parent.Add(_root);
            SafeArea = SafeAreaLayout.Find(_root);
            _hasController = loadingScreenController != null;
            if (_hasController) loadingScreenController.OnMount(_root);
            return true;
        }

        public async UniTask Show(VisualElement parent, bool instant = false)
        {
            gameObject.SetActive(true);
            Mount(parent);
            _root.style.display = DisplayStyle.Flex;

            // Флаг поднимаем до анимации: иначе параллельный запрос навигации увидит
            // экран «невидимым» и запустит второй показ поверх идущего.
            IsVisible = true;

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
