using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;

namespace Exerussus.AppCore.Screens
{
    /// <summary>
    /// Скрин критического сбоя: терминальный. Из него нет пути «дальше» —
    /// только перезапуск приложения или выход. Доступен на любом этапе инициализации App.
    /// </summary>
    /// <remarks>
    /// Два источника показа:
    /// <list type="bullet">
    ///   <item><description>Ядро на <c>BootState.Failed</c> — шаг бута упал с исключением.</description></item>
    ///   <item><description>Сервис — намеренная остановка (гейт/регион-лок): сам зовёт <see cref="Show"/>
    ///   и бросает <c>BootHaltException</c>, чтобы машина встала в <c>Halted</c> без аварийного оверлея ядра.</description></item>
    /// </list>
    /// Визуал: либо назначенный <see cref="CriticalScreenController"/>, либо дефолтный код-оверлей.
    /// </remarks>
    public class CriticalScreen : AppScreen
    {
        [SerializeField] private CriticalScreenController controller;

        private bool _hasController;
        private Action _reboot;
        private Action _quit;

        protected override void OnMounted(VisualElement root)
        {
            _hasController = controller != null;
            if (_hasController) controller.OnMount(root);
        }

        /// <summary>
        /// Показывает критическую ошибку.
        /// </summary>
        /// <param name="message">Текст для пользователя.</param>
        /// <param name="canReboot">Доступен ли перезапуск (для транзиентных сбоев — да, для регион-лока — нет).</param>
        /// <param name="onReboot">Что делать по «перезапустить». Если null — берётся дефолт (перезагрузка сцены).</param>
        /// <param name="onQuit">Что делать по «выйти». Если null — <see cref="Application.Quit()"/>.</param>
        public void Show(string message, bool canReboot, Action onReboot = null, Action onQuit = null)
        {
            _reboot = onReboot ?? DefaultReboot;
            _quit = onQuit ?? DefaultQuit;

            Reveal();

            if (_hasController)
            {
                controller.OnShow(message, canReboot, InvokeReboot, InvokeQuit).Forget(Debug.LogException);
            }
            else if (_messageLabel != null)
            {
                _messageLabel.text = message;
                _rebootButton.style.display = canReboot ? DisplayStyle.Flex : DisplayStyle.None;
            }
            else
            {
                Debug.LogError("[CriticalScreen] вёрстка задана без CriticalScreenController — нечем показать сообщение. Либо назначьте контроллер, либо оставьте fullTree и safeTree пустыми для дефолтного оверлея.");
            }
        }

        private void InvokeReboot()
        {
            var cb = _reboot;
            _reboot = null;
            _quit = null;
            cb?.Invoke();
        }

        private void InvokeQuit()
        {
            var cb = _quit;
            _reboot = null;
            _quit = null;
            cb?.Invoke();
        }

        private static void DefaultReboot()
        {
            // Пуленепробиваемый перезапуск: чистая перезагрузка активной сцены —
            // AppRunner пересоздастся и заново прогонит Awake → boot-машину.
            var scene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(scene.buildIndex);
        }

        private static void DefaultQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ─── Дефолтный код-оверлей (когда контроллер не задан) ───

        private Label _messageLabel;
        private Button _rebootButton;

        // Затемнение — во всю полосу кадра, чтобы доходило до выреза.
        protected override VisualElement BuildFallbackBackdrop() => BuildDimmer();

        // Текст и кнопки — в безопасную зону, поверх затемнения.
        protected override VisualElement BuildFallbackContent()
        {
            var box = BuildContentBox();

            _messageLabel = BuildMessageLabel(string.Empty);
            box.Add(_messageLabel);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;

            _rebootButton = BuildButton("Перезапустить");
            _rebootButton.clicked += InvokeReboot;
            row.Add(_rebootButton);

            var quitButton = BuildButton("Выйти");
            quitButton.clicked += InvokeQuit;
            row.Add(quitButton);

            box.Add(row);
            return box;
        }
    }
}
