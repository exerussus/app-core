using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Exerussus.AppCore.Analytics.Reports;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Analytics.Editor
{
    /// <summary>
    /// Файл представлений проекта: <c>Assets/App/Settings/Editor/AnalyticsReport.json</c> — представления,
    /// классификация событий, профили экспорта. Лежит в git (общий для команды), в сборку не попадает (папка Editor,
    /// ни на что не ссылается): рантайм и память WebGL о нём не знают.
    /// </summary>
    public static class AnalyticsReportStore
    {
        public const string Path = AppCorePaths.SettingsDir + "/Editor/AnalyticsReport.json";

        private static ReportConfig _cached;
        private static DateTime _cachedStamp;

        /// <summary>Файл есть, но не разбирается: сохранять поверх нельзя (потеряется правка руками).</summary>
        public static string Broken { get; private set; }

        /// <summary>Конфигурация. Нет файла — пустая (или перенесённая из ролей каталога 4.8). Битый файл — пустая и <see cref="Broken"/>.</summary>
        public static ReportConfig Load()
        {
            try
            {
                var full = System.IO.Path.GetFullPath(Path);
                if (!File.Exists(full))
                {
                    Broken = null;
                    var migrated = MigrateLegacy();
                    if (migrated != null) return migrated;
                    return new ReportConfig();
                }

                var stamp = File.GetLastWriteTimeUtc(full);
                if (_cached != null && stamp == _cachedStamp) return _cached;

                var config = ReportConfig.Parse(File.ReadAllText(full));
                Broken = null;
                _cached = config;
                _cachedStamp = stamp;
                return config;
            }
            catch (Exception e) when (e is FormatException || e is IOException || e is UnauthorizedAccessException)
            {
                Broken = $"{Path}: {e.Message}";
                Debug.LogError("[AppAnalytics] Файл представлений не читается — " + Broken);
                return new ReportConfig();
            }
        }

        /// <summary>Записать (через временный файл). Битый файл не перезаписывается — сначала почините его руками.</summary>
        public static void Save(ReportConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (Broken != null && File.Exists(System.IO.Path.GetFullPath(Path)))
                throw new InvalidOperationException("Файл представлений не разбирается — сохранение отменено, чтобы не затереть его: " + Broken);

            var full = System.IO.Path.GetFullPath(Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full) ?? ".");
            var text = config.ToJson();
            if (File.Exists(full) && File.ReadAllText(full) == text) return;
            ReportExport.WriteAtomic(full, text, false);
            _cached = null;
            AssetDatabase.ImportAsset(Path);
        }

        public static void Invalidate()
        {
            _cached = null;
            _legacyChecked = false;
        }

        /// <summary>Замечания по классификации событий и представлениям (ошибки — неклассифицированные события, битые привязки).</summary>
        public static List<ReportIssue> Validate(AnalyticsSettings settings = null, ReportConfig config = null)
        {
            settings ??= AnalyticsCatalog.Load();
            config ??= Load();
            var events = new List<ReportEventInfo>();
            if (settings != null) events.AddRange(AnalyticsReports.Catalog(settings).Events);
            var issues = ReportValidation.Validate(config, events);
            if (Broken != null) issues.Insert(0, new ReportIssue(ReportIssueLevel.Error, "file", Broken));
            return issues;
        }

        // ============================================================ перенос ролей 4.8

        /// <summary>
        /// AppCore 4.8 хранил роль события прямо в ассете (поле <c>report</c>); в 5.0 его нет. Пока Unity не
        /// пересохранила ассет, роли ещё в YAML — переносим их в файл представлений один раз.
        /// </summary>
        private static bool _legacyChecked;

        public static ReportConfig MigrateLegacy()
        {
            if (_legacyChecked) return null;
            _legacyChecked = true;
            // файл уже есть — перенос не нужен (иначе каждая перезагрузка домена затирала бы правки команды)
            if (File.Exists(System.IO.Path.GetFullPath(Path))) return null;
            var asset = System.IO.Path.GetFullPath(AnalyticsSettings.AssetPath);
            if (!File.Exists(asset)) return null;
            string text;
            try
            {
                text = File.ReadAllText(asset);
            }
            catch (IOException)
            {
                return null;
            }

            if (text.IndexOf("\n    report:", StringComparison.Ordinal) < 0) return null;

            var config = FromLegacyYaml(text);
            if (config == null) return null;
            try
            {
                Save(config);
                Debug.Log($"[AppAnalytics] Роли событий из каталога 4.8 перенесены в {Path} — проверьте представления в Nexus → Аналитика.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AppAnalytics] Перенос ролей 4.8 не записан: " + e.Message);
            }

            return config;
        }

        private static readonly Regex Entry = new Regex(@"\n  - key: *(?<key>[^\r\n]*)(?<body>(?:\n(?!  - ).*)*)", RegexOptions.Compiled);
        private static readonly Regex Report = new Regex(@"\n    report:(?<body>(?:\n      .*)*)", RegexOptions.Compiled);

        /// <summary>YAML ассета 4.8 → представления: интервалы, метки (дорожка + разбивка по субъекту), состояния.</summary>
        public static ReportConfig FromLegacyYaml(string yaml)
        {
            var config = new ReportConfig();
            var any = false;
            foreach (Match m in Entry.Matches(yaml.Replace("\r\n", "\n")))
            {
                var key = m.Groups["key"].Value.Trim();
                if (key.Length == 0) continue;
                var r = Report.Match(m.Groups["body"].Value);
                if (!r.Success)
                {
                    config.GetOrAddEvent(key).Raw = true;
                    continue;
                }

                string Get(string name)
                {
                    var f = Regex.Match(r.Groups["body"].Value, @"\n      " + name + @": *(?<v>[^\r\n]*)");
                    var v = f.Success ? f.Groups["v"].Value.Trim() : null;
                    return string.IsNullOrEmpty(v) ? null : v;
                }

                int.TryParse(Get("role") ?? "0", out var role);
                int.TryParse(Get("tone") ?? "0", out var tone);
                var interval = Get("interval");
                var subject = Get("subject");
                var measure = Get("measure");
                var value = Get("value");
                var cls = config.GetOrAddEvent(key);
                var t = tone == 1 ? Tone.Positive : tone == 2 ? Tone.Negative : Tone.Neutral;

                switch (role)
                {
                    case 1: // метка
                        View(config, "marks", "Метки", ViewType.Lane);
                        cls.Bindings.Add(new ViewBinding { View = "marks", Role = BindRole.Point, Subject = subject, Tone = t });
                        if (subject != null)
                        {
                            var id = Slug("by_" + subject);
                            View(config, id, "По " + subject, ViewType.Breakdown).GroupBy.Add("subject");
                            cls.Bindings.Add(new ViewBinding { View = id, Role = BindRole.Point, Subject = subject, Measure = measure });
                        }
                        else if (measure != null)
                        {
                            var id = Slug(LastSegment(key) + "_" + measure);
                            View(config, id, key + ": " + measure, ViewType.Distribution);
                            cls.Bindings.Add(new ViewBinding { View = id, Role = BindRole.Point, Measure = measure });
                        }
                        any = true;
                        break;
                    case 2: // начало интервала
                    case 3: // конец интервала
                        if (interval == null)
                        {
                            cls.Raw = true;
                            break;
                        }

                        var vid = Slug(interval);
                        View(config, vid, interval, ViewType.Interval).GroupBy.Add("subject");
                        cls.Bindings.Add(new ViewBinding { View = vid, Role = role == 2 ? BindRole.Begin : BindRole.End, Subject = subject, Measure = role == 3 ? measure : null });
                        if (role == 2 && subject != null)
                        {
                            View(config, vid + "_funnel", interval + ": порядок", ViewType.Funnel);
                            cls.Bindings.Add(new ViewBinding { View = vid + "_funnel", Role = BindRole.Step, Subject = subject });
                        }
                        any = true;
                        break;
                    case 4: // состояние
                        if (value == null)
                        {
                            cls.Raw = true;
                            break;
                        }

                        var sid = Slug(LastSegment(key) + "_state");
                        View(config, sid, key, ViewType.State);
                        cls.Bindings.Add(new ViewBinding { View = sid, Role = BindRole.Value, Value = value });
                        any = true;
                        break;
                    default:
                        cls.Raw = true;
                        break;
                }

                // вся группировка уникальна
                foreach (var v in config.Views)
                {
                    var unique = new List<string>();
                    foreach (var g in v.GroupBy)
                        if (!unique.Contains(g)) unique.Add(g);
                    v.GroupBy.Clear();
                    v.GroupBy.AddRange(unique);
                }
            }

            return any ? config : null;
        }

        private static ReportView View(ReportConfig config, string id, string title, ViewType type)
        {
            var v = config.FindView(id);
            if (v != null) return v;
            v = new ReportView { Id = id, Title = title, Type = type };
            config.Views.Add(v);
            return v;
        }

        private static string LastSegment(string key)
        {
            var i = key.LastIndexOf('.');
            return i >= 0 ? key.Substring(i + 1) : key;
        }

        private static string Slug(string s)
        {
            var chars = new List<char>();
            foreach (var c in s)
                chars.Add((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ? char.ToLowerInvariant(c) : '_');
            var r = new string(chars.ToArray()).Trim('_');
            if (r.Length == 0 || char.IsDigit(r[0])) r = "v_" + r;
            return r.Length > 64 ? r.Substring(0, 64) : r;
        }
    }
}
