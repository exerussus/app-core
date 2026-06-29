
using App.Abstractions;
using UnityEngine;
using UnityEngine.UIElements;

namespace App.UIToolkit.Manipulators
{
    public class SoundClickManipulator : Manipulator
    {
        public SoundClickManipulator(SoundAdapter soundAdapter, UISoundLibrary library)
        {
            _soundAdapter = soundAdapter;
            _library = library;
            _isValid = _soundAdapter != null && _library != null;
        }
        
        private readonly SoundAdapter _soundAdapter;
        private readonly UISoundLibrary _library;
        private readonly bool _isValid;
        
        private bool _pointerDown;

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            target.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            _pointerDown = true;
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!_pointerDown) return;
            _pointerDown = false;
    
            if (!target.worldBound.Contains(evt.position)) return;

            PlaySound();
        }

        private void PlaySound()
        {
            if (!_isValid) return;

            AudioClip clip = null;
            var found = false;

            foreach (var match in _library.matches)
            {
                if (target.ClassListContains(match.className))
                {
                    _soundAdapter.TryGet(match.sound, out clip);
                    found = true;
                    break;
                }
            }

            if (!found) _soundAdapter.TryGet(_library.defaultButton, out clip);

            if (clip != null)
            {
                _soundAdapter.PlayUIShot(clip);
            }
        }
    }
}