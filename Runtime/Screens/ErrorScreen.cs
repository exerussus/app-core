using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Screens
{
    /// <summary>
    /// Скрин закрываемой ошибки: показал сообщение → пользователь закрыл → жизнь продолжается.
    /// НЕ терминальный. Доступен на любом этапе инициализации App и в рантайме.
    /// </summary>
    /// <remarks>
    /// Отличие от <see cref="CriticalScreen"/>: тот ведёт в reboot/quit и не даёт продолжить.
    /// Здесь — просто уведомление, которое можно закрыть.
    /// Визуал: либо назначенный <see cref="ErrorScreenController"/>, либо дефолтный код-оверлей.
    /// </remarks>
    public class ErrorScreen : AppScreen
    {
        [SerializeField] private ErrorScreenController controller;

        private bool _hasController;

        /// <summary>Вызывается после закрытия скрина пользователем.</summary>
        public event Action Dismissed;

        protected override void OnMounted(VisualElement root)
        {
            _hasController = controller != null;
            if (_hasController) controller.OnMount(root);
        }

        /// <summary>Показывает ошибку. Возврат управления — по нажатию «закрыть».</summary>
        public void Show(string message)
        {
            Reveal();

            if (_hasController)
            {
                controller.OnShow(message, Dismiss).Forget(Debug.LogException);
            }
            else if (_messageLabel != null)
            {
                _messageLabel.text = message;
            }
            else
            {
                Debug.LogError("[ErrorScreen] visualTree задан без ErrorScreenController — нечем показать сообщение. Либо назначьте контроллер, либо оставьте visualTree пустым для дефолтного оверлея.");
            }
        }

        private void Dismiss()
        {
            if (!IsVisible) return;

            if (_hasController) controller.OnHide().Forget(Debug.LogException);
            Hide().Forget(Debug.LogException);

            try { Dismissed?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ─── Дефолтный код-оверлей (когда контроллер не задан) ───

        private Label _messageLabel;

        protected override VisualElement BuildFallback()
        {
            var dimmer = BuildDimmer();

            var box = BuildSafeAreaBox();

            _messageLabel = BuildMessageLabel(string.Empty);
            box.Add(_messageLabel);

            var close = BuildButton("Закрыть");
            close.clicked += Dismiss;
            box.Add(close);

            dimmer.Add(box);
            return dimmer;
        }
    }
}
