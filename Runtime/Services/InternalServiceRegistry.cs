using Exerussus.AppCore.Navigation;

namespace Exerussus.AppCore.Services
{
    internal static class InternalServiceRegistry
    {
        public static IAppService[] GetAllServices()
        {
            return new IAppService[]
            {
                new NavigatorService(),
            };
        }
    }
}
