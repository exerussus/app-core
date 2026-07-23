using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Audio;

namespace Exerussus.AppCore.Services
{
    internal static class InternalServiceRegistry
    {
        public static IAppService[] GetAllServices()
        {
            return new IAppService[]
            {
                new NavigatorService(),
                new PageSoundService(),
            };
        }
    }   
}
