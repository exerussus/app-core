using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Audio
{
    public class SoundClickManipulator : Manipulator
    {
        public SoundClickManipulator(SoundAdapter soundAdapter, AudioClip[] soundClips)
        {
            _soundAdapter = soundAdapter;
            _soundClips = soundClips;
        }
        
        private readonly SoundAdapter _soundAdapter;
        private readonly AudioClip[] _soundClips;
        
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
            foreach (var audioClip in _soundClips) _soundAdapter.PlayUIShot(audioClip);
        }
    }
}
