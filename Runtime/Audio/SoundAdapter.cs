using UnityEngine;

namespace Exerussus.AppCore.Audio
{
    public abstract class SoundAdapter : MonoBehaviour
    {
        public abstract void PlayUIShot(AudioClip clip);
        public abstract bool TryGet(string id, out AudioClip clip);
    }
}
