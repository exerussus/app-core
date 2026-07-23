using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Screens
{
    /// <summary>
    /// Опциональный контроллер визуала <c>CriticalScreen</c> (терминальный сбой).
    /// Назначается в инспектор. Если не задан — скрин рисует дефолтный код-оверлей.
    /// </summary>
    /// <remarks>
    /// Контроллер не трогает контейнер. Всё приходит аргументами: текст, доступность
    /// перезапуска и два колбэка — reboot и quit. Одна из этих кнопок обязана быть нажата:
    /// критический скрин не закрывается «в никуда», он ведёт либо в перезапуск, либо в выход.
    /// </remarks>
    public abstract class CriticalScreenController : MonoBehaviour
    {
        public abstract void OnMount(VisualElement root);

        /// <param name="message">Текст критической ошибки.</param>
        /// <param name="canReboot">Показывать ли кнопку перезапуска (если перезапуск имеет смысл).</param>
        /// <param name="reboot">Колбэк перезапуска приложения.</param>
        /// <param name="quit">Колбэк выхода из приложения.</param>
        public abstract UniTask OnShow(string message, bool canReboot, Action reboot, Action quit);
    }
}
