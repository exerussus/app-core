using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Audio.Editor
{
    /// <summary>
    /// Прослушивание в редакторе без Play Mode: один скрытый AudioSource. «Как в игре» считает звук
    /// по тем же правилам, что <see cref="AppSound"/>: режим, дефолт, случайный клип без повтора,
    /// громкость общая × событие × (дефолт) × клип, случайная высота из диапазона.
    /// Владелец — тот, кто открыл прослушивание (вкладка): он зовёт <see cref="Dispose"/>.
    /// </summary>
    public sealed class SoundPreview
    {
        private GameObject _host;
        private AudioSource _source;
        private int _lastPick = -1;
        private string _lastKey;

        public AudioClip Clip => _source != null ? _source.clip : null;
        public bool IsPlaying => _source != null && _source.isPlaying;
        public float Time => _source != null ? _source.time : 0f;
        public float Pitch => _source != null ? _source.pitch : 1f;
        public float Volume => _source != null ? _source.volume : 0f;

        /// <summary>Файл как есть: громкость 1, высота 1.</summary>
        public void PlayRaw(AudioClip clip) => Play(clip, 1f, 1f);

        /// <summary>
        /// Как в игре. <paramref name="entry"/> и <paramref name="clips"/> — черновик (клипы ещё могут быть вне папки).
        /// Возвращает текст причины тишины или <c>null</c>, если звук пошёл.
        /// </summary>
        public string PlayAsGame(SoundSettings settings, in SoundEntry entry, IReadOnlyList<DraftClip> clips)
        {
            float master = settings != null ? settings.MasterVolume : 1f;
            if (entry.mode == SoundMode.Off) return "режим «Молчит»";

            float volume = Mathf.Clamp01(entry.volume);
            float pitchMin = entry.pitchMin, pitchMax = entry.pitchMax;
            var pool = new List<(AudioClip clip, float volume)>();
            string poolKey = entry.key;

            if (entry.mode == SoundMode.Default && !entry.isDefault)
            {
                SoundEntry[] entries = settings != null ? settings.Entries : System.Array.Empty<SoundEntry>();
                int d = SoundEdit.IndexOf(entries, entry.defaultKey);
                if (d < 0) return $"дефолт «{entry.defaultKey}» не найден";
                SoundEntry def = entries[d];
                if (!def.isDefault) return $"«{def.key}» не дефолт";
                if (def.mode == SoundMode.Off) return $"дефолт «{def.key}» выключен";
                volume *= Mathf.Clamp01(def.volume);
                pitchMin = def.pitchMin;
                pitchMax = def.pitchMax;
                poolKey = def.key;
                if (def.clips != null)
                    foreach (SoundClip c in def.clips)
                        if (c.clip != null) pool.Add((c.clip, c.volume));
            }
            else
            {
                foreach (DraftClip c in clips)
                {
                    AudioClip clip = c.Clip;
                    if (clip == null && !string.IsNullOrEmpty(c.ExternalPath)) continue; // внешний файл слышен после Apply
                    if (clip != null) pool.Add((clip, c.Volume));
                }
            }

            if (pool.Count == 0) return "нет клипов";

            if (_lastKey != poolKey) { _lastKey = poolKey; _lastPick = -1; }
            int pick;
            if (pool.Count == 1) pick = 0;
            else if (_lastPick < 0) pick = Random.Range(0, pool.Count);
            else
            {
                pick = Random.Range(0, pool.Count - 1);
                if (pick >= _lastPick) pick++;
            }
            _lastPick = pick;

            float min = Mathf.Clamp(Mathf.Min(pitchMin, pitchMax), 0.5f, 2f);
            float max = Mathf.Clamp(Mathf.Max(pitchMin, pitchMax), 0.5f, 2f);
            Play(pool[pick].clip, master * volume * Mathf.Clamp01(pool[pick].volume), Random.Range(min, max));
            return null;
        }

        public void Stop()
        {
            if (_source != null) _source.Stop();
        }

        public void Dispose()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            _host = null;
            _source = null;
        }

        private void Play(AudioClip clip, float volume, float pitch)
        {
            if (clip == null) return;
            if (_source == null)
            {
                _host = EditorUtility.CreateGameObjectWithHideFlags("[AppSound Preview]", HideFlags.HideAndDontSave, typeof(AudioSource));
                _source = _host.GetComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.spatialBlend = 0f;
            }

            _source.Stop();
            _source.clip = clip;
            _source.volume = volume;
            _source.pitch = pitch;
            _source.Play();
        }
    }
}
