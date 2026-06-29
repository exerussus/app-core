
using UnityEngine;

namespace App.Abstractions
{
    public abstract class AppServiceRegister : MonoBehaviour
    {
        public abstract IAppService[] GetAllServices();
    }
}