using System;
using UnityEngine;
using UnityEngine.Audio;

namespace Exerussus.AppCore.Audio
{
    /// <summary>Как звучит событие.</summary>
    public enum SoundMode : byte
    {
        /// <summary>Молчит.</summary>
        [InspectorName("Молчит")] Off = 0,

        /// <summary>Играет дефолтный звук (<see cref="SoundEntry.defaultKey"/>). Дефолту этот режим недоступен.</summary>
        [InspectorName("Дефолт")] Default = 1,

        /// <summary>Свои клипы.</summary>
        [InspectorName("Свои клипы")] Custom = 2,
    }

    /// <summary>Шина: кто проигрывает звук. Сейчас есть только UI (2D); World — задел под 3D.</summary>
    public enum SoundBus : byte
    {
        [InspectorName("UI (2D)")] Ui = 0,
        [InspectorName("Мир (3D)")] World = 1,
    }

    /// <summary>Клип события и его поправка громкости (выровнять варианты без пересведения).</summary>
    [Serializable]
    public struct SoundClip
    {
        public AudioClip clip;

        [Range(0f, 1f)] public float volume;

        public static SoundClip Of(AudioClip clip) => new() { clip = clip, volume = 1f };
    }

    /// <summary>
    /// Звуковое событие каталога. Ключ — путь по точкам (<c>UI.Effects.Click</c>), он же путь папки клипов.
    /// Правится вкладкой Nexus → App → «Звуки»; в рантайме читается один раз при запекании.
    /// </summary>
    [Serializable]
    public struct SoundEntry
    {
        public const float DefaultPitchMin = 0.95f;
        public const float DefaultPitchMax = 1.05f;
        public const float DefaultCooldown = 0.05f;

        [Tooltip("Путь по точкам: UI.Effects.Click. До 5 уровней.")]
        public string key;

        [Tooltip("Название для людей.")]
        public string title;

        [Tooltip("Когда звучит — для каталога, html звуковика и списка звуков.")]
        [TextArea(1, 3)] public string when;

        [Tooltip("Дефолтный звук: на него ссылаются события в режиме «Дефолт». Сам ссылаться на дефолт не может.")]
        public bool isDefault;

        public SoundMode mode;

        [Tooltip("Ключ дефолта для режима «Дефолт».")]
        public string defaultKey;

        [Tooltip("Громкость события (поверх поправок клипов).")]
        [Range(0f, 1f)] public float volume;

        [Range(0.5f, 2f)] public float pitchMin;
        [Range(0.5f, 2f)] public float pitchMax;

        [Tooltip("Не чаще раза в столько секунд.")]
        [Min(0f)] public float cooldown;

        public SoundBus bus;

        [Tooltip("Своя группа микшера; пусто — общая из настроек.")]
        public AudioMixerGroup output;

        public SoundClip[] clips;

        /// <summary>Новая запись с умолчаниями.</summary>
        public static SoundEntry Create(string key, bool isDefault = false) => new()
        {
            key = key,
            title = string.Empty,
            when = string.Empty,
            isDefault = isDefault,
            mode = isDefault ? SoundMode.Custom : SoundMode.Default,
            defaultKey = string.Empty,
            volume = 1f,
            pitchMin = DefaultPitchMin,
            pitchMax = DefaultPitchMax,
            cooldown = DefaultCooldown,
            bus = SoundBus.Ui,
            clips = Array.Empty<SoundClip>(),
        };
    }

    /// <summary>
    /// Каталог и настройки звуков приложения. Ассет один, лежит по <see cref="AssetPath"/>; редактор создаёт
    /// его сам и кладёт в Preloaded Assets.
    /// </summary>
    /// <remarks>
    /// Пока галочка <see cref="Enabled"/> снята, звука нет вовсе: каталог не запекается, голоса не создаются,
    /// <see cref="AppSound.Play"/> выходит на первой проверке. Записи хранятся отсортированными по ключу
    /// (ordinal) — этот порядок и есть номера констант <c>AppSounds</c>; его держит редактор.
    /// </remarks>
    public sealed class SoundSettings : ScriptableObject
    {
        public const string AssetPath = AppCorePaths.SettingsDir + "/SoundSettings.asset";

        /// <summary>Корень папок клипов: ключ <c>UI.Effects.Click</c> → <c>Assets/AppSounds/UI/Effects/Click</c>.</summary>
        public const string ClipsRoot = "Assets/AppSounds";

        /// <summary>Сгенерированные константы ключей.</summary>
        public const string KeysPath = AppCorePaths.GeneratedDir + "/AppSounds.cs";

        public const int MaxDepth = 5;
        public const int MaxVoices = 32;

#pragma warning disable CS0649
        [Tooltip("Включить звуки. Выключено — ничего не запекается и не создаётся, Play выходит сразу.")]
        [SerializeField] private bool enabled;

        [Tooltip("Выключать в batch mode (сервер, CI).")]
        [SerializeField] private bool disableInBatchMode = true;

        [Range(0f, 1f)]
        [SerializeField] private float masterVolume = 1f;

        [Tooltip("Сколько звуков одновременно (пул AudioSource). На WebGL каждый — узел WebAudio.")]
        [Range(1, MaxVoices)]
        [SerializeField] private int voices = 8;

        [Tooltip("Группа микшера для всех звуков (необязательно).")]
        [SerializeField] private AudioMixerGroup output;

        [Tooltip("Глушить звук, когда окно/вкладка не в фокусе.")]
        [SerializeField] private bool pauseWhenUnfocused = true;

        [SerializeField] private SoundEntry[] entries = Array.Empty<SoundEntry>();
#pragma warning restore CS0649

        public bool Enabled => enabled && !(disableInBatchMode && Application.isBatchMode);
        public float MasterVolume => masterVolume;
        public int Voices => Mathf.Clamp(voices, 1, MaxVoices);
        public AudioMixerGroup Output => output;
        public bool PauseWhenUnfocused => pauseWhenUnfocused;

        /// <summary>Записи каталога (отсортированы по ключу). Массив живой — не менять в рантайме.</summary>
        public SoundEntry[] Entries => entries ??= Array.Empty<SoundEntry>();

        private static SoundSettings _current;

        /// <summary>
        /// Настройки текущего запуска. В сборке приходят через Preloaded Assets, в редакторе читаются
        /// по фиксированному пути. <c>null</c> — ассета нет, звук выключен.
        /// </summary>
        public static SoundSettings Current
        {
            get
            {
#if UNITY_EDITOR
                if (_current == null && !_lookedUp)
                {
                    _lookedUp = true;
                    _current = UnityEditor.AssetDatabase.LoadAssetAtPath<SoundSettings>(AssetPath);
                }
#endif
                return _current;
            }
        }

#if UNITY_EDITOR
        private static bool _lookedUp;

        /// <summary>Ассет создан или переимпортирован — искать заново.</summary>
        public static void ResetLookup() => _lookedUp = false;

        /// <summary>Для редактора: заменить записи целиком (уже отсортированные).</summary>
        internal void SetEntries(SoundEntry[] value) => entries = value ?? Array.Empty<SoundEntry>();
#endif

        private void OnEnable()
        {
            if (_current == null) _current = this;
        }

        private void OnDisable()
        {
            if (_current == this) _current = null;
        }
    }
}
