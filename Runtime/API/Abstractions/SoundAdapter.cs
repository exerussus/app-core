using UnityEngine;

namespace App.Abstractions
{
    public abstract class SoundAdapter : MonoBehaviour
    {
        public abstract void PlayUIShot(AudioClip clip);
        public abstract bool TryGet(string id, out AudioClip clip);
    }
}