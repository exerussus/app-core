using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
using Exerussus.AppCore.Views;
using Exerussus.AppCore.Localization;

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
        private readonly ViewText _text = new();

        /// <summary>Анимация затухания, идущая сейчас. Новый Show/Hide отменяет прежнюю.</summary>
        private CancellationTokenSource _fadeCts;

        /// <summary>
        /// Номер последнего запроса Show/Hide. Запрос, который за время своих await'ов
        /// перебили новым, не доводит дело до конца — иначе Hide погасил бы уже показанный экран.
        /// </summary>
        private int _transition;

        /// <summary>
        /// Логическое состояние: куда экран идёт, а не что сейчас на экране. <c>true</c> с начала
        /// Show, <c>false</c> с начала Hide — во время затухания объект ещё активен.
        /// </summary>
        public bool IsVisible { get; private set; }

        /// <summary>
        /// Тексты экрана загрузки: ключи <c>^</c> вёрстки переведены и сменятся вместе с языком
        /// (как у Error/Critical скринов). Строки из кода — <c>Text.Set(label, "^KEY")</c>.
        /// </summary>
        public LocBinder Text => _text.Binder;

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

            // После OnMount: тексты, которые контроллер достроил кодом, тоже переводятся.
            _text.Attach(_root, this, null);
            return true;
        }

        public async UniTask Show(VisualElement parent, bool instant = false)
        {
            var transition = ++_transition;
            StopFade();

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
                    await FadeTo(1f, transition);
                }
            }
        }

        public async UniTask Hide()
        {
            var transition = ++_transition;
            StopFade();

            // Логическое состояние меняется сразу: запрос показа, пришедший во время затухания,
            // должен увидеть экран скрывающимся и поднять его заново.
            IsVisible = false;

            if (_hasController)
            {
                await loadingScreenController.OnHide();
            }
            else
            {
                await FadeTo(0f, transition);
            }

            // Пока ждали, экран снова попросили показать — новый Show главнее, гасить нельзя.
            if (transition != _transition) return;

            gameObject.SetActive(false);
            if (_root != null) _root.style.display = DisplayStyle.None;
        }

        private void OnDestroy()
        {
            StopFade();
            _text.Detach();
        }

        /// <summary>Отменяет идущее затухание. Отменённая анимация больше не трогает opacity.</summary>
        private void StopFade()
        {
            if (_fadeCts == null) return;

            _fadeCts.Cancel();
            _fadeCts = null;
        }

        private async UniTask FadeTo(float target, int transition)
        {
            if (_root == null) return;

            _fadeCts = new CancellationTokenSource();
            var token = _fadeCts.Token;

            float start   = _root.style.opacity.value;
            float elapsed = 0f;

            while (elapsed < fadeSeconds)
            {
                if (_root == null) return;
                elapsed += Time.unscaledDeltaTime;   // экран загрузки обязан жить при timeScale = 0
                _root.style.opacity = Mathf.Lerp(start, target, elapsed / fadeSeconds);

                // Перебили новым Show/Hide — тихо выходим, конечное состояние выставит он.
                if (await UniTask.NextFrame(token).SuppressCancellationThrow()) return;
                if (transition != _transition) return;
            }

            _root.style.opacity = target;
        }
    }
}
