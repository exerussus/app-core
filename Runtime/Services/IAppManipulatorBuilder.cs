using UnityEngine.UIElements;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Services
{
    /// <summary>
    /// Сервис, достраивающий поведение вёрстки при первом монтировании вью: кнопки,
    /// манипуляторы, подписки. Вызывается один раз на вью (и на каждое монтирование
    /// фрагмента с <c>unmountOnHide</c>).
    /// </summary>
    public interface IAppManipulatorBuilder
    {
        public virtual void OnBuildButtonManipulator(IAppView appView, Button button) { }
        public virtual void OnBuildManipulators(IAppView appView) { }
    }
}
