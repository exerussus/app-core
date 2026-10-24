using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Exerussus.AppCore.Analytics.Reports
{
    public enum ExportFormat : byte
    {
        /// <summary>Набор CSV в zip: таблица на раздел.</summary>
        CsvZip = 0,
        /// <summary>Один JSON: мета, каталог, сессии, прогоны, профили, теги с таблицами, интервалы, события.</summary>
        Json = 1,
        /// <summary>Склеенные пакеты без дублей: строка на заголовок или событие.</summary>
        Jsonl = 2,
    }

    public sealed class CsvOptions
    {
        /// <summary>Разделитель: «;» — для Excel с русской локалью, «,» — для остального.</summary>
        public char Delimiter = ';';
        /// <summary>UTF-8 BOM: Excel без него ломает кириллицу.</summary>
        public bool Bom = true;
        /// <summary>Добавить колонки времени UTC в ISO 8601.</summary>
        public bool IsoTimes;

        /// <summary>Десятичная запятая в числах (по умолчанию — вместе с разделителем «;», как ждёт Excel с русской локалью).</summary>
        public bool? DecimalComma;

        public bool UseDecimalComma => DecimalComma ?? Delimiter == ';';

        /// <summary>Таблица сырых событий (events.csv). Профиль экспорта без «raw» её не кладёт.</summary>
        public bool IncludeEvents = true;
    }

    /// <summary>report.html и экспорт из <see cref="ReportData"/>. Файлы пишутся через временный и переносятся.</summary>
    public static class ReportExport
    {
        /// <summary>Маркер в шаблоне, на место которого встаёт JSON отчёта.</summary>
        public const string DataMarker = "/*__REPORT_DATA__*/null";

        // ============================================================ report.html

        public static void WriteHtml(ReportData data, string template, string path)
        {
            if (template == null || template.IndexOf(DataMarker, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Шаблон отчёта без маркера данных " + DataMarker);

            var json = Payload(data);
            var html = template.Replace(DataMarker, json);
            WriteAtomic(path, html, false);
        }

        /// <summary>JSON, который читает шаблон report.html: посчитанные представления, прогоны, игроки, таймлайны.</summary>
        public static string Payload(ReportData data)
        {
            var sb = new StringBuilder(1 << 16);
            var w = new JsonOut(sb);
            var sessionIndex = new Dictionary<ReportSession, int>();
            var runIndex = new Dictionary<ReportRun, int>();
            for (var i = 0; i < data.Sessions.Count; i++) sessionIndex[data.Sessions[i]] = i;
            for (var i = 0; i < data.Runs.Count; i++) runIndex[data.Runs[i]] = i;

            w.BeginObject();
            WriteMeta(w, data);
            w.Prop("catalog", CatalogNode(data));
            w.Prop("results", ResultClasses(data));

            w.Name("sessions").BeginArray();
            foreach (var s in data.Sessions)
            {
                w.BeginObject()
                 .Prop("id", s.Id).Prop("profile", s.Profile).Prop("start", Iso(s.StartTime))
                 .Prop("duration", Round(s.Duration))
                 .Prop("platform", s.Platform).Prop("build", s.Build).Prop("ended", s.Ended)
                 .Prop("dropped", s.Dropped).Prop("missing", s.Missing).Prop("runs", s.Runs.Count).Prop("events", s.Events.Count);
                w.Name("props");
                WriteMap(w, s.Props);
                w.EndObject();
            }
            w.EndArray();

            w.Name("runs").BeginArray();
            foreach (var r in data.Runs)
            {
                w.BeginObject()
                 .Prop("id", r.Id).Prop("session", sessionIndex[r.Session]).Prop("profile", r.Profile)
                 .Prop("tag", r.TagOrDash).Prop("result", r.Result).Prop("finished", r.Finished)
                 .Prop("start", Iso(r.StartTime)).Prop("duration", Round(r.Duration)).Prop("game", Round(r.GameTime))
                 .Prop("events", r.Events.Count)
                 .Prop("positive", r.Positive).Prop("negative", r.Negative)
                 .Prop("platform", r.Session.Platform).Prop("build", r.Session.Build)
                 .EndObject();
            }
            w.EndArray();

            w.Name("profiles").BeginArray();
            foreach (var p in data.Profiles)
            {
                w.BeginObject()
                 .Prop("id", p.Id).Prop("sessions", p.Sessions).Prop("runs", p.Runs)
                 .Prop("time", Round(p.Time)).Prop("negative", p.Negative)
                 .Prop("first", Iso(p.First)).Prop("last", Iso(p.Last))
                 .Prop("platform", p.Platform).Prop("build", p.Build);
                w.Name("results");
                WriteCounts(w, p.Results);
                w.Name("props");
                WriteMap(w, p.Props);
                w.EndObject();
            }
            w.EndArray();

            w.Name("views").BeginArray();
            foreach (var v in data.ViewResults) w.Value(v.Data);
            w.EndArray();

            w.Name("tags").BeginArray();
            foreach (var t in data.Tags)
            {
                w.BeginObject().Prop("tag", t.Tag);
                w.Name("runs").BeginArray();
                foreach (var r in t.Runs) w.Value(runIndex[r]);
                w.EndArray();
                w.Name("results");
                WriteCounts(w, t.Results);
                w.Name("views").BeginArray();
                foreach (var v in t.Views) w.Value(v.Data);
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            // таймлайны последних прогонов: дорожки посчитаны в C#, события в отчёт не кладутся
            w.Name("timelines").BeginArray();
            foreach (var tl in data.Timelines)
            {
                if (!runIndex.TryGetValue(tl.Run, out var index)) continue;
                w.BeginObject().Prop("run", index).Prop("end", Round(tl.Run.AxisEnd)).Prop("realEnd", Round(tl.Run.RealEnd))
                 .Prop("hasGame", !double.IsNaN(tl.Run.GameTime));
                w.Name("lanes").BeginArray();
                foreach (var lane in tl.Lanes) w.Value(lane);
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            w.EndObject();
            return sb.ToString();
        }

        private static void WriteMeta(JsonOut w, ReportData data)
        {
            w.Name("meta").BeginObject()
             .Prop("title", data.Options.Title ?? data.Catalog.Title)
             .Prop("generated", Iso(data.Generated))
             .Prop("files", data.Files).Prop("packets", data.Packets).Prop("duplicates", data.DuplicatePackets)
             .Prop("broken", data.BrokenLines).Prop("events", data.EventCount)
             .Prop("anonymized", data.Options.Anonymize)
             .Prop("filter", (data.Options.Filter ?? new ReportFilter()).Describe())
             .Prop("timezone", data.Time.Name)
             .Prop("from", Iso(data.PeriodFrom)).Prop("to", Iso(data.PeriodTo))
             .Prop("timelineRuns", data.Timelines.Count);
            if (data.Previous != null) w.Prop("previousFrom", Iso(data.Previous.PeriodFrom)).Prop("previousTo", Iso(data.Previous.PeriodTo));
            WriteStrings(w, "sources", data.Sources);
            WriteStrings(w, "schemas", data.Schemas);
            WriteStrings(w, "builds", data.Builds);
            WriteStrings(w, "platforms", data.Platforms);
            WriteStrings(w, "warnings", data.Warnings);
            WriteStrings(w, "fields", data.FieldNames);
            w.EndObject();
        }

        /// <summary>
        /// Каталог для отчёта и выгрузок: события с полями и классификацией (куда входят), представления, профили
        /// экспорта, свойства. Внешний инструмент разберёт выгрузку по нему, не зная, что за игра.
        /// </summary>
        public static JsonNode CatalogNode(ReportData data)
        {
            var config = data.Catalog.Config ?? new ReportConfig();
            var views = config.EffectiveViews();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in data.AllEvents())
            {
                counts.TryGetValue(e.Key, out var n);
                counts[e.Key] = n + 1;
            }

            var keys = new List<string>();
            foreach (var e in data.Catalog.Events)
                if (!string.IsNullOrEmpty(e.Key)) keys.Add(e.Key);
            var extra = new List<string>();
            foreach (var k in counts.Keys)
                if (data.Catalog.Find(k) == null) extra.Add(k);
            extra.Sort(StringComparer.Ordinal);
            keys.AddRange(extra);

            var events = JsonNode.Arr();
            foreach (var key in keys)
            {
                var info = data.Catalog.FindAny(key);
                var cls = config.FindEvent(key);
                var n = JsonNode.Obj().Set("key", key).SetIf("title", info?.Title).SetIf("when", info?.When)
                                .Set("system", data.Catalog.Find(key) == null).Set("count", counts.TryGetValue(key, out var c) ? c : 0)
                                .Set("raw", cls != null && cls.Raw);
                var fields = JsonNode.Arr();
                if (info != null)
                    foreach (var f in info.Fields)
                        fields.Add(JsonNode.Obj().Set("name", f.Name ?? string.Empty).Set("type", f.Type.ToString().ToLowerInvariant()).SetIf("note", f.Note));
                n.Set("fields", fields);
                var bind = JsonNode.Arr();
                foreach (var b in config.BindingsOf(key, views)) bind.Add(ReportConfig.WriteBinding(b, true));
                n.Set("bind", bind);
                events.Add(n);
            }

            var viewNodes = JsonNode.Arr();
            foreach (var v in views)
            {
                var vn = ReportConfig.WriteView(v);
                if (v.Builtin) vn.Set("builtin", true);
                vn.Set("typeTitle", ViewTypes.Of(v.Type).Title);
                viewNodes.Add(vn);
            }

            var full = config.ToNode();
            return JsonNode.Obj().Set("schema", data.Catalog.Schema ?? string.Empty).Set("events", events).Set("views", viewNodes)
                           .Set("profiles", full["profiles"]).Set("properties", full["properties"]).Set("timezone", config.TimeZone ?? "UTC");
        }

        /// <summary>Классы итогов прогонов (подпись, окраска) — из представления app_runs проекта или встроенного.</summary>
        private static JsonNode ResultClasses(ReportData data)
        {
            var config = data.Catalog.Config ?? new ReportConfig();
            var view = config.FindView(ViewTypes.BuiltinRuns) ?? ViewTypes.BuiltinRunsView();
            var node = JsonNode.Obj();
            var core = ViewTypes.BuiltinRunsView();
            foreach (var list in new[] { view.Outcomes, core.Outcomes })
                foreach (var o in list)
                    if (!string.IsNullOrEmpty(o.Value) && node[o.Value] == null)
                        node.Set(o.Value, JsonNode.Obj().Set("label", string.IsNullOrWhiteSpace(o.Label) ? o.Value : o.Label).Set("tone", ViewTypes.Name(o.Tone)));
            return node;
        }

        private static void WriteMap(JsonOut w, Dictionary<string, string> map)
        {
            w.BeginObject();
            if (map != null)
            {
                var keys = new List<string>(map.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys) w.Prop(k, map[k]);
            }
            w.EndObject();
        }

        private static void WriteCounts(JsonOut w, SortedDictionary<string, int> counts)
        {
            w.BeginObject();
            foreach (var pair in counts) w.Prop(pair.Key, pair.Value);
            w.EndObject();
        }

        private static void WriteStrings(JsonOut w, string name, IEnumerable<string> values)
        {
            w.Name(name).BeginArray();
            foreach (var v in values) w.Value(v);
            w.EndArray();
        }

        // ============================================================ экспорт

        public static void Export(ReportData data, ExportFormat format, string path, CsvOptions csv = null)
        {
            switch (format)
            {
                case ExportFormat.CsvZip: WriteCsvZip(data, path, csv ?? new CsvOptions()); break;
                case ExportFormat.Json: WriteAtomic(path, FullJson(data, csv ?? new CsvOptions()), false); break;
                case ExportFormat.Jsonl: WriteAtomic(path, string.Join("\n", data.RawLines) + "\n", false); break;
            }
        }

        /// <summary>Таблицы экспорта: id файла → таблица. Тот же набор пишут CSV и полный JSON.</summary>
        public static List<ReportTable> Tables(ReportData data, CsvOptions csv = null)
        {
            csv ??= new CsvOptions();
            var tables = new List<ReportTable>();
            var props = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var s in data.Sessions)
                foreach (var k in s.Props.Keys) props.Add(k);

            var sessions = new ReportTable { Id = "sessions", Title = "Сессии" };
            sessions.Columns.AddRange(new[] { "profile", "session", "start_utc", "duration", "platform", "build", "runs", "events", "ended", "dropped", "missing" });
            foreach (var k in props) sessions.Columns.Add("prop_" + k);
            foreach (var s in data.Sessions)
            {
                var row = new List<JsonValue> { S(s.Profile), S(s.Id), S(Iso(s.StartTime)), N(s.Duration), S(s.Platform), S(s.Build), N(s.Runs.Count), N(s.Events.Count), JsonValue.Of(s.Ended), N(s.Dropped), N(s.Missing) };
                foreach (var k in props) row.Add(S(s.Props.TryGetValue(k, out var v) ? v : null));
                sessions.Rows.Add(row.ToArray());
            }
            tables.Add(sessions);

            var runs = new ReportTable { Id = "runs", Title = "Прогоны" };
            runs.Columns.AddRange(new[] { "profile", "session", "run", "tag", "result", "finished", "start_utc", "duration", "game_time", "events", "positive", "negative", "platform", "build" });
            foreach (var r in data.Runs)
                runs.Rows.Add(new[] { S(r.Profile), S(r.Session.Id), S(r.Id), S(r.TagOrDash), S(r.Result), JsonValue.Of(r.Finished), S(Iso(r.StartTime)), N(r.Duration), N(r.GameTime), N(r.Events.Count), N(r.Positive), N(r.Negative), S(r.Session.Platform), S(r.Session.Build) });
            tables.Add(runs);

            var profiles = new ReportTable { Id = "profiles", Title = "Игроки" };
            var results = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in data.Profiles)
                foreach (var k in p.Results.Keys) results.Add(k);
            profiles.Columns.AddRange(new[] { "profile", "first_seen", "last_seen", "sessions", "runs", "time", "negative", "platform", "build" });
            foreach (var k in results) profiles.Columns.Add("result_" + k);
            foreach (var k in props) profiles.Columns.Add("prop_" + k);
            foreach (var p in data.Profiles)
            {
                var row = new List<JsonValue> { S(p.Id), S(Iso(p.First)), S(Iso(p.Last)), N(p.Sessions), N(p.Runs), N(p.Time), N(p.Negative), S(p.Platform), S(p.Build) };
                foreach (var k in results)
                {
                    p.Results.TryGetValue(k, out var n);
                    row.Add(N(n));
                }
                foreach (var k in props) row.Add(S(p.Props.TryGetValue(k, out var v) ? v : null));
                profiles.Rows.Add(row.ToArray());
            }
            tables.Add(profiles);

            if (csv.IncludeEvents)
            {
                var events = new ReportTable { Id = "events", Title = "События" };
                events.Columns.AddRange(new[] { "profile", "session", "run", "tag", "s", "t", "g", "session_t" });
                if (csv.IsoTimes) events.Columns.Add("utc");
                events.Columns.Add("event");
                // поле события с именем служебной колонки (tag у app.run.begin…) — с префиксом, без дублей
                var fixedColumns = new HashSet<string>(events.Columns, StringComparer.Ordinal);
                foreach (var name in data.FieldNames) events.Columns.Add(fixedColumns.Contains(name) ? "f_" + name : name);
                foreach (var s in data.Sessions)
                {
                    foreach (var e in s.Events)
                    {
                        var row = new List<JsonValue>
                        {
                            S(s.Profile), S(s.Id), S(e.Run?.Id), S(e.Run?.TagOrDash), N(e.Seq), N(e.T), N(e.G), N(e.SessionTime),
                        };
                        if (csv.IsoTimes) row.Add(S(Iso(e.Utc)));
                        row.Add(S(e.Key));
                        foreach (var name in data.FieldNames) row.Add(e.Field(name));
                        events.Rows.Add(row.ToArray());
                    }
                }
                tables.Add(events);
            }

            // представления: таблица на представление; по тегам — одна таблица с тегом первой колонкой
            foreach (var v in data.ViewResults)
            {
                if (!v.View.Export) continue;
                tables.AddRange(v.Tables);
            }

            // по тегам: колонки у тегов могут различаться (мера есть не у всех) — объединение колонок, ячейки по id
            var byTag = new Dictionary<string, ReportTable>(StringComparer.Ordinal);
            var parts = new Dictionary<string, List<KeyValuePair<string, ReportTable>>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var tag in data.Tags)
                foreach (var v in tag.Views)
                {
                    if (!v.View.Export) continue;
                    foreach (var t in v.Tables)
                    {
                        if (t.Id.EndsWith("_intervals", StringComparison.Ordinal)) continue; // интервалы уже есть целиком
                        var id = t.Id + "_by_tag";
                        if (!parts.TryGetValue(id, out var list))
                        {
                            parts[id] = list = new List<KeyValuePair<string, ReportTable>>();
                            order.Add(id);
                        }

                        list.Add(new KeyValuePair<string, ReportTable>(tag.Tag, t));
                    }
                }

            foreach (var id in order)
            {
                var list = parts[id];
                var all = new ReportTable { Id = id, Title = list[0].Value.Title + " — по тегам" };
                all.Add("tag", "Тег");
                var index = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var pair in list)
                    for (var i = 0; i < pair.Value.Columns.Count; i++)
                    {
                        var c = pair.Value.Columns[i];
                        if (index.ContainsKey(c)) continue;
                        index[c] = all.Columns.Count;
                        all.Add(c, i < pair.Value.Labels.Count ? pair.Value.Labels[i] : c);
                    }

                foreach (var pair in list)
                    foreach (var row in pair.Value.Rows)
                    {
                        var r = new JsonValue[all.Columns.Count];
                        r[0] = S(pair.Key);
                        for (var i = 0; i < row.Length && i < pair.Value.Columns.Count; i++) r[index[pair.Value.Columns[i]]] = row[i];
                        all.Rows.Add(r);
                    }

                byTag[id] = all;
                tables.Add(all);
            }

            return tables;
        }

        private static void WriteCsvZip(ReportData data, string path, CsvOptions csv)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
                using (var stream = new FileStream(temp, FileMode.CreateNew))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var table in Tables(data, csv))
                    {
                        var entry = zip.CreateEntry(SafeName(table.Id) + ".csv", CompressionLevel.Optimal);
                        using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(csv.Bom)))
                            WriteCsv(w, table, csv.Delimiter, csv.UseDecimalComma);
                    }

                    var catalog = zip.CreateEntry("catalog.json", CompressionLevel.Optimal);
                    using (var w = new StreamWriter(catalog.Open(), new UTF8Encoding(false)))
                        w.Write(CatalogJson(data));
                }

                Replace(temp, path);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        public static void WriteCsv(TextWriter w, ReportTable table, char delimiter, bool decimalComma = false)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (i > 0) w.Write(delimiter);
                w.Write(CsvCell(table.Columns[i], delimiter));
            }
            w.Write("\r\n");

            foreach (var row in table.Rows)
            {
                for (var i = 0; i < row.Length; i++)
                {
                    if (i > 0) w.Write(delimiter);
                    var v = row[i];
                    if (v.Kind == JsonKind.String) w.Write(CsvCell(v.Text, delimiter));
                    else
                    {
                        var text = v.AsText() ?? string.Empty;
                        if (decimalComma && v.Kind == JsonKind.Number) text = text.Replace('.', ',');
                        w.Write(text);
                    }
                }
                w.Write("\r\n");
            }
        }

        private static string CsvCell(string s, char delimiter)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // формулы Excel (=, +, -, @) в начале строки — экранировать апострофом: защита от CSV-инъекций
            if (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@') s = "'" + s;
            var quote = s.IndexOf(delimiter) >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        /// <summary>catalog.json: мета выгрузки + каталог (события, классификация, представления).</summary>
        public static string CatalogJson(ReportData data)
        {
            var sb = new StringBuilder();
            var json = new JsonOut(sb).BeginObject();
            WriteMeta(json, data);
            json.Prop("catalog", CatalogNode(data));
            json.EndObject();
            return sb.ToString();
        }

        private static string FullJson(ReportData data, CsvOptions csv)
        {
            var sb = new StringBuilder(1 << 16);
            var w = new JsonOut(sb).BeginObject();
            WriteMeta(w, data);
            w.Prop("catalog", CatalogNode(data));
            w.Name("views").BeginObject();
            foreach (var v in data.ViewResults) w.Name(v.View.Id ?? "?").Value(v.Data);
            w.EndObject();
            w.Name("tables").BeginObject();
            foreach (var table in Tables(data, csv))
            {
                w.Name(table.Id).BeginArray();
                foreach (var row in table.Rows)
                {
                    w.BeginObject();
                    for (var i = 0; i < table.Columns.Count && i < row.Length; i++)
                    {
                        if (row[i].IsNull) continue;
                        w.Name(table.Columns[i]).Value(row[i]);
                    }
                    w.EndObject();
                }
                w.EndArray();
            }
            w.EndObject();
            w.EndObject();
            return sb.ToString();
        }

        // ============================================================ утилиты

        private static JsonValue S(string v) => JsonValue.Of(v);

        private static JsonValue N(double v) => double.IsNaN(v) ? JsonValue.Null : JsonValue.Of(Math.Round(v, 3));

        private static double Round(double v) => double.IsNaN(v) ? double.NaN : Math.Round(v, 3);

        public static string Iso(DateTime? t) => t?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        private static string SafeName(string id)
        {
            var sb = new StringBuilder(id.Length);
            foreach (var c in id) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        public static void WriteAtomic(string path, string text, bool bom)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(bom));
                Replace(temp, path);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        private static void Replace(string temp, string path)
        {
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // занят — не мешать исходной ошибке
            }
        }
    }
}
