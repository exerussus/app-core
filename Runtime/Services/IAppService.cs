using System.Threading;
using Cysharp.Threading.Tasks;
using Exerussus.DI;

namespace Exerussus.AppCore.Services
{
    public interface IAppService
    {
        public void OnInject(DependenciesContainer container) {}

        /// <summary>
        /// Синхронная фаза: вызывается в стейте RegisteringServices сразу после инъекций,
        /// ДО инициализации страниц/попапов. Здесь нельзя открывать попапы (их ещё нет в реестре).
        /// </summary>
        public void PreInitialize() {}

        /// <summary>
        /// Асинхронная инициализация сервиса. Выполняется в стейте InitializingServices —
        /// ПОСЛЕ регистрации страниц/попапов, поэтому здесь уже можно открывать System-попапы
        /// и показывать скрины. Токен отменяется при уничтожении AppRunner.
        /// Бросьте <see cref="BootHaltException"/> для штатной остановки бута
        /// (гейт/регион-лок): машина уйдёт в Halted без аварийного оверлея.
        /// </summary>
        public UniTask Initialize(CancellationToken token) => UniTask.CompletedTask;

        /// <summary>
        /// Второй асинхронный проход: вызывается после того, как ВСЕ сервисы прошли
        /// <see cref="Initialize"/>. Подходит для связей между сервисами и прогрева кешей.
        /// </summary>
        public UniTask PostInitialize(CancellationToken token) => UniTask.CompletedTask;

        public void Destroy() {}
    }
}
