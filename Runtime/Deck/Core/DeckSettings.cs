using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Deck
{
    /// <summary>Когда поднимать AppDeck.</summary>
    public enum DeckActivation
    {
        /// <summary>Не поднимать: ни окна, ни перехвата логов. Регистрация команд работает (это просто данные).</summary>
        Off = 0,

        /// <summary>В редакторе и development-сборках.</summary>
        DevelopmentBuilds = 1,

        Always = 2,
    }

    /// <summary>Размер окна.</summary>
    public enum DeckLayout
    {
        /// <summary>Шторка сверху на часть экрана: мир виден и кликабелен ниже. Высота тянется.</summary>
        Partial = 0,

        /// <summary>Весь экран.</summary>
        Full = 1,
    }

    /// <summary>Клавиша открытия. Свой список, чтобы не зависеть от системы ввода в сериализации.</summary>
    public enum DeckHotkey
    {
        None = 0,
        BackQuote = 1,
        F1 = 2, F2 = 3, F3 = 4, F4 = 5, F5 = 6, F6 = 7,
        F7 = 8, F8 = 9, F9 = 10, F10 = 11, F11 = 12, F12 = 13,
        Insert = 14,
        Home = 15,
        PageUp = 16,
        Backslash = 17,
    }

    /// <summary>Угол экрана.</summary>
    public enum DeckCorner
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3,
    }

    /// <summary>
    /// Настройки AppDeck. Ассет один, лежит по <see cref="AssetPath"/>; редактор создаёт его сам
    /// и кладёт в Preloaded Assets, поэтому в сборке он доступен без сцены и без Resources.
    /// </summary>
    public sealed class DeckSettings : ScriptableObject
    {
        public const string AssetPath = AppCorePaths.SettingsDir + "/AppDeckSettings.asset";

        /// <summary>Таблица стилей в пакете. Редактор проставляет ссылку сам.</summary>
        public const string StyleSheetPath = "Packages/com.exerussus.app-core/Runtime/Deck/UI/AppDeck.uss";

        // поля пишет сериализация (инспектор), в коде плеера — только чтение → глушим CS0649
#pragma warning disable CS0649
        [Header("Запуск")]
        [Tooltip("Когда поднимать AppDeck. Off — ни окна, ни перехвата логов; регистрация команд всё равно работает.")]
        [SerializeField] private DeckActivation activation = DeckActivation.DevelopmentBuilds;

        [Tooltip("Клавиша открытия/закрытия. Shift + клавиша — переключить размер окна.")]
        [SerializeField] private DeckHotkey toggleKey = DeckHotkey.BackQuote;

        [Tooltip("Открыть сразу на старте.")]
        [SerializeField] private bool startOpen;

        [Header("Панель")]
        [Tooltip("Источник настроек панели (тема, шрифты, масштаб). Проставляется из Assets/App/UIToolkit/App Settings.asset. " +
                 "На старте копируется, копии ставится свой порядок отрисовки.")]
        [SerializeField] private PanelSettings panelSettings;

        [Tooltip("Таблица стилей AppDeck. Проставляется редактором из пакета.")]
        [SerializeField] private StyleSheet styleSheet;

        [Tooltip("Порядок отрисовки панели. Больше, чем у любой другой панели, — AppDeck поверх всего, включая экраны загрузки.")]
        [SerializeField] private float sortingOrder = 30000f;

        [Header("Окно")]
        [SerializeField] private DeckLayout defaultLayout = DeckLayout.Partial;

        [Tooltip("Высота шторки в режиме Partial, доля экрана. Дальше её тянут мышью, значение запоминается.")]
        [SerializeField, Range(0.15f, 0.95f)] private float partialHeight = 0.45f;

        [Tooltip("Пока окно открыто, курсор свободен. После закрытия возвращается то, что просила игра.")]
        [SerializeField] private bool unlockCursor = true;

        [Header("Консоль")]
        [Tooltip("Сколько записей держит консоль. Округляется до степени двойки.")]
        [SerializeField] private int logCapacity = 4096;

        [Tooltip("Сворачивать подряд идущие одинаковые записи в одну со счётчиком.")]
        [SerializeField] private bool collapseDuplicates = true;

        [Tooltip("Сколько введённых команд помнить (↑/↓).")]
        [SerializeField, Range(8, 512)] private int historySize = 64;

        [Header("Выбор цели")]
        [Tooltip("Клик по миру при открытом окне (Partial) выбирает цель для команд.")]
        [SerializeField] private bool picking = true;

        [Tooltip("Встроенный пикер: луч из главной камеры по физике. Игра может добавить свой с большим приоритетом.")]
        [SerializeField] private bool physicsPicker = true;

        [SerializeField] private float pickDistance = 1000f;
        [SerializeField] private LayerMask pickLayers = ~0;

        [Header("Мини-HUD")]
        [Tooltip("Закреплённые метрики видны в углу, даже когда окно закрыто.")]
        [SerializeField] private bool hud = true;

        [SerializeField] private DeckCorner hudCorner = DeckCorner.TopLeft;

        [Tooltip("Сколько раз в секунду обновлять мини-HUD.")]
        [SerializeField, Range(1, 30)] private int hudRate = 4;

        [Header("Метрики")]
        [Tooltip("Сколько раз в секунду опрашивать метрики, пока вкладка открыта.")]
        [SerializeField, Range(1, 60)] private int metricsRate = 8;

        [Tooltip("Длина истории треков (точек на спарклайн).")]
        [SerializeField, Range(16, 1024)] private int trackLength = 120;

#pragma warning restore CS0649

        public DeckActivation Activation => activation;
        public DeckHotkey ToggleKey => toggleKey;
        public bool StartOpen => startOpen;
        public PanelSettings PanelSettings => panelSettings;
        public StyleSheet StyleSheet => styleSheet;
        public float SortingOrder => sortingOrder;
        public DeckLayout DefaultLayout => defaultLayout;
        public float PartialHeight => partialHeight;
        public bool UnlockCursor => unlockCursor;
        public int LogCapacity => logCapacity;
        public bool CollapseDuplicates => collapseDuplicates;
        public int HistorySize => historySize;
        public bool Picking => picking;
        public bool PhysicsPicker => physicsPicker;
        public float PickDistance => pickDistance;
        public int PickLayers => pickLayers;
        public bool Hud => hud;
        public DeckCorner HudCorner => hudCorner;
        public int HudRate => hudRate;
        public int MetricsRate => metricsRate;
        public int TrackLength => trackLength;

        /// <summary>Поднимать ли AppDeck в этой сборке.</summary>
        public bool ShouldRun => activation == DeckActivation.Always ||
                                 (activation == DeckActivation.DevelopmentBuilds && Debug.isDebugBuild);

        private static DeckSettings _current;

        /// <summary>
        /// Настройки текущего запуска. В сборке приходят через Preloaded Assets (ассет регистрирует себя
        /// в OnEnable), в редакторе — читаются по фиксированному пути. null — ассета нет, работают умолчания.
        /// </summary>
        public static DeckSettings Current
        {
            get
            {
#if UNITY_EDITOR
                if (_current == null) _current = UnityEditor.AssetDatabase.LoadAssetAtPath<DeckSettings>(AssetPath);
#endif
                return _current;
            }
        }

        private void OnEnable()
        {
            // в сборке ассет поднимается из Preloaded Assets раньше первой сцены
            if (_current == null) _current = this;
        }

        private void OnDisable()
        {
            if (_current == this) _current = null;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // ссылки на ассеты AppCore проставляются сами, руками не назначаются
            if (panelSettings == null)
                panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(AppCorePaths.PanelSettingsPath);

            if (styleSheet == null)
                styleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);

            logCapacity = Mathf.Clamp(logCapacity, 64, 1 << 20);
        }
#endif
    }
}
