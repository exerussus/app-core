using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace Exerussus.AppCore.Editor
{
    /// <summary>Какие символы запекать в запасной шрифт.</summary>
    public enum FontCoverage
    {
        /// <summary>Весь набор проекта.</summary>
        All = 0,

        /// <summary>Только перечисленные символы (например, масти в отдельном шрифте).</summary>
        OnlyListed = 1,
    }

    /// <summary>
    /// Текст проекта: какие символы обязаны быть в атласах и из каких шрифтов их брать.
    /// </summary>
    /// <remarks>
    /// <para>
    /// В билде нет системных шрифтов: всё, чего нет в атласе и в цепочке запасных, просто не
    /// нарисуется. В редакторе дыру не видно — недостающий символ молча берётся из шрифта ОС.
    /// </para>
    /// <para>
    /// Ассет один и лежит по <see cref="AppCorePaths.TextSettingsPath"/>. Редактируется на вкладке
    /// Text страницы App в Nexus; применяет <see cref="AppTextTools"/>.
    /// </para>
    /// </remarks>
    public sealed class AppTextSettings : ScriptableObject
    {
        [Serializable]
        public sealed class UnicodeRange
        {
            [Tooltip("Подпись диапазона. Идёт только в отчёт.")]
            public string name = "Диапазон";

            [Tooltip("Первый код в шестнадцатеричном виде, например 0400.")]
            public string from = "0400";

            [Tooltip("Последний код включительно, например 04FF.")]
            public string to = "04FF";
        }

        [Serializable]
        public sealed class FallbackFont
        {
            public FontAsset font;
            public FontCoverage coverage = FontCoverage.All;

            [Tooltip("Символы подряд, без разделителей. Используются при «Только эти».")]
            public string characters = "";
        }

        [Header("Базовые наборы")]
        public bool basicLatin = true;
        public bool latinSupplement = true;
        public bool cyrillic = true;
        public bool generalPunctuation = true;
        public bool arrows;

        [Header("Дополнительно")]
        [Tooltip("Отдельные символы подряд, без разделителей.")]
        public string extraCharacters = "№₽•·";

        public List<UnicodeRange> customRanges = new();

        [Header("Сканирование исходников")]
        public bool scanSources = true;
        public List<string> scanFolders = new() { AppCorePaths.Root };
        public List<string> scanExtensions = new() { ".cs", ".uxml", ".uss" };

        [Header("Шрифты")]
        public FontAsset primaryFont;
        public List<FallbackFont> fallbacks = new();

        [Header("Сборка")]
        [Tooltip("Перед билдом пересканировать исходники, допечь новые символы и остановить сборку, если символ не покрыт ни одним шрифтом.")]
        public bool checkBeforeBuild = true;
    }
}
