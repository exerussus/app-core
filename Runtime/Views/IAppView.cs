using UnityEngine.UIElements;
using Exerussus.AppCore.Audio;

namespace Exerussus.AppCore.Views
{
    public interface IAppView
    {
        public TemplateContainer Root { get;}
        public UISoundLibrary OverrideSoundLibrary { get;}
    }
}
