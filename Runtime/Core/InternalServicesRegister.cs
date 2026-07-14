using App.Abstractions;
using App.Services.Navigator;
using AppCore.Runtime.Core.InternalServices.Manipulators.Audio;

namespace App
{
    internal static class InternalServicesRegister
    {
        public static IAppService[] GetAllServices()
        {
            return new IAppService[]
            {
                new NavigatorService(),
                new AudioPageService(),
            };
        }
    }   
}