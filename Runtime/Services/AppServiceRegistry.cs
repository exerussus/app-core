using UnityEngine;

namespace Exerussus.AppCore.Services
{
    public abstract class AppServiceRegistry : MonoBehaviour
    {
        public abstract IAppService[] GetAllServices();
    }
}
