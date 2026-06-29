using Exerussus.DI;

namespace App.Abstractions
{
    public interface IAppService
    {
        public void OnInject(DependenciesContainer container) {}
        public void Initialize() {}
        public void Destroy() {}
    }

    public interface IAppServiceUpdate : IAppService
    {
        public void Update();
    }
}