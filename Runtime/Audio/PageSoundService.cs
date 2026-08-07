using System.Collections.Generic;
using Exerussus.DI;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Services;
using Exerussus.AppCore.Signals;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Audio
{
    public class PageSoundService : IAppService, IAppManipulatorBuilder
    {
        private UISoundLibrary _uiSoundLibrary;
        private SoundAdapter _soundAdapter;

        private readonly List<AudioClip> _audioClassesList = new();

        private bool _hasAdapter;

        public void OnInject(DependenciesContainer container)
        {
            _hasAdapter = container.TryGet(out _soundAdapter);

            // Библиотека необязательна глобально: вью может принести свою через OverrideSoundLibrary.
            container.TryGet(out _uiSoundLibrary);

            if (!_hasAdapter) Debug.LogWarning("PageSoundService: SoundAdapter не зарегистрирован — звуки UI отключены.");
        }

        public void OnBuildButtonManipulator(IAppView appView, Button button, PayloadBuilder payloadBuilder)
        {
            if (!_hasAdapter) return;

            // Своя библиотека вью имеет приоритет над общей. Раньше это поле читалось,
            // но никуда не передавалось, поэтому переопределение фактически не работало.
            var library = appView.OverrideSoundLibrary != null ? appView.OverrideSoundLibrary : _uiSoundLibrary;
            if (library == null || library.matches == null) return;

            _audioClassesList.Clear();

            foreach (var match in library.matches)
            {
                if (string.IsNullOrEmpty(match.className)) continue;
                if (!button.ClassListContains(match.className)) continue;
                if (_soundAdapter.TryGet(match.sound, out var clip)) _audioClassesList.Add(clip);
            }

            if (_audioClassesList.Count > 0)
            {
                button.AddToClassList("signal-button");
                button.AddManipulator(new SoundClickManipulator(_soundAdapter, _audioClassesList.ToArray()));
            }
        }
    }
}
