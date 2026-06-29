using App.Abstractions;
using App.Services.Navigator;

namespace App
{
    internal static class InternalServicesRegister
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