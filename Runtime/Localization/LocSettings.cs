using System;
using System.Collections.Generic;
using UnityEngine;

namespace Exerussus.AppCore.Localization
{
    /// <summary>Кто кладёт пакеты локализации в игре.</summary>
    public enum LocPackageLoading
    {
        /// <summary>AppCore сам: пакеты каталога (маски или провайдер редактора) запекаются в настройки перед сборкой.</summary>
        Auto = 0,

        /// <summary>Проект: свой загрузчик отдаёт пакеты через <see cref="Loc.SetPackages"/> (порядок модов, паки из StreamingAssets).</summary>
        Project = 1,
    }

    /// <summary>Пакет, запечённый редактором для <see cref="LocPackageLoading.Auto"/>: ассет или файл в StreamingAssets.</summary>
    [Serializable]
    public sealed class LocPackageRef
    {
        public string id;

        [Tooltip("Таблица, импортированная как TextAsset (вне StreamingAssets).")]
        public TextAsset asset;

        [Tooltip("Путь внутри StreamingAssets — читается с диска в игре.")]
        public string streamingPath;
    }

    /// <summary>
    /// Шаблон употребления ключа без <c>^</c>: регулярное выражение с группой <c>key</c>
    /// (или первой группой) — <c>Loc\.Tr\(\s*"(?&lt;key&gt;[^"]+)"</c>.
    /// </summary>
    [Serializable]
    public sealed class LocUsagePattern
    {
        [Tooltip("Подпись в списке употреблений.")]
        public string name;

        [Tooltip("Регулярное выражение с группой (?<key>…) или первой группой.")]
        public string regex;

        [Tooltip("Расширения через ; (.cs;.sal). Пусто — .cs.")]
        public string extensions = ".cs";

        [Tooltip("Папки от корня проекта через ; (Assets/Game;Assets/StreamingAssets). Пусто — Assets.")]
        public string folders;

        [Tooltip("Чем заменять ключ при переименовании: ключ как есть (Key) или идентификатором (Identifier: точки и минусы → _).")]
        public LocUsageReplace replace = LocUsageReplace.Key;

        [Tooltip("Засчитывать только ключи, которые есть в таблицах: для шаблонов общего вида (любой строковый литерал " +
                 "с точкой) — иначе каждая такая строка попала бы в «нет в таблицах».")]
        public bool knownOnly;
    }

    /// <summary>Как шаблон пишет ключ в употреблении.</summary>
    public enum LocUsageReplace
    {
        /// <summary>Ключ как есть.</summary>
        Key = 0,

        /// <summary>Идентификатор C#: всё, кроме букв, цифр и _, заменено на _.</summary>
        Identifier = 1,

        /// <summary>Не переименовывать — только находить.</summary>
        None = 2,
    }
    /// <summary>Какой язык включить на первом запуске (без сохранённого выбора).</summary>
    public enum LocStartLanguage
    {
        /// <summary>Язык системы, если он есть в таблице, иначе основной.</summary>
        System = 0,

        /// <summary>Основной язык.</summary>
        Primary = 1,

        /// <summary>Заданный в настройках.</summary>
        Fixed = 2,
    }

    /// <summary>Что делать с разметкой (rich text) в строках модов.</summary>
    public enum LocModRichText
    {
        /// <summary>Пропускать как есть.</summary>
        Allow = 0,

        /// <summary>Пропускать только безопасные теги (b, i, u, s, color, size, sup, sub, mark, br, alpha, noparse), строку с другими — отклонять.</summary>
        Validate = 1,

        /// <summary>Вырезать любые теги.</summary>
        Strip = 2,
    }

    /// <summary>Когда писать историю ключей (кто какой патч что изменил).</summary>
    public enum LocTraceMode
    {
        Off = 0,

        /// <summary>Только в редакторе.</summary>
        Editor = 1,

        /// <summary>В редакторе и development-сборках.</summary>
        DevelopmentBuilds = 2,

        Always = 3,
    }

    /// <summary>Проверка ключей перед сборкой.</summary>
    public enum LocBuildCheck
    {
        Off = 0,

        /// <summary>Предупреждения в лог, сборка идёт.</summary>
        Warn = 1,

        /// <summary>Неизвестный ключ в коде или вёрстке останавливает сборку.</summary>
        Fail = 2,
    }

    /// <summary>
    /// Настройки локализации AppCore. Ассет один, лежит по <see cref="AssetPath"/>; редактор создаёт
    /// его сам и кладёт в Preloaded Assets.
    /// </summary>
    /// <remarks>
    /// Пока галочка <see cref="Enabled"/> снята, локализации нет вовсе: таблица не грузится,
    /// вёрстка не обходится, патчи не принимаются, <see cref="Loc.Get"/> возвращает строку как есть.
    /// </remarks>
    public sealed class LocSettings : ScriptableObject
    {
        public const string AssetPath = AppCorePaths.LocalizationSettingsPath;

        // поля пишет сериализация (инспектор), в коде плеера — только чтение → глушим CS0649
#pragma warning disable CS0649
        [Tooltip("Включить локализацию. Выключено — ключи '^' не резолвятся, вёрстка не обходится, патчи не принимаются.")]
        [SerializeField] private bool enabled;

        [Tooltip("Выключать локализацию в batch mode (выделенный сервер, CI): таблицы не грузятся, ключи остаются ключами. " +
                 "Из кода то же на один запуск — Loc.OverrideEnabled(false).")]
        [SerializeField] private bool disableInBatchMode;

        [Header("Таблица")]
        [Tooltip("Базовая таблица проекта (.loc). Обычно Assets/App/Localization/base.loc.")]
        [SerializeField] private TextAsset baseTable;

        [Tooltip("Основной язык: из него берётся непереведённое. Пусто — первый язык базовой таблицы.")]
        [SerializeField] private string primaryLanguage;

        [Tooltip("Что показывать вместо ключа, которого нет ни на одном языке.")]
        [SerializeField] private string missingText = "???";

        [Tooltip("Префиксы ключей, которые собираются в коде (PLANET_ + id). Проверки не считают такие ключи лишними.")]
        [SerializeField] private string[] dynamicPrefixes = new string[0];

        [Header("Пакеты")]
        [Tooltip("Где лежат пакеты локализации (.loc модулей), маски от корня проекта: Assets/**/Localization/*.loc, " +
                 "Assets/StreamingAssets/Core/*/phrases/*.loc. ** — любые папки, * — часть имени. Базовая таблица в пакеты не входит. " +
                 "Провайдер проекта (ILocPackageProvider в редакторской сборке) заменяет маски.")]
        [SerializeField] private string[] packageMasks = new string[0];

        [Tooltip("Маски, которые из пакетов исключаются.")]
        [SerializeField] private string[] packageExcludes = new string[0];

        [Tooltip("Кто кладёт пакеты в игре. Auto — AppCore сам (порядок — как в каталоге); Project — загрузчик проекта через Loc.SetPackages.")]
        [SerializeField] private LocPackageLoading packageLoading = LocPackageLoading.Auto;

        [Tooltip("Нестрогое чтение пакетов: строка с ошибкой отбрасывается, остальное принимается.")]
        [SerializeField] private bool lenientPackages = true;

        // Запекает редактор (импорт .loc, перед сборкой) для режима Auto.
        [SerializeField, HideInInspector] private List<LocPackageRef> packages = new();

        [Header("Употребления")]
        [Tooltip("Где искать «^KEY» и L.KEY: расширения файлов.")]
        [SerializeField] private string[] usageExtensions = { ".uxml", ".cs", ".prefab", ".asset", ".unity" };

        [Tooltip("Ключи без «^»: свои шаблоны вызовов (Loc.Tr(\"key\"), Say(\"key\"), константы).")]
        [SerializeField] private LocUsagePattern[] usagePatterns = new LocUsagePattern[0];

        [Tooltip("Части путей, которые поиск употреблений пропускает (/ThirdParty/, /Plugins/Vendor/).")]
        [SerializeField] private string[] usageExcludes = new string[0];

        [Header("Язык")]
        [SerializeField] private LocStartLanguage startLanguage = LocStartLanguage.System;

        [Tooltip("Язык для режима Fixed.")]
        [SerializeField] private string fixedLanguage;

        [Tooltip("Запоминать выбор игрока (PlayerPrefs).")]
        [SerializeField] private bool rememberChoice = true;

        [SerializeField] private string prefsKey = "appcore.language";

        [Header("Моды")]
        [Tooltip("Разрешить модам удалять ключи и значения (~).")]
        [SerializeField] private bool modRemovals;

        [SerializeField] private LocModRichText modRichText = LocModRichText.Validate;

        [Tooltip("Ключи, которые моды не трогают. Шаблон с * в конце — префикс: APPCORE_*.")]
        [SerializeField] private string[] protectedKeys = new string[0];

        [Header("Отладка")]
        [Tooltip("Писать историю ключей: какой слой что поставил, заменил, удалил, что отклонено и почему.")]
        [SerializeField] private LocTraceMode trace = LocTraceMode.Editor;

        [Tooltip("В редакторе перечитывать таблицы при изменении .loc-файлов.")]
        [SerializeField] private bool hotReload = true;

        [Tooltip("Предупреждать в лог о ключе, которого нет в таблице (раз на ключ).")]
        [SerializeField] private bool warnMissing = true;

        [Tooltip("Предупреждать о именованном токене без аргумента ({count} в Format без count). Обычно выключено: " +
                 "такие токены дорабатывают проходы и процессор проекта.")]
        [SerializeField] private bool warnUnknownTokens;

        [Header("Редактор")]
        [Tooltip("Генерировать константы ключей (L.MENU_PLAY) в Assets/App/Generated/LocKeys.cs.")]
        [SerializeField] private bool generateKeys = true;

        [SerializeField] private string keysNamespace = "App.Localization";

        [Tooltip("Проверка ключей перед сборкой.")]
        [SerializeField] private LocBuildCheck buildCheck = LocBuildCheck.Warn;
#pragma warning restore CS0649

        public bool Enabled => enabled;
        public TextAsset BaseTable => baseTable;
        public string PrimaryLanguage => primaryLanguage;
        public string MissingText => missingText;
        public string[] DynamicPrefixes => dynamicPrefixes ?? new string[0];
        public string[] PackageMasks => packageMasks ?? new string[0];
        public string[] PackageExcludes => packageExcludes ?? new string[0];
        public LocPackageLoading PackageLoading => packageLoading;
        public bool LenientPackages => lenientPackages;
        public IReadOnlyList<LocPackageRef> Packages => packages ??= new List<LocPackageRef>();
        public string[] UsageExtensions => usageExtensions ?? new string[0];
        public LocUsagePattern[] UsagePatterns => usagePatterns ?? new LocUsagePattern[0];
        public string[] UsageExcludes => usageExcludes ?? new string[0];
        public LocStartLanguage StartLanguage => startLanguage;
        public string FixedLanguage => fixedLanguage;
        public bool RememberChoice => rememberChoice;
        public string PrefsKey => string.IsNullOrEmpty(prefsKey) ? "appcore.language" : prefsKey;
        public bool ModRemovals => modRemovals;
        public LocModRichText ModRichText => modRichText;
        public string[] ProtectedKeys => protectedKeys ?? new string[0];
        public LocTraceMode Trace => trace;
        public bool HotReload => hotReload;
        public bool WarnMissing => warnMissing;
        public bool WarnUnknownTokens => warnUnknownTokens;
        public bool DisableInBatchMode => disableInBatchMode;
        public bool GenerateKeys => generateKeys;
        public string KeysNamespace => string.IsNullOrEmpty(keysNamespace) ? "App.Localization" : keysNamespace;
        public LocBuildCheck BuildCheck => buildCheck;

        /// <summary>Писать ли историю в этом запуске.</summary>
        public bool TraceActive => trace switch
        {
            LocTraceMode.Always => true,
            LocTraceMode.DevelopmentBuilds => Debug.isDebugBuild,
            LocTraceMode.Editor => Application.isEditor,
            _ => false,
        };

        private static LocSettings _current;

        /// <summary>
        /// Настройки текущего запуска. В сборке приходят через Preloaded Assets, в редакторе читаются
        /// по фиксированному пути. <c>null</c> — ассета нет, локализация выключена.
        /// </summary>
        public static LocSettings Current
        {
            get
            {
#if UNITY_EDITOR
                // Нет ассета — не ищем его на каждом обращении: Loc.Enabled зовут часто. Поиск
                // повторяется, когда редактор сообщит об импорте ассета (ResetLookup).
                if (_current == null && !_lookedUp)
                {
                    _lookedUp = true;
                    _current = UnityEditor.AssetDatabase.LoadAssetAtPath<LocSettings>(AssetPath);
                }
#endif
                return _current;
            }
        }

#if UNITY_EDITOR
        private static bool _lookedUp;

        /// <summary>Ассет создан или переимпортирован — искать заново.</summary>
        public static void ResetLookup() => _lookedUp = false;
#endif

        private void OnEnable()
        {
            if (_current == null) _current = this;
        }

        private void OnDisable()
        {
            if (_current == this) _current = null;
        }

#if UNITY_EDITOR
        /// <summary>Изменение в инспекторе во время игры — перечитать.</summary>
        private void OnValidate()
        {
            if (Application.isPlaying) Loc.SettingsChanged();
        }
#endif
    }
}
