using System.Collections.Generic;
using App.Abstractions;
using AppCore.Runtime.Core.Models;
using Exerussus.DI;
using UnityEngine;
using UnityEngine.UIElements;

namespace AppCore.Runtime.Core.InternalServices.Manipulators.Audio
{
    public class AudioPageService : IAppService, IAppManipulatorBuilder
    {
        private UISoundLibrary _uiSoundLibrary;
        private SoundAdapter _soundAdapter;

        private readonly Dictionary<string, int> _audioClasses = new();
        private readonly List<AudioClip> _audioClassesList = new();
        
        private bool _isValid;
        
        public void OnInject(DependenciesContainer container)
        {
            _isValid = container.TryGet(out _soundAdapter) && container.TryGet(out _uiSoundLibrary);

            if (_isValid)
            {
                for (var index = 0; index < _uiSoundLibrary.matches.Length; index++)
                {
                    var match = _uiSoundLibrary.matches[index];
                    _audioClasses.Add(match.className, index);
                }
            }
            else
            {
                Debug.LogWarning($"AudioPageService is not valid.");
            }
        }

        public void OnBuildButtonManipulator(IAppView appView, Button button, PayloadBuilder payloadBuilder)
        {
            if (!_isValid)
            {
                return;
            }
            
            _audioClassesList.Clear();

            foreach (var (key, soundIndex) in _audioClasses)
            {
                if (button.ClassListContains(key) && _soundAdapter.TryGet(_uiSoundLibrary.matches[soundIndex].sound, out var clip)) _audioClassesList.Add(clip);
            }
            
            if (_audioClassesList.Count > 0)
            {
                button.AddToClassList("signal-button");
                button.AddManipulator(new SoundClickManipulator(_soundAdapter, _audioClassesList.ToArray()));
            }
        }
    }
}