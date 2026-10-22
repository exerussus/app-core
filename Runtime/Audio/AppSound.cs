using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.IL2CPP.CompilerServices;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Exerussus.AppCore.Audio
{
    /// <summary>
    /// Звуки приложения: 2D-события по ключам каталога (<see cref="SoundSettings"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Вызов: <c>AppSound.Play(AppSounds.UI.Effects.Click_S)</c> — константа из сгенерированного
    /// <c>AppSounds</c> (номер записи в каталоге), или ручка <see cref="Get"/> по строке, взятая один раз.
    /// </para>
    /// <para>
    /// Каталог запекается при старте в плоские массивы; <see cref="Play"/> не аллоцирует, не трогает строк
    /// и SO, не зовёт <c>Time</c> и <c>UnityEngine.Random</c>: время кадра пишет своя система в PlayerLoop,
    /// случайность — свой xorshift. Выключено в настройках — ничего не создаётся, <see cref="Play"/> выходит
    /// на первой проверке.
    /// </para>
    /// </remarks>
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    public static class AppSound
    {
        // ---------------------------------------------------------------- запечённые данные

        /// <summary>Горячие данные события: всё, что читает Play, рядом.</summary>
        private struct Slot
        {
            public int ClipStart;
            public int ClipCount;      // 0 — молчит (выключено, пусто, дефолт молчит)
            public int LastClip;       // -1 — ещё не звучало
            public int Group;          // индекс в _groups
            public float Volume;       // событие × дефолт (если через дефолт)
            public float PitchMin;
            public float PitchRange;
            public float Cooldown;
            public float NextTime;
        }

        private static bool _on;             // запечено и включено
        private static bool _muted;
        private static float _master = 1f;
        private static float _now;

        private static Slot[] _slots = Array.Empty<Slot>();
        private static int _count;

        private static AudioClip[] _clips = Array.Empty<AudioClip>();
        private static float[] _clipVolume = Array.Empty<float>();
        private static float[] _clipLength = Array.Empty<float>();

        private static AudioMixerGroup[] _groups = Array.Empty<AudioMixerGroup>();

        private static AudioSource[] _voices = Array.Empty<AudioSource>();
        private static float[] _voiceEnd = Array.Empty<float>();
        private static int[] _voiceGroup = Array.Empty<int>();
        private static int _voiceCount;
        private static GameObject _host;

        private static uint _rng = 2463534242u;
        private static bool _clockInstalled;
        private static bool _focusHooked;
        private static bool _pauseUnfocused;
        private static HashSet<string> _warned;
        private static SoundEntry[] _catalog;   // каталог запущенного звука (тесты могут подменить настройки)

        // ---------------------------------------------------------------- публичное API

        /// <summary>Звук включён и запечён.</summary>
        public static bool Enabled => _on;

        /// <summary>Глушит все звуки (Play выходит сразу). Не трогает настройки.</summary>
        public static bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                if (value) StopAll();
            }
        }

        /// <summary>Общая громкость запуска (умножается на громкость событий). Старт — из настроек.</summary>
        public static float MasterVolume
        {
            get => _master;
            set => _master = value < 0f ? 0f : value > 1f ? 1f : value;
        }

        /// <summary>Сколько записей в каталоге (диапазон номеров для <see cref="Play"/>).</summary>
        public static int Count => _count;

        /// <summary>
        /// Поднять событие по номеру (константа <c>AppSounds.…_S</c>). Неверный номер, молчащее событие,
        /// кулдаун, выключенный звук — тихий выход. Без аллокаций.
        /// </summary>
        public static void Play(int id)
        {
            if (!_on || _muted) return;
            if ((uint)id >= (uint)_count) return;

            ref Slot slot = ref _slots[id];
            int count = slot.ClipCount;
            if (count == 0) return;

            float now = _now;
            if (now < slot.NextTime) return;
            slot.NextTime = now + slot.Cooldown;

            // случайный клип без повтора предыдущего: берём из count-1 и перешагиваем прошлый
            int pick;
            int last = slot.LastClip;
            if (count == 1) pick = 0;
            else if (last < 0) pick = Range(count);
            else
            {
                pick = Range(count - 1);
                if (pick >= last) pick++;
            }
            slot.LastClip = pick;

            int clip = slot.ClipStart + pick;
            float pitch = slot.PitchMin + slot.PitchRange * Unit();

            int voice = PickVoice(now);
            AudioSource source = _voices[voice];

            if (_voiceGroup[voice] != slot.Group)
            {
                _voiceGroup[voice] = slot.Group;
                source.outputAudioMixerGroup = _groups[slot.Group];
            }

            source.clip = _clips[clip];
            source.pitch = pitch;
            source.volume = slot.Volume * _clipVolume[clip] * _master;
            source.Play();

            _voiceEnd[voice] = now + _clipLength[clip] / pitch;
        }

        /// <summary>
        /// Ручка события по ключу (<c>"UI.Effects.Click"</c>). Брать один раз (поле <c>static readonly</c>,
        /// <c>Awake</c>), не на каждый вызов: поиск — двоичный по каталогу. Нет ключа — пустая ручка
        /// (молчит) и одно предупреждение.
        /// </summary>
        public static SoundHandle Get(string key)
        {
            int id = IndexOf(key);
            if (id < 0) WarnMissing(key);
            return new SoundHandle(id);
        }

        /// <summary>Номер записи по ключу или -1. Без аллокаций (двоичный поиск, ordinal).</summary>
        public static int IndexOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;

            SoundEntry[] entries = _catalog;
            if (entries == null)
            {
                SoundSettings settings = SoundSettings.Current;
                if (settings == null) return -1;
                entries = settings.Entries;
            }

            int lo = 0, hi = entries.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int cmp = string.CompareOrdinal(entries[mid].key, key);
                if (cmp == 0) return mid;
                if (cmp < 0) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>Остановить все звучащие голоса.</summary>
        public static void StopAll()
        {
            for (int i = 0; i < _voiceCount; i++)
            {
                if (_voices[i] != null) _voices[i].Stop();
                _voiceEnd[i] = 0f;
            }
        }

        /// <summary>
        /// Перечитать каталог (после импорта / правок в редакторе). Аллоцирует — не для игрового цикла.
        /// Номера событий не меняются, пока не меняется список ключей.
        /// </summary>
        public static void Reload()
        {
            Shutdown();
            Startup(SoundSettings.Current);
        }

        // ---------------------------------------------------------------- жизненный цикл

        // статика между запусками без перезагрузки домена — в ноль
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Shutdown();
            _muted = false;
            _warned = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot() => Startup(SoundSettings.Current);

        private static void Startup(SoundSettings settings)
        {
            if (settings == null || !settings.Enabled) return;

            _catalog = settings.Entries;
            Bake(settings);
            CreateVoices(settings);

            _master = settings.MasterVolume;
            _rng = (uint)Environment.TickCount | 1u;
            _now = Time.unscaledTime;

            if (Application.isPlaying) InstallClock();

            _pauseUnfocused = settings.PauseWhenUnfocused;
            if (_pauseUnfocused && !_focusHooked)
            {
                Application.focusChanged += OnFocusChanged;
                _focusHooked = true;
            }

            _on = _count > 0;
        }

        private static void Shutdown()
        {
            _on = false;
            UninstallClock();

            if (_focusHooked)
            {
                Application.focusChanged -= OnFocusChanged;
                _focusHooked = false;
            }

            if (_host != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_host);
                else UnityEngine.Object.DestroyImmediate(_host);
            }

            _host = null;
            _catalog = null;
            _slots = Array.Empty<Slot>();
            _count = 0;
            _clips = Array.Empty<AudioClip>();
            _clipVolume = Array.Empty<float>();
            _clipLength = Array.Empty<float>();
            _groups = Array.Empty<AudioMixerGroup>();
            _voices = Array.Empty<AudioSource>();
            _voiceEnd = Array.Empty<float>();
            _voiceGroup = Array.Empty<int>();
            _voiceCount = 0;
        }

        // ---------------------------------------------------------------- запекание

        private static void Bake(SoundSettings settings)
        {
            SoundEntry[] entries = settings.Entries;
            int n = entries.Length;

            // пул клипов: только свои клипы (режим Custom); события через дефолт ссылаются на диапазон дефолта
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                if (entries[i].mode != SoundMode.Custom || entries[i].clips == null) continue;
                SoundClip[] list = entries[i].clips;
                for (int c = 0; c < list.Length; c++)
                    if (list[c].clip != null) total++;
            }

            _clips = new AudioClip[total];
            _clipVolume = new float[total];
            _clipLength = new float[total];

            var start = new int[n];
            var length = new int[n];
            int pos = 0;
            for (int i = 0; i < n; i++)
            {
                start[i] = pos;
                if (entries[i].mode == SoundMode.Custom && entries[i].clips != null)
                {
                    SoundClip[] list = entries[i].clips;
                    for (int c = 0; c < list.Length; c++)
                    {
                        if (list[c].clip == null) continue;
                        _clips[pos] = list[c].clip;
                        _clipVolume[pos] = Mathf.Clamp01(list[c].volume);
                        _clipLength[pos] = list[c].clip.length;
                        pos++;
                    }
                }
                length[i] = pos - start[i];
            }

            // группы микшера: 0 — общая из настроек (может быть null)
            var groups = new List<AudioMixerGroup>(4) { settings.Output };

            _slots = new Slot[n];
            _count = n;

            for (int i = 0; i < n; i++)
            {
                ref readonly SoundEntry entry = ref entries[i];
                ref Slot slot = ref _slots[i];
                slot.LastClip = -1;
                slot.NextTime = float.NegativeInfinity;
                slot.Cooldown = Mathf.Max(0f, entry.cooldown);

                int source = i;
                float volume = Mathf.Clamp01(entry.volume);

                if (entry.mode == SoundMode.Off) continue;

                if (entry.mode == SoundMode.Default && !entry.isDefault)
                {
                    source = Find(entries, entry.defaultKey);
                    if (source < 0 || !entries[source].isDefault || entries[source].mode != SoundMode.Custom) continue;
                    volume *= Mathf.Clamp01(entries[source].volume);
                }
                else if (entry.mode != SoundMode.Custom) continue;

                ref readonly SoundEntry from = ref entries[source];
                slot.ClipStart = start[source];
                slot.ClipCount = length[source];
                slot.Volume = volume;

                float min = Mathf.Clamp(Mathf.Min(from.pitchMin, from.pitchMax), 0.5f, 2f);
                float max = Mathf.Clamp(Mathf.Max(from.pitchMin, from.pitchMax), 0.5f, 2f);
                slot.PitchMin = min;
                slot.PitchRange = max - min;

                AudioMixerGroup output = entry.output != null ? entry.output : source != i ? from.output : null;
                if (output != null)
                {
                    int g = groups.IndexOf(output);
                    if (g < 0)
                    {
                        g = groups.Count;
                        groups.Add(output);
                    }
                    slot.Group = g;
                }
            }

            _groups = groups.ToArray();
        }

        private static int Find(SoundEntry[] entries, string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            int lo = 0, hi = entries.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int cmp = string.CompareOrdinal(entries[mid].key, key);
                if (cmp == 0) return mid;
                if (cmp < 0) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }

        private static void CreateVoices(SoundSettings settings)
        {
            int count = settings.Voices;
            _host = new GameObject("[AppSound]") { hideFlags = HideFlags.HideAndDontSave };
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(_host);

            _voices = new AudioSource[count];
            _voiceEnd = new float[count];
            _voiceGroup = new int[count];
            _voiceCount = count;

            AudioMixerGroup output = _groups.Length > 0 ? _groups[0] : null;
            for (int i = 0; i < count; i++)
            {
                AudioSource source = _host.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.dopplerLevel = 0f;
                source.outputAudioMixerGroup = output;
                _voices[i] = source;
            }
        }

        // ---------------------------------------------------------------- голоса, случайность

        /// <summary>Свободный голос, иначе тот, что закончится раньше всех (вытеснение).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PickVoice(float now)
        {
            int best = 0;
            float bestEnd = _voiceEnd[0];
            for (int i = 0; i < _voiceCount; i++)
            {
                float end = _voiceEnd[i];
                if (end <= now) return i;
                if (end < bestEnd)
                {
                    bestEnd = end;
                    best = i;
                }
            }
            return best;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Next()
        {
            uint x = _rng;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _rng = x;
            return x;
        }

        /// <summary>[0, n) без деления.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Range(int n) => (int)(((ulong)Next() * (uint)n) >> 32);

        /// <summary>[0, 1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Unit() => (Next() >> 8) * (1f / 16777216f);

        // ---------------------------------------------------------------- время кадра (PlayerLoop)

        /// <summary>Маркер системы в PlayerLoop.</summary>
        private struct AppSoundClock { }

        private static readonly PlayerLoopSystem.UpdateFunction ClockTick = Tick;

        private static void Tick() => _now = Time.unscaledTime;

        private static void InstallClock()
        {
            if (_clockInstalled) return;
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (!Modify(ref root, true)) return;
            PlayerLoop.SetPlayerLoop(root);
            _clockInstalled = true;
        }

        private static void UninstallClock()
        {
            if (!_clockInstalled) return;
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (Modify(ref root, false)) PlayerLoop.SetPlayerLoop(root);
            _clockInstalled = false;
        }

        /// <summary>Добавить (или убрать) свою систему в начало PreUpdate.</summary>
        private static bool Modify(ref PlayerLoopSystem root, bool add)
        {
            PlayerLoopSystem[] phases = root.subSystemList;
            if (phases == null) return false;

            for (int p = 0; p < phases.Length; p++)
            {
                if (phases[p].type != typeof(PreUpdate)) continue;

                PlayerLoopSystem[] systems = phases[p].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                int existing = -1;
                for (int s = 0; s < systems.Length; s++)
                    if (systems[s].type == typeof(AppSoundClock)) existing = s;

                if (add)
                {
                    if (existing >= 0) return false;
                    var next = new PlayerLoopSystem[systems.Length + 1];
                    next[0] = new PlayerLoopSystem { type = typeof(AppSoundClock), updateDelegate = ClockTick };
                    Array.Copy(systems, 0, next, 1, systems.Length);
                    phases[p].subSystemList = next;
                }
                else
                {
                    if (existing < 0) return false;
                    var next = new PlayerLoopSystem[systems.Length - 1];
                    for (int s = 0, d = 0; s < systems.Length; s++)
                        if (s != existing) next[d++] = systems[s];
                    phases[p].subSystemList = next;
                }

                root.subSystemList = phases;
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- фокус

        private static void OnFocusChanged(bool focused)
        {
            if (!_pauseUnfocused) return;
            for (int i = 0; i < _voiceCount; i++)
            {
                AudioSource source = _voices[i];
                if (source == null) continue;
                if (focused) source.UnPause();
                else source.Pause();
            }
        }

        // ---------------------------------------------------------------- диагностика

        private static void WarnMissing(string key)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _warned ??= new HashSet<string>(StringComparer.Ordinal);
            if (_warned.Add(key ?? string.Empty))
                Debug.LogWarning($"[AppSound] Ключа «{key}» нет в каталоге — ручка молчит.");
#endif
        }

        // ---------------------------------------------------------------- для тестов и редактора

        /// <summary>Подменить время кадра (тесты без PlayerLoop).</summary>
        internal static void SetTime(float now) => _now = now;

        /// <summary>Запустить на указанных настройках (тесты, предпросмотр).</summary>
        internal static void StartWith(SoundSettings settings)
        {
            Shutdown();
            Startup(settings);
        }

        internal static void Stop() => Shutdown();

        internal static int ClipCountOf(int id) => (uint)id < (uint)_count ? _slots[id].ClipCount : -1;
        internal static int LastClipOf(int id) => (uint)id < (uint)_count ? _slots[id].LastClip : -1;
        internal static float VolumeOf(int id) => (uint)id < (uint)_count ? _slots[id].Volume : 0f;
        internal static int VoiceCount => _voiceCount;
    }
}
