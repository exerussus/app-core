using System;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Настройки аналитики и каталог событий (<c>Assets/App/Settings/AnalyticsSettings.asset</c>).
    /// Каталог отсортирован по ключу: номер записи — константа <c>AppEvents.….Id</c> (генерируется).
    /// В сборку попадает через Preloaded Assets, в редакторе читается по фиксированному пути.
    /// </summary>
    [CreateAssetMenu(menuName = "Exerussus/App/Analytics Settings", fileName = "AnalyticsSettings")]
    public sealed class AnalyticsSettings : ScriptableObject
    {
        public const string AssetPath = AppCorePaths.SettingsDir + "/AnalyticsSettings.asset";
        public const string KeysPath = AppCorePaths.GeneratedDir + "/AppEvents.cs";
        public const int MaxDepth = 5;

#pragma warning disable CS0649
        [Header("Сбор")]
        [Tooltip("Модуль аналитики включён. Выключено — ничего не создаётся, Raise выходит сразу. По умолчанию выключен: включение — решение проекта.")]
        [SerializeField] private bool enabled;

        [Tooltip("Сбор включён по умолчанию для нового игрока (дальше — его выбор, хранится в PlayerPrefs).")]
        [SerializeField] private bool collectByDefault;

        [Tooltip("Выключать в batch mode (сервер, CI).")]
        [SerializeField] private bool disableInBatchMode = true;

        [Tooltip("Выключение сбора стирает ещё не доставленные пакеты (отказ от согласия). Иначе они дошлются.")]
        [SerializeField] private bool discardQueueOnDisable;

        [Header("Пакеты")]
        [Tooltip("Событий в пакете, после которых он закрывается.")]
        [Min(1)] [SerializeField] private int maxEventsPerPacket = 200;

        [Tooltip("Секунд жизни открытого пакета, после которых он закрывается.")]
        [Min(1f)] [SerializeField] private float maxPacketAge = 30f;

        [Tooltip("Размер пакета, КБ памяти (UTF-16). Буфер выделяется один раз при старте и не растёт; " +
                 "пакет закрывается, когда заполнен.")]
        [Min(4)] [SerializeField] private int maxPacketKilobytes = 64;

        [Header("Лёгкий режим")]
        [Tooltip("Где работать в лёгком режиме: без диска и экспорта в файлы, маленькая очередь в памяти, " +
                 "без адаптера события не пишутся вовсе. WebGL — всегда лёгкий (файлы там живут в памяти страницы); " +
                 "настройка решает для остальных платформ.")]
        [SerializeField] private AnalyticsLeanMode leanMode = AnalyticsLeanMode.WebGL;

        [Tooltip("Лёгкий режим: максимум закрытых пакетов в памяти. Сверх — выбрасываются самые старые.")]
        [Min(1)] [SerializeField] private int leanMaxQueuedPackets = 4;

        [Tooltip("Лёгкий режим: максимум закрытых пакетов в памяти, КБ.")]
        [Min(16)] [SerializeField] private int leanMaxQueuedKilobytes = 256;

        [Header("Очередь (на диске)")]
        [Tooltip("Максимум недоставленных пакетов. Сверх — выбрасываются самые старые (с записью о потере).")]
        [Min(1)] [SerializeField] private int maxQueuedPackets = 500;

        [Tooltip("Максимум недоставленных данных, КБ.")]
        [Min(16)] [SerializeField] private int maxQueuedKilobytes = 8192;

        [Header("Доставка")]
        [Tooltip("Доставка без ответа адаптера дольше этого (с) — пакет снова в очереди.")]
        [Min(1f)] [SerializeField] private float deliveryLease = 60f;

        [Tooltip("Пауза перед первой повторной попыткой (с); дальше удваивается.")]
        [Min(0.1f)] [SerializeField] private float retryBase = 5f;

        [Tooltip("Потолок паузы между попытками (с).")]
        [Min(1f)] [SerializeField] private float retryMax = 300f;

        [Tooltip("Сколько пакетов одновременно у адаптера.")]
        [Range(1, 16)] [SerializeField] private int maxInFlight = 4;

        [Tooltip("Если проект не назначил свой адаптер — сохранять пакеты файлами в persistentDataPath/AppAnalytics/Export. " +
                 "Работает только в редакторе и development-сборках. Выключено — пакеты ждут в очереди, пока адаптер не назначат.")]
        [SerializeField] private bool exportToFilesByDefault = true;

        [SerializeField] private AnalyticsEventEntry[] entries = Array.Empty<AnalyticsEventEntry>();
#pragma warning restore CS0649

        // Жёсткие пределы. [Min]/[Range] работают только в инспекторе — правка YAML или мерж их обходят,
        // поэтому каждое значение зажимается здесь: никакая настройка не даёт переполнения, огромного буфера
        // или очереди без потолка.
        public const int MaxPacketKilobytesCeiling = 512;
        public const int MaxQueuedKilobytesCeiling = 64 * 1024;
        public const int LeanMaxQueuedKilobytesCeiling = 4 * 1024;
        public const int MaxQueuedPacketsCeiling = 10000;
        public const int LeanMaxQueuedPacketsCeiling = 64;

        public bool Enabled => enabled && !(disableInBatchMode && Application.isBatchMode);
        public bool CollectByDefault => collectByDefault;
        public bool DiscardQueueOnDisable => discardQueueOnDisable;
        public int MaxEventsPerPacket => Clamp(maxEventsPerPacket, 1, 100000);
        public float MaxPacketAge => Clamp(maxPacketAge, 1f, 3600f, 30f);
        public int MaxQueuedPackets => Clamp(maxQueuedPackets, 1, MaxQueuedPacketsCeiling);
        public long MaxQueuedBytes => Clamp(maxQueuedKilobytes, 16, MaxQueuedKilobytesCeiling) * 1024L;
        public float DeliveryLease => Clamp(deliveryLease, 1f, 3600f, 60f);
        public float RetryBase => Clamp(retryBase, 0.1f, 3600f, 5f);
        public float RetryMax => Clamp(retryMax, RetryBase, 86400f, 300f);
        public int MaxInFlight => Clamp(maxInFlight, 1, 16);
        /// <summary>Экспорт в файлы по умолчанию — только редактор и development-сборки: в релизе папка росла бы без конца.</summary>
        public bool ExportToFilesByDefault => exportToFilesByDefault && (Application.isEditor || Debug.isDebugBuild);
        /// <summary>Размер пакета в символах (UTF-16): 4–512 КБ памяти. На проводе (UTF-8) — до вдвое меньше для латиницы.</summary>
        public int MaxPacketChars => Clamp(maxPacketKilobytes, 4, MaxPacketKilobytesCeiling) * 512;
        public AnalyticsLeanMode LeanMode => leanMode;
        /// <summary>Лёгкая очередь: не меньше пакетов в полёте + 2 (иначе медленная сеть вытесняла бы каждый новый).</summary>
        public int LeanMaxQueuedPackets => Math.Min(LeanMaxQueuedPacketsCeiling, Math.Max(Clamp(leanMaxQueuedPackets, 1, LeanMaxQueuedPacketsCeiling), MaxInFlight + 2));
        /// <summary>Лёгкая очередь, байт: не меньше трёх пакетов, не больше 4 МБ.</summary>
        public long LeanMaxQueuedBytes => Math.Min(LeanMaxQueuedKilobytesCeiling * 1024L,
                                                   Math.Max(Clamp(leanMaxQueuedKilobytes, 16, LeanMaxQueuedKilobytesCeiling) * 1024L, 3L * MaxPacketChars * sizeof(char)));

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        private static float Clamp(float v, float min, float max, float fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            return v < min ? min : v > max ? max : v;
        }

        /// <summary>
        /// Лёгкий режим на этой платформе. WebGL — всегда: файлы там живут в памяти страницы (MEMFS без syncfs),
        /// полный режим с диском на WebGL не поддерживается.
        /// </summary>
        public bool IsLeanOn(RuntimePlatform platform) => platform == RuntimePlatform.WebGLPlayer || leanMode switch
        {
            AnalyticsLeanMode.Always => true,
            _ => false,
        };

        /// <summary>Записи каталога (отсортированы по ключу). Массив живой — не менять в рантайме.</summary>
        public AnalyticsEventEntry[] Entries => entries ??= Array.Empty<AnalyticsEventEntry>();

        /// <summary>
        /// Хэш схемы каталога: ключи и поля с типами. Пишется в заголовок пакета — по нему получатель
        /// понимает, по какому каталогу собраны данные.
        /// </summary>
        public string SchemaHash()
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong h = offset;

                void Mix(string s)
                {
                    if (s != null)
                        foreach (char c in s)
                        {
                            h ^= c;
                            h *= prime;
                        }
                    h ^= '|';
                    h *= prime;
                }

                foreach (AnalyticsEventEntry e in Entries)
                {
                    Mix(e.key);
                    if (e.fields == null) continue;
                    foreach (AnalyticsField f in e.fields)
                    {
                        Mix(f.name);
                        Mix(((int)f.type).ToString());
                    }
                }

                return h.ToString("x16");
            }
        }

        private static AnalyticsSettings _current;

        /// <summary>Настройки текущего запуска. <c>null</c> — ассета нет, аналитика выключена.</summary>
        public static AnalyticsSettings Current
        {
            get
            {
#if UNITY_EDITOR
                if (_current == null && !_lookedUp)
                {
                    _lookedUp = true;
                    _current = UnityEditor.AssetDatabase.LoadAssetAtPath<AnalyticsSettings>(AssetPath);
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
        internal void SetEntries(AnalyticsEventEntry[] value) => entries = value ?? Array.Empty<AnalyticsEventEntry>();
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

    /// <summary>Где аналитика работает в лёгком режиме.</summary>
    public enum AnalyticsLeanMode : byte
    {
        [InspectorName("Никогда")] Never = 0,
        [InspectorName("WebGL")] WebGL = 1,
        [InspectorName("Везде")] Always = 2,
    }
}
