using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Screens
{
    /// <summary>
    /// Опциональный контроллер визуала <c>ErrorScreen</c> (закрываемая ошибка).
    /// Назначается в инспектор. Если не задан — скрин рисует дефолтный код-оверлей.
    /// </summary>
    /// <remarks>
    /// Контроллер, как и сам скрин, не имеет права трогать контейнер зависимостей.
    /// Он получает всё, что нужно, аргументами: текст ошибки и колбэк закрытия.
    /// </remarks>
    public abstract class ErrorScreenController : MonoBehaviour
    {
        public abstract void OnMount(VisualElement root);

        /// <param name="message">Текст ошибки для показа.</param>
        /// <param name="dismiss">Колбэк «закрыть» — контроллер зовёт его по нажатию своей кнопки.</param>
        public abstract UniTask OnShow(string message, Action dismiss);

        public abstract UniTask OnHide();
    }
}
