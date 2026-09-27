using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Screens
{
    /// <summary>
    /// Экран загрузки. Монтируется поверх всех слоёв и управляет видимостью через opacity.
    /// </summary>
    public class LoadingScreen : MonoBehaviour
    {
        [Tooltip("Вёрстка экрана загрузки: один uxml со слоями FullLayer (заливка, фон) и SafeLayer (логотип, прогресс, подписи).")]
        [SerializeField] private VisualTreeAsset visualTree;

        [SerializeField] private LoadingScreenController loadingScreenController;

        // Наследие 3.x: вёрстка из двух файлов. Читается только миграцией в редакторе.
        [SerializeField, HideInInspector, FormerlySerializedAs("fullTree")] private VisualTreeAsset legacyFullTree;
        [SerializeField, HideInInspector, FormerlySerializedAs("safeTree")] private VisualTreeAsset legacySafeTree;
        
        [SerializeField, Tooltip("Дефолтное время затухания при отсутствии контроллера")] 
        private float fadeSeconds = 0.5f;
        
        private VisualElement _parent;
        private readonly ViewRoot _view = new();
        private VisualElement _root;
        private bool _hasController;
        private bool _isInitialized;
        
        public bool IsVisible { get; private set; }

        /// <summary>Проставляется AppRunner-ом до монтирования — нужен для регистрации безопасной зоны.</summary>
        public AppRunner AppRunner { get; internal set; }

        /// <summary>
        /// Монтирует визуал в слой скринов. Идемпотентно. Вызывается AppRunner-ом до старта
        /// boot-машины, а не при первом показе.
        /// </summary>
        /// <returns><c>true</c>, если монтирование произошло именно сейчас.</returns>
        public bool Mount(VisualElement parent)
        {
            if (_isInitialized) return false;
            _isInitialized = true;

            if (!_view.Build(nameof(LoadingScreen), visualTree))
            {
                Debug.LogError(legacyFullTree != null || legacySafeTree != null
                    ? "[AppCore] LoadingScreen ещё на двух файлах вёрстки (3.x). Запустите Exerussus/App/Migrate to 4.0."
                    : "[AppCore] LoadingScreen: не задана вёрстка.", this);
                return false;
            }

            _root = _view.Root;
            _parent = parent;
            _parent.Add(_root);

            AppRunner?.RegisterSafeArea(_view);

            _hasController = loadingScreenController != null;
            if (_hasController) loadingScreenController.OnMount(_root);
            return true;
        }

        public async UniTask Show(VisualElement parent, bool instant = false)
        {
            gameObject.SetActive(true);
            Mount(parent);
            if (_root == null) return;   // вёрстки нет — показывать нечего, ошибка уже в логе
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
            if (_root != null) _root.style.display = DisplayStyle.None;
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
                elapsed += Time.unscaledDeltaTime;   // экран загрузки обязан жить при timeScale = 0
                _root.style.opacity = Mathf.Lerp(start, target, elapsed / fadeSeconds);
                await UniTask.NextFrame();
            }

            _root.style.opacity = target;
        }
    }
}
