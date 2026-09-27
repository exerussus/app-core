using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Применение <see cref="AppTextSettings"/>: сбор набора символов, запекание в атласы,
    /// настройки текста панели и отчёт о покрытии по всей цепочке шрифтов.
    /// </summary>
    public static class AppTextTools
    {
        public sealed class FontResult
        {
            public string FontName;
            public int Requested;
            public int MissingInSource;
            public string Error;
        }

        public sealed class Report
        {
            public int Required;
            public int FilesScanned;
            public int OutsideSets;
            public readonly List<FontResult> Fonts = new();

            /// <summary>Символы, которых нет ни в одном шрифте цепочки.</summary>
            public readonly List<uint> Uncovered = new();

            /// <summary>Символы, которых нет в основном шрифте, но они закрыты запасным.</summary>
            public readonly List<uint> CoveredByFallback = new();

            /// <summary>Где в исходниках встретился непокрытый символ (первое вхождение).</summary>
            public readonly Dictionary<uint, string> Locations = new();

            public string Error;

            public bool Ok => string.IsNullOrEmpty(Error) && Uncovered.Count == 0;
        }

        // ============================================================ набор символов

        /// <summary>Разворачивает базовые наборы, доп. символы и диапазоны. Без сканирования.</summary>
        public static HashSet<uint> BuildDeclaredSet(AppTextSettings s)
        {
            var result = new HashSet<uint>();
            if (s == null) return result;

            if (s.basicLatin) AddRange(result, 0x0020, 0x007E);
            if (s.latinSupplement) AddRange(result, 0x00A0, 0x00FF);
            if (s.cyrillic) AddRange(result, 0x0400, 0x04FF);
            if (s.generalPunctuation) AddRange(result, 0x2010, 0x203A);
            if (s.arrows) AddRange(result, 0x2190, 0x2199);

            if (!string.IsNullOrEmpty(s.extraCharacters)) AddText(result, s.extraCharacters);

            foreach (var range in s.customRanges)
            {
                if (range == null) continue;
                if (!TryParseHex(range.from, out var from) || !TryParseHex(range.to, out var to)) continue;
                AddRange(result, Math.Min(from, to), Math.Max(from, to));
            }

            return result;
        }

        /// <summary>Полный набор: объявленный плюс всё не-ASCII из исходников.</summary>
        public static HashSet<uint> BuildRequiredSet(AppTextSettings s, out int filesScanned, out int outsideSets,
                                                    Dictionary<uint, string> locations = null)
        {
            var declared = BuildDeclaredSet(s);
            var result = new HashSet<uint>(declared);
            filesScanned = 0;
            outsideSets = 0;

            if (s != null && s.scanSources)
            {
                var scanned = new HashSet<uint>();
                ScanSources(scanned, s.scanFolders, s.scanExtensions, out filesScanned, locations);

                foreach (var code in scanned)
                {
                    // Управляющие символы и переводы строк атласу не нужны.
                    if (code < 0x20 || (code >= 0x7F && code < 0xA0)) continue;
                    if (result.Add(code)) outsideSets++;
                }
            }

            return result;
        }

        private static readonly Regex EscapeRx = new(@"\\u([0-9a-fA-F]{4})|\\U([0-9a-fA-F]{8})", RegexOptions.Compiled);

        /// <summary>
        /// Все не-ASCII символы файлов целиком, а не только строковые литералы: разбор литералов
        /// со всеми экранированиями врёт чаще, чем помогает, а лишний глиф стоит меньше килобайта.
        /// </summary>
        public static void ScanSources(HashSet<uint> target, IEnumerable<string> folders, IEnumerable<string> extensions,
                                       out int filesScanned, Dictionary<uint, string> locations = null)
        {
            filesScanned = 0;

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in extensions ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(ext)) continue;
                allowed.Add(ext.StartsWith(".") ? ext : "." + ext);
            }

            if (allowed.Count == 0) return;

            foreach (var folder in folders ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

                foreach (var path in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    if (!allowed.Contains(Path.GetExtension(path))) continue;

                    filesScanned++;
                    var text = File.ReadAllText(path);
                    var before = locations != null ? new HashSet<uint>(target) : null;

                    AddText(target, text, asciiToo: false);

                    foreach (Match match in EscapeRx.Matches(text))
                    {
                        var digits = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                        if (TryParseHex(digits, out var code)) target.Add(code);
                    }

                    if (locations == null) continue;
                    foreach (var code in target)
                        if (!before.Contains(code) && !locations.ContainsKey(code))
                            locations[code] = path.Replace('\\', '/');
                }
            }
        }

        // ============================================================ применение

        /// <summary>
        /// Полное применение: настройки текста панели, цепочка запасных, запекание, отчёт.
        /// </summary>
        public static Report Apply(AppTextSettings s)
        {
            var report = new Report();
            if (s == null) { report.Error = "Нет TextSettings."; return report; }
            if (s.primaryFont == null) { report.Error = "Не задан основной шрифт."; return report; }

            var required = BuildRequiredSet(s, out report.FilesScanned, out report.OutsideSets, report.Locations);
            report.Required = required.Count;

            var chain = BuildChain(s);
            AssignPanelTextSettings(s, chain);

            foreach (var (font, set) in chain)
                report.Fonts.Add(Bake(font, set ?? required));

            FillCoverage(report, chain, required);

            AssetDatabase.SaveAssets();
            return report;
        }

        /// <summary>Отчёт без изменений: что уже лежит в атласах.</summary>
        public static Report Analyze(AppTextSettings s)
        {
            var report = new Report();
            if (s == null) { report.Error = "Нет TextSettings."; return report; }
            if (s.primaryFont == null) { report.Error = "Не задан основной шрифт."; return report; }

            var required = BuildRequiredSet(s, out report.FilesScanned, out report.OutsideSets, report.Locations);
            report.Required = required.Count;

            FillCoverage(report, BuildChain(s), required);
            return report;
        }

        /// <summary>Цепочка (шрифт, набор). Набор <c>null</c> — весь набор проекта.</summary>
        private static List<(FontAsset font, HashSet<uint> set)> BuildChain(AppTextSettings s)
        {
            var chain = new List<(FontAsset, HashSet<uint>)> { (s.primaryFont, null) };

            foreach (var fallback in s.fallbacks)
            {
                if (fallback?.font == null || fallback.font == s.primaryFont) continue;

                HashSet<uint> set = null;
                if (fallback.coverage == FontCoverage.OnlyListed)
                {
                    set = new HashSet<uint>();
                    AddText(set, fallback.characters ?? string.Empty);
                    set.Add(' ');
                }

                chain.Add((fallback.font, set));
            }

            return chain;
        }

        private static void FillCoverage(Report report, List<(FontAsset font, HashSet<uint> set)> chain, HashSet<uint> required)
        {
            foreach (var code in required.OrderBy(c => c))
            {
                if (code == ' ' || code == 0xA0) continue;   // пробелы рисуются без глифа

                var inPrimary = Has(chain[0].font, code);
                if (inPrimary) continue;

                var inFallback = false;
                for (var i = 1; i < chain.Count && !inFallback; i++)
                    inFallback = (chain[i].set == null || chain[i].set.Contains(code)) && Has(chain[i].font, code);

                if (inFallback) report.CoveredByFallback.Add(code);
                else report.Uncovered.Add(code);
            }
        }

        // Есть ли глиф в шрифте (в атласе или в исходном файле динамического шрифта).
        private static bool Has(FontAsset font, uint code)
            => font != null && font.TryGetGlyphIndex(code, out _);

        private static bool IsDynamic(FontAsset font)
            => font.atlasPopulationMode is AtlasPopulationMode.Dynamic or AtlasPopulationMode.DynamicOS;

        /// <summary>Запекает набор в атлас одного шрифта.</summary>
        public static FontResult Bake(FontAsset font, HashSet<uint> characters)
        {
            var result = new FontResult { FontName = font != null ? font.name : "—", Requested = characters.Count };
            if (font == null) { result.Error = "шрифт не задан"; return result; }

            if (!IsDynamic(font))
            {
                result.Error = "атлас не динамический — допечь в него нечего. Переключите режим на Dynamic.";
                return result;
            }

            // Весь набор разом: шрифт раскладывает глифы одной упаковкой, а не дырявит атлас по символу.
            font.TryAddCharacters(characters.ToArray(), out var missing, true);
            result.MissingInSource = missing?.Length ?? 0;

            DisableClearOnBuild(font);
            AttachNewAtlasTextures(font);
            EditorUtility.SetDirty(font);
            return result;
        }

        // Иначе сборка выкинет всё, что только что запекли. Публичного сеттера у флага нет.
        private static void DisableClearOnBuild(FontAsset font)
        {
            var so = new SerializedObject(font);
            var property = so.FindProperty("m_ClearDynamicDataOnBuild");
            if (property == null || !property.boolValue) return;

            property.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Когда символы не влезли в один лист, шрифт создаёт новый, но в ассет его не кладёт.
        private static void AttachNewAtlasTextures(FontAsset font)
        {
            var path = AssetDatabase.GetAssetPath(font);
            if (string.IsNullOrEmpty(path) || font.atlasTextures == null) return;

            foreach (var texture in font.atlasTextures)
            {
                if (texture == null || !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture))) continue;
                texture.name = font.name + " Atlas";
                AssetDatabase.AddObjectToAsset(texture, font);
            }
        }

        /// <summary>
        /// Настройки текста панели: основной шрифт, цепочка запасных, «?» на месте дыры.
        /// Прописываются в PanelSettings приложения.
        /// </summary>
        private static void AssignPanelTextSettings(AppTextSettings s, List<(FontAsset font, HashSet<uint> set)> chain)
        {
            var textSettings = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(AppCorePaths.PanelTextSettingsPath);
            if (textSettings == null)
            {
                AppCoreAssets.EnsureFolder(Path.GetDirectoryName(AppCorePaths.PanelTextSettingsPath));
                textSettings = ScriptableObject.CreateInstance<PanelTextSettings>();
                AssetDatabase.CreateAsset(textSettings, AppCorePaths.PanelTextSettingsPath);
            }

            // Поля наследуются от TextSettings и публичных сеттеров не имеют.
            var so = new SerializedObject(textSettings);

            var defaultFont = so.FindProperty("m_DefaultFontAsset");
            if (defaultFont != null) defaultFont.objectReferenceValue = s.primaryFont;

            var list = so.FindProperty("m_FallbackFontAssets");
            if (list != null)
            {
                list.ClearArray();
                for (var i = 1; i < chain.Count; i++)
                {
                    list.InsertArrayElementAtIndex(list.arraySize);
                    list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = chain[i].font;
                }
            }

            // Знак вопроса вместо пустоты: дыра в шрифте должна быть видна, а не тиха.
            var missing = so.FindProperty("m_MissingCharacterUnicode");
            if (missing != null) missing.intValue = 0x003F;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(textSettings);

            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(AppCorePaths.PanelSettingsPath);
            if (panel != null && panel.textSettings != textSettings)
            {
                panel.textSettings = textSettings;
                EditorUtility.SetDirty(panel);
            }
        }

        // ============================================================ утилиты

        public static string Describe(Report report)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(report.Error)) return report.Error;

            sb.Append($"Символов: {report.Required}");
            if (report.FilesScanned > 0) sb.Append($" · файлов: {report.FilesScanned} · вне наборов: {report.OutsideSets}");
            sb.AppendLine();

            foreach (var font in report.Fonts)
                sb.AppendLine(font.Error != null ? $"«{font.FontName}»: {font.Error}" : $"«{font.FontName}»: запрошено {font.Requested}, нет в файле шрифта {font.MissingInSource}");

            if (report.Uncovered.Count > 0)
            {
                sb.AppendLine($"Не покрыто ни одним шрифтом: {report.Uncovered.Count}");
                foreach (var code in report.Uncovered)
                    sb.AppendLine($"  U+{code:X4} {Printable(code)}" + (report.Locations.TryGetValue(code, out var at) ? $" · {at}" : ""));
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>Печатаем и код, и сам символ: по одному коду не всегда понятно, чего не хватает.</summary>
        public static string Printable(uint code)
        {
            if (code is >= 0xD800 and <= 0xDFFF || code > 0x10FFFF) return "?";
            return char.ConvertFromUtf32((int)code);
        }

        /// <summary>Добавляет символы строки с учётом суррогатных пар: эмодзи занимают два char.</summary>
        public static void AddText(HashSet<uint> target, string text, bool asciiToo = true)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var high = text[i];

                // Одинокий суррогат — битый файл, а не символ: ConvertToUtf32 на нём бросает.
                if (char.IsHighSurrogate(high))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        target.Add((uint)char.ConvertToUtf32(high, text[i + 1]));
                        i++;
                    }
                    continue;
                }

                if (char.IsLowSurrogate(high)) continue;
                if (!asciiToo && high < 0x80) continue;

                target.Add(high);
            }
        }

        private static void AddRange(HashSet<uint> target, uint from, uint to)
        {
            for (var code = from; code <= to; code++) target.Add(code);
        }

        public static bool TryParseHex(string value, out uint code)
        {
            code = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var text = value.Trim();
            if (text.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
            else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);

            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
        }
    }

    /// <summary>
    /// Проверка текста перед билдом: пересканировать исходники, допечь новые символы и
    /// остановить сборку, если символ не покрыт ни одним шрифтом.
    /// </summary>
    internal sealed class AppTextBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => -900;

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = AppCoreAssets.FindTextSettings();
            if (settings == null || !settings.checkBeforeBuild || settings.primaryFont == null) return;

            var result = AppTextTools.Apply(settings);
            Debug.Log("[AppCore] Текст перед билдом:\n" + AppTextTools.Describe(result));

            if (!result.Ok)
                throw new BuildFailedException("[AppCore] Текст: есть символы вне всех шрифтов.\n" + AppTextTools.Describe(result));
        }
    }
}
