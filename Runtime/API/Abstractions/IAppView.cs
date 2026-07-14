using AppCore.Runtime.Core.InternalServices.Manipulators.Audio;
using UnityEngine.UIElements;

namespace App.Abstractions
{
    public interface IAppView
    {
        public TemplateContainer Root { get;}
        public UISoundLibrary OverrideSoundLibrary { get;}
    }
}