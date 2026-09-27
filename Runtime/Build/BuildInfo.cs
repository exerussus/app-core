using System;
using System.Text;
using UnityEngine;

namespace Exerussus.AppCore.Build
{
    /// <summary>Когда показывать строку версии поверх приложения.</summary>
    public enum VersionOverlayMode
    {
        Off = 0,
        DevelopmentBuilds = 1,
        Always = 2,
    }

    /// <summary>Угол экрана для строки версии.</summary>
    public enum VersionOverlayCorner
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3,
    }

    /// <summary>
    /// Информация о сборке: версия, хэш коммита, профиль, платформа, время — плюс настройки
    /// строки версии, которую <see cref="AppRunner"/> рисует поверх приложения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ассет один и лежит строго по <see cref="AppCorePaths.BuildInfoPath"/>; <c>AppRunner</c>
    /// получает ссылку на него автоматически.
    /// </para>
    /// <para>
    /// В закоммиченном ассете поля значений ПУСТЫЕ, режим — <see cref="EditorMode"/>.
    /// Перед билдом редакторная часть AppCore штампует значения, после билда (в т.ч. упавшего)
    /// откатывает. В Play Mode редактора версия и хэш подставляются на лету и в ассет не пишутся.
    /// Настройки оверлея — обычные поля, штамп их не трогает.
    /// </para>
    /// </remarks>
    public sealed class BuildInfo : ScriptableObject
    {
        /// <summary>Режим закоммиченного ассета: значения пустые.</summary>
        public const string EditorMode = "editor-play-mode";

        /// <summary>Режим проштампованного ассета (только на время билда и в самом билде).</summary>
        public const string BuildMode = "build";

        /// <summary>Формат строки версии по умолчанию.</summary>
        public const string DefaultOverlayFormat = "{version} · {hash}{dirty} · {profile}";

        // Поля значений пишет только редакторный штамп (в плеере — лишь чтение) → глушим CS0649.
#pragma warning disable CS0649
        [SerializeField, HideInInspector] private string mode = EditorMode;
        [SerializeField, HideInInspector] private string version = "";
        [SerializeField, HideInInspector] private string commitHash = "";
        [SerializeField, HideInInspector] private bool isDirty;
        [SerializeField, HideInInspector] private string prefix = "";
        [SerializeField, HideInInspector] private string postfix = "";
        [SerializeField, HideInInspector] private string profile = "";
        [SerializeField, HideInInspector] private string platform = "";
        [SerializeField, HideInInspector] private string buildTimeUtc = "";
#pragma warning restore CS0649

        [Header("Строка версии")]
        [SerializeField] private VersionOverlayMode overlayMode = VersionOverlayMode.DevelopmentBuilds;
        [SerializeField] private VersionOverlayCorner overlayCorner = VersionOverlayCorner.BottomRight;
        [SerializeField, Range(0.1f, 1f)] private float overlayOpacity = 0.45f;
        [SerializeField, Range(8, 32)] private int overlayFontSize = 11;
        [SerializeField] private string overlayFormat = DefaultOverlayFormat;
        [SerializeField] private bool logOnStart = true;

        private static BuildInfo _current;

        /// <summary>
        /// Ассет информации о сборке. Проставляется <see cref="AppRunner"/> на старте;
        /// до этого — <c>null</c>.
        /// </summary>
        public static BuildInfo Current => _current;

        /// <summary>Полная строка версии текущей сборки; без ассета — <c>Application.version</c>.</summary>
        public static string CurrentFullVersion => _current != null ? _current.FullVersion : Application.version;

        /// <summary>Делает ассет текущим. Вызывает <see cref="AppRunner"/>.</summary>
        internal static void SetCurrent(BuildInfo info) => _current = info;

        /// <summary>Режим ассета: <see cref="EditorMode"/> или <see cref="BuildMode"/>.</summary>
        public string Mode => mode;

        /// <summary>true — значения проставлены штампом билда.</summary>
        public bool IsStamped => mode == BuildMode;

        /// <summary>Версия игры (<c>PlayerSettings.bundleVersion</c> на момент билда).</summary>
        public string Version
        {
            get
            {
#if UNITY_EDITOR
                if (TryEditor(out var e)) return e.Version ?? "";
#endif
                return string.IsNullOrEmpty(version) ? Application.version : version;
            }
        }

        /// <summary>Укороченный хэш коммита; пусто, если git не найден.</summary>
        public string CommitHash
        {
            get
            {
#if UNITY_EDITOR
                if (TryEditor(out var e)) return e.CommitHash ?? "";
#endif
                return commitHash ?? "";
            }
        }

        /// <summary>В рабочем дереве были незакоммиченные изменения.</summary>
        public bool IsDirty
        {
            get
            {
#if UNITY_EDITOR
                if (TryEditor(out var e)) return e.IsDirty;
#endif
                return isDirty;
            }
        }

        public string Prefix => prefix ?? "";
        public string Postfix => postfix ?? "";

        public string Profile
        {
            get
            {
#if UNITY_EDITOR
                if (TryEditor(out var e)) return e.Profile ?? "";
#endif
                return profile ?? "";
            }
        }

        public string Platform => string.IsNullOrEmpty(platform) ? Application.platform.ToString() : platform;
        public string BuildTimeUtc => buildTimeUtc ?? "";

        /// <summary>Версия с префиксом/постфиксом, без хэша: «beta-1.2.3-rc».</summary>
        public string DisplayVersion => Prefix + Version + Postfix;

        /// <summary>Полная версия: «beta-1.2.3-rc+a1b2c3d» (и «-dirty», если дерево было грязным).</summary>
        public string FullVersion
        {
            get
            {
                var v = DisplayVersion;
                var h = CommitHash;
                if (!string.IsNullOrEmpty(h)) v += "+" + h + (IsDirty ? "-dirty" : "");
                return v;
            }
        }

        public VersionOverlayMode OverlayMode => overlayMode;
        public VersionOverlayCorner OverlayCorner => overlayCorner;
        public float OverlayOpacity => overlayOpacity;
        public int OverlayFontSize => overlayFontSize;
        public string OverlayFormat => string.IsNullOrEmpty(overlayFormat) ? DefaultOverlayFormat : overlayFormat;
        public bool LogOnStart => logOnStart;

        /// <summary>Нужно ли показывать строку версии в текущем запуске.</summary>
        public bool ShouldShowOverlay => overlayMode switch
        {
            VersionOverlayMode.Always => true,
            VersionOverlayMode.DevelopmentBuilds => Debug.isDebugBuild,
            _ => false,
        };

        /// <summary>
        /// Строка по шаблону. Токены: <c>{version}</c> <c>{hash}</c> <c>{dirty}</c> (звёздочка
        /// при грязном дереве) <c>{profile}</c> <c>{platform}</c> <c>{date}</c>. Разделители вокруг
        /// пустых значений схлопываются.
        /// </summary>
        public string Format(string format)
        {
            if (string.IsNullOrEmpty(format)) format = DefaultOverlayFormat;

            var date = BuildTimeUtc;
            if (date.Length >= 10) date = date.Substring(0, 10);

            var text = new StringBuilder(format)
                .Replace("{version}", DisplayVersion)
                .Replace("{hash}", CommitHash)
                .Replace("{dirty}", IsDirty ? "*" : "")
                .Replace("{profile}", Profile)
                .Replace("{platform}", Platform)
                .Replace("{date}", date)
                .ToString();

            return CollapseSeparators(text);
        }

        /// <summary>Строка оверлея по настройкам ассета.</summary>
        public string FormatOverlay() => Format(OverlayFormat);

        public override string ToString() => FullVersion;

        // «0.4.2 ·  · webgl» → «0.4.2 · webgl»: пустой токен не должен оставлять двойной разделитель.
        private static string CollapseSeparators(string text)
        {
            var parts = text.Split('·');
            var sb = new StringBuilder();

            foreach (var raw in parts)
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(part);
            }

            return sb.ToString();
        }

        // Сброс статики при входе в Play Mode с выключенной перезагрузкой домена.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _current = null;

#if UNITY_EDITOR
        /// <summary>Значения для Play Mode редактора (в ассет не пишутся).</summary>
        public struct EditorValues
        {
            public string Version;
            public string CommitHash;
            public bool IsDirty;
            public string Profile;
        }

        /// <summary>Источник значений Play Mode; выставляется редакторной частью AppCore.</summary>
        public static Func<EditorValues?> EditorProvider;

        private bool TryEditor(out EditorValues values)
        {
            values = default;
            if (IsStamped || !Application.isPlaying || EditorProvider == null) return false;
            var v = EditorProvider();
            if (!v.HasValue) return false;
            values = v.Value;
            return true;
        }

        /// <summary>Штамп перед билдом (вызывает только редакторная часть AppCore).</summary>
        public void EditorStamp(string version, string commitHash, bool isDirty, string prefix, string postfix,
                                string profile, string platform, string buildTimeUtc)
        {
            mode = BuildMode;
            this.version = version ?? "";
            this.commitHash = commitHash ?? "";
            this.isDirty = isDirty;
            this.prefix = prefix ?? "";
            this.postfix = postfix ?? "";
            this.profile = profile ?? "";
            this.platform = platform ?? "";
            this.buildTimeUtc = buildTimeUtc ?? "";
        }

        /// <summary>Откат к пустому «editor-play-mode».</summary>
        public void EditorReset()
        {
            mode = EditorMode;
            version = "";
            commitHash = "";
            isDirty = false;
            prefix = "";
            postfix = "";
            profile = "";
            platform = "";
            buildTimeUtc = "";
        }
#endif
    }
}
