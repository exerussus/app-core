using System;
using AppCore.Runtime.Core.Models;
using Exerussus.DI;
using UnityEngine.UIElements;

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

    public interface IAppManipulatorBuilder
    {
        public virtual void OnBuildButtonManipulator(IAppView appView, Button button, PayloadBuilder payloadBuilder) {  }
        public virtual void OnBuildManipulators(IAppView appView) {  }
    }
}