using System.Threading;
using Cysharp.Threading.Tasks;
using Exerussus.DI;
using UnityEngine;

namespace App.Abstractions
{
    /// <summary>
    /// Хук, позволяющий выполнить асинхронную работу до и после инициализации сервисов
    /// в <see cref="App.AppRunner"/>. Подключается опционально через инспектор.
    /// </summary>
    public abstract class AppBootstrapper : MonoBehaviour
    {
        /// <summary>
        /// Вызывается ПОСЛЕ показа экрана загрузки, но ДО регистрации сервисов
        /// и инициализации страниц/попапов. Подходит для загрузки удалённого конфига,
        /// авторизации пользователя и прочей подготовки, которая должна быть готова
        /// к моменту старта сервисов.
        /// </summary>
        public abstract UniTask PreInitialize(DependenciesContainer container, CancellationToken token);

        /// <summary>
        /// Вызывается ПОСЛЕ полной инициализации сервисов и страниц, но ДО снятия экрана
        /// загрузки и перехода на дефолтную страницу. Подходит для прогрева кешей,
        /// предзагрузки данных через уже доступные сервисы и т.п.
        /// </summary>
        public abstract UniTask PostInitialize(DependenciesContainer container, CancellationToken token);
    }
}