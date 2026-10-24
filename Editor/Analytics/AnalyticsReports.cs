using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Exerussus.AppCore.Analytics.Reports;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Analytics.Editor
{
    /// <summary>Настройки сборки отчёта — на машину и проект (UserSettings, не в git).</summary>
    [Serializable]
    public sealed class AnalyticsReportPrefs
    {
        /// <summary>Брать папку экспорта редактора (persistentDataPath/AppAnalytics/Export).</summary>
        public bool includeExport = true;
        /// <summary>Дополнительные папки или файлы .jsonl (выгрузка с сервера, с устройств коллег).</summary>
        public List<string> folders = new List<string>();
        /// <summary>Куда писать отчёт и экспорт; пусто — persistentDataPath/AppAnalytics/Reports.</summary>
        public string outputDir = string.Empty;

        public bool anonymize;
        public bool csvSemicolon = true;
        public bool csvBom = true;
        public bool csvIsoTimes;

        /// <summary>Фильтр: даты yyyy-MM-dd (UTC), пусто — без границы.</summary>
        public string from = string.Empty;
        public string to = string.Empty;
        public List<string> builds = new List<string>();
        public List<string> platforms = new List<string>();
        public List<string> tags = new List<string>();
    }

    /// <summary>
    /// Отчёт аналитики из редактора: каталог из <see cref="AnalyticsSettings"/>, источники и фильтр из
    /// <see cref="AnalyticsReportPrefs"/>, сборка — <see cref="ReportBuilder"/> (без Unity), шаблон report.html — из пакета.
    /// Вызывают вкладка Nexus «Аналитика → Отчёт» и команды CLI. Окон и диалогов здесь нет: ошибки — исключениями.
    /// </summary>
    public static class AnalyticsReports
    {
        /// <summary>GUID шаблона Editor/Analytics/Report/ReportTemplate.html.</summary>
        public const string TemplateGuid = "adee35773eb5471ab1268ef40ee67e82";

        private const string PrefsPath = "UserSettings/AppAnalyticsReport.json";

        public static string ExportFolder => Path.Combine(Application.persistentDataPath, "AppAnalytics", "Export");
        public static string DefaultOutput => Path.Combine(Application.persistentDataPath, "AppAnalytics", "Reports");

        // ============================================================ настройки

        public static AnalyticsReportPrefs LoadPrefs()
        {
            try
            {
                if (File.Exists(PrefsPath))
                {
                    var prefs = JsonUtility.FromJson<AnalyticsReportPrefs>(File.ReadAllText(PrefsPath));
                    if (prefs != null)
                    {
                        prefs.folders ??= new List<string>();
                        prefs.builds ??= new List<string>();
                        prefs.platforms ??= new List<string>();
                        prefs.tags ??= new List<string>();
                        return prefs;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppAnalytics] Настройки отчёта не читаются ({PrefsPath}): {e.Message}");
            }

            return new AnalyticsReportPrefs();
        }

        public static void SavePrefs(AnalyticsReportPrefs prefs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath) ?? "UserSettings");
            File.WriteAllText(PrefsPath, JsonUtility.ToJson(prefs, true));
        }

        public static string OutputDir(AnalyticsReportPrefs prefs) =>
            string.IsNullOrWhiteSpace(prefs?.outputDir) ? DefaultOutput : prefs.outputDir;

        public static List<string> Sources(AnalyticsReportPrefs prefs)
        {
            var list = new List<string>();
            if (prefs == null || prefs.includeExport) list.Add(ExportFolder);
            if (prefs != null)
                foreach (var f in prefs.folders)
                    if (!string.IsNullOrWhiteSpace(f) && !list.Contains(f)) list.Add(f);
            return list;
        }

        // ============================================================ каталог

        /// <summary>Каталог для сборщика: ключи, поля, роли. Нет ассета — пустой каталог (всё без ролей).</summary>
        public static ReportCatalog Catalog(AnalyticsSettings settings = null)
        {
            settings ??= AnalyticsCatalog.Load();
            var catalog = new ReportCatalog { Title = string.IsNullOrEmpty(Application.productName) ? "Аналитика" : Application.productName };
            if (settings == null) return catalog;

            catalog.Schema = settings.SchemaHash();
            foreach (var e in settings.Entries)
            {
                if (string.IsNullOrEmpty(e.key)) continue;
                var info = new ReportEventInfo
                {
                    Key = e.key,
                    Title = e.title,
                    Role = (ReportRole)e.report.role,
                    Interval = Clean(e.report.interval),
                    Subject = Clean(e.report.subject),
                    Measure = Clean(e.report.measure),
                    Value = Clean(e.report.value),
                    Tone = (ReportTone)e.report.tone,
                };

                if (e.fields != null)
                    foreach (var f in e.fields)
                        info.Fields.Add(new ReportField { Name = f.name, Type = (ReportFieldType)f.type, Note = f.note });

                catalog.Events.Add(info);
            }

            return catalog;
        }

        private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // ============================================================ сборка

        public static ReportOptions Options(AnalyticsReportPrefs prefs)
        {
            var options = new ReportOptions { Anonymize = prefs != null && prefs.anonymize };
            if (prefs == null) return options;

            options.Filter.From = ParseDate(prefs.from, false);
            options.Filter.To = ParseDate(prefs.to, true);
            foreach (var b in prefs.builds) if (!string.IsNullOrWhiteSpace(b)) options.Filter.Builds.Add(b.Trim());
            foreach (var p in prefs.platforms) if (!string.IsNullOrWhiteSpace(p)) options.Filter.Platforms.Add(p.Trim());
            foreach (var t in prefs.tags) if (!string.IsNullOrWhiteSpace(t)) options.Filter.Tags.Add(t.Trim());
            return options;
        }

        /// <summary>«yyyy-MM-dd» → начало дня (или конец, для «по»), UTC. Пусто или не дата — без границы.</summary>
        public static DateTime? ParseDate(string s, bool endOfDay)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (!DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)) return null;
            return endOfDay ? d.AddDays(1).AddTicks(-1) : d;
        }

        public static ReportData Build(AnalyticsReportPrefs prefs = null)
        {
            prefs ??= LoadPrefs();
            return ReportBuilder.Build(Sources(prefs), Catalog(), Options(prefs));
        }

        // ============================================================ запись

        public static string LoadTemplate()
        {
            var path = AssetDatabase.GUIDToAssetPath(TemplateGuid);
            if (string.IsNullOrEmpty(path)) throw new FileNotFoundException("Шаблон отчёта не найден (ReportTemplate.html, GUID " + TemplateGuid + ").");
            return File.ReadAllText(Path.GetFullPath(path));
        }

        /// <summary>report.html. Путь пустой — в папку вывода с меткой времени. Возвращает полный путь.</summary>
        public static string WriteHtml(ReportData data, string path = null, AnalyticsReportPrefs prefs = null)
        {
            prefs ??= LoadPrefs();
            path = string.IsNullOrWhiteSpace(path) ? Path.Combine(OutputDir(prefs), "report_" + Stamp() + ".html") : path;
            ReportExport.WriteHtml(data, LoadTemplate(), path);
            return Path.GetFullPath(path);
        }

        /// <summary>Экспорт в формате. Путь пустой — в папку вывода с меткой времени. Возвращает полный путь.</summary>
        public static string Export(ReportData data, ExportFormat format, string path = null, AnalyticsReportPrefs prefs = null)
        {
            prefs ??= LoadPrefs();
            if (string.IsNullOrWhiteSpace(path))
            {
                var ext = format == ExportFormat.CsvZip ? ".zip" : format == ExportFormat.Json ? ".json" : ".jsonl";
                path = Path.Combine(OutputDir(prefs), "analytics_" + Stamp() + ext);
            }

            var csv = new CsvOptions { Delimiter = prefs.csvSemicolon ? ';' : ',', Bom = prefs.csvBom, IsoTimes = prefs.csvIsoTimes };
            ReportExport.Export(data, format, path, csv);
            return Path.GetFullPath(path);
        }

        public static ExportFormat ParseFormat(string s)
        {
            switch ((s ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "json": return ExportFormat.Json;
                case "jsonl": return ExportFormat.Jsonl;
                case "csv":
                case "zip":
                case "": return ExportFormat.CsvZip;
                default: throw new ArgumentException($"Формат «{s}»: csv | json | jsonl");
            }
        }

        private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        /// <summary>Краткая сводка для статуса и CLI.</summary>
        public static string Describe(ReportData data) =>
            $"пакетов {data.Packets} (дублей {data.DuplicatePackets}, битых строк {data.BrokenLines}) · игроков {data.Profiles.Count} · " +
            $"сессий {data.Sessions.Count} · прогонов {data.Runs.Count} · событий {data.EventCount}";

        [MenuItem("Exerussus/App/Analytics/Собрать отчёт")]
        private static void BuildMenu()
        {
            try
            {
                var data = Build();
                var path = WriteHtml(data);
                Debug.Log($"[AppAnalytics] Отчёт: {path}\n{Describe(data)}");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AppAnalytics] Отчёт не собран: {e.Message}");
            }
        }
    }
}
