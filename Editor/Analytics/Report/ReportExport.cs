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

        /// <summary>JSON, который читает шаблон report.html.</summary>
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
            WriteCatalog(w, data);

            w.Name("sessions").BeginArray();
            foreach (var s in data.Sessions)
            {
                w.BeginObject()
                 .Prop("id", s.Id).Prop("profile", s.Profile).Prop("start", Iso(s.StartTime))
                 .Prop("duration", Round(s.Duration)).Prop("away", Round(s.Away))
                 .Prop("platform", s.Platform).Prop("build", s.Build).Prop("ended", s.Ended)
                 .Prop("dropped", s.Dropped).Prop("runs", s.Runs.Count).Prop("events", s.Events.Count)
                 .EndObject();
            }
            w.EndArray();

            w.Name("runs").BeginArray();
            foreach (var r in data.Runs)
            {
                w.BeginObject()
                 .Prop("id", r.Id).Prop("session", sessionIndex[r.Session]).Prop("profile", r.Profile)
                 .Prop("tag", r.TagOrDash).Prop("result", r.Result).Prop("finished", r.Finished)
                 .Prop("start", Iso(r.StartTime)).Prop("duration", Round(r.Duration)).Prop("game", Round(r.GameTime))
                 .Prop("away", Round(r.Away)).Prop("events", r.Events.Count)
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
                 .Prop("time", Round(p.Time)).Prop("away", Round(p.Away)).Prop("negative", p.Negative)
                 .Prop("first", Iso(p.First)).Prop("last", Iso(p.Last))
                 .Prop("platform", p.Platform).Prop("build", p.Build);
                w.Name("results");
                WriteCounts(w, p.Results);
                w.EndObject();
            }
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
                w.Name("tables").BeginArray();
                foreach (var table in t.Tables) WriteTable(w, table);
                w.EndArray();
                w.Name("funnels").BeginArray();
                foreach (var f in t.Funnels)
                {
                    w.BeginObject().Prop("interval", f.Interval).Prop("field", f.SubjectField).Prop("runs", f.Runs);
                    w.Name("steps").BeginArray();
                    foreach (var step in f.Steps)
                        w.BeginObject().Prop("subject", step.Subject).Prop("reached", step.Reached).Prop("closed", step.Closed)
                         .Prop("median", Round(step.MedianDuration)).EndObject();
                    w.EndArray();
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            // интервалы: [сессия, прогон|-1, имя, субъект, начало, конец, закрыт]
            w.Name("intervals").BeginArray();
            foreach (var i in data.Intervals)
            {
                w.BeginArray().Value(sessionIndex[i.Session]).Value(i.Run != null ? runIndex[i.Run] : -1)
                 .Value(i.Name).Value(i.Subject).Value(Round(i.Start)).Value(Round(i.End)).Value(i.Closed).EndArray();
            }
            w.EndArray();

            // события компактно: [сессия, прогон|-1, s, t, g|null, ключ (индекс в keys), {поля}]
            var keys = new List<string>();
            var keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            w.Name("events").BeginArray();
            foreach (var s in data.Sessions)
            {
                foreach (var e in s.Events)
                {
                    if (!keyIndex.TryGetValue(e.Key, out var k))
                    {
                        k = keys.Count;
                        keys.Add(e.Key);
                        keyIndex.Add(e.Key, k);
                    }

                    w.BeginArray().Value(sessionIndex[s]).Value(e.Run != null ? runIndex[e.Run] : -1).Value(e.Seq)
                     .Value(Round(e.T)).Value(Round(e.G)).Value(k);
                    w.BeginObject();
                    foreach (var f in e.Fields) w.Name(f.Key).Value(f.Value);
                    w.EndObject();
                    w.EndArray();
                }
            }
            w.EndArray();

            w.Name("keys").BeginArray();
            foreach (var k in keys) w.Value(k);
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
             .Prop("filter", (data.Options.Filter ?? new ReportFilter()).Describe());
            WriteStrings(w, "sources", data.Sources);
            WriteStrings(w, "schemas", data.Schemas);
            WriteStrings(w, "builds", data.Builds);
            WriteStrings(w, "platforms", data.Platforms);
            WriteStrings(w, "warnings", data.Warnings);
            WriteStrings(w, "fields", data.FieldNames);
            w.EndObject();
        }

        private static void WriteCatalog(JsonOut w, ReportData data)
        {
            w.Name("catalog").BeginArray();
            foreach (var e in data.Catalog.Events)
            {
                w.BeginObject().Prop("key", e.Key).Prop("title", e.Title).Prop("role", e.Role.ToString().ToLowerInvariant())
                 .Prop("interval", e.Interval).Prop("subject", e.Subject).Prop("measure", e.Measure).Prop("value", e.Value)
                 .Prop("tone", e.Tone.ToString().ToLowerInvariant());
                w.Name("fields").BeginArray();
                foreach (var f in e.Fields)
                    w.BeginObject().Prop("name", f.Name).Prop("type", f.Type.ToString().ToLowerInvariant()).Prop("note", f.Note).EndObject();
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
        }

        private static void WriteTable(JsonOut w, ReportTable table)
        {
            w.BeginObject().Prop("id", table.Id).Prop("title", table.Title);
            WriteStrings(w, "columns", table.Columns);
            WriteStrings(w, "labels", table.Labels.Count == table.Columns.Count ? table.Labels : table.Columns);
            w.Name("rows").BeginArray();
            foreach (var row in table.Rows)
            {
                w.BeginArray();
                foreach (var v in row) w.Value(v);
                w.EndArray();
            }
            w.EndArray();
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
                case ExportFormat.Json: WriteAtomic(path, FullJson(data), false); break;
                case ExportFormat.Jsonl: WriteAtomic(path, string.Join("\n", data.RawLines) + "\n", false); break;
            }
        }

        /// <summary>Таблицы экспорта: id файла → таблица. Тот же набор пишут CSV и полный JSON.</summary>
        public static List<ReportTable> Tables(ReportData data, CsvOptions csv = null)
        {
            csv ??= new CsvOptions();
            var tables = new List<ReportTable>();

            var sessions = new ReportTable { Id = "sessions", Title = "Сессии" };
            sessions.Columns.AddRange(new[] { "profile", "session", "start_utc", "duration", "away", "platform", "build", "runs", "events", "ended", "dropped" });
            foreach (var s in data.Sessions)
                sessions.Rows.Add(new[] { S(s.Profile), S(s.Id), S(Iso(s.StartTime)), N(s.Duration), N(s.Away), S(s.Platform), S(s.Build), N(s.Runs.Count), N(s.Events.Count), JsonValue.Of(s.Ended), N(s.Dropped) });
            tables.Add(sessions);

            var runs = new ReportTable { Id = "runs", Title = "Прогоны" };
            runs.Columns.AddRange(new[] { "profile", "session", "run", "tag", "result", "finished", "start_utc", "duration", "game_time", "away", "events", "positive", "negative", "platform", "build" });
            foreach (var r in data.Runs)
                runs.Rows.Add(new[] { S(r.Profile), S(r.Session.Id), S(r.Id), S(r.TagOrDash), S(r.Result), JsonValue.Of(r.Finished), S(Iso(r.StartTime)), N(r.Duration), N(r.GameTime), N(r.Away), N(r.Events.Count), N(r.Positive), N(r.Negative), S(r.Session.Platform), S(r.Session.Build) });
            tables.Add(runs);

            var profiles = new ReportTable { Id = "profiles", Title = "Игроки" };
            var results = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in data.Profiles)
                foreach (var k in p.Results.Keys) results.Add(k);
            profiles.Columns.AddRange(new[] { "profile", "first_seen", "last_seen", "sessions", "runs", "time", "away", "negative", "platform", "build" });
            foreach (var k in results) profiles.Columns.Add("result_" + k);
            foreach (var p in data.Profiles)
            {
                var row = new List<JsonValue> { S(p.Id), S(Iso(p.First)), S(Iso(p.Last)), N(p.Sessions), N(p.Runs), N(p.Time), N(p.Away), N(p.Negative), S(p.Platform), S(p.Build) };
                foreach (var k in results)
                {
                    p.Results.TryGetValue(k, out var n);
                    row.Add(N(n));
                }
                profiles.Rows.Add(row.ToArray());
            }
            tables.Add(profiles);

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
                    if (csv.IsoTimes) row.Add(S(s.StartTime != null ? Iso(s.StartTime.Value.AddSeconds(e.SessionTime)) : null));
                    row.Add(S(e.Key));
                    foreach (var name in data.FieldNames) row.Add(e.Field(name));
                    events.Rows.Add(row.ToArray());
                }
            }
            tables.Add(events);

            if (data.Intervals.Count > 0)
            {
                var intervals = new ReportTable { Id = "intervals", Title = "Интервалы" };
                intervals.Columns.AddRange(new[] { "profile", "session", "run", "tag", "interval", "subject", "start", "end", "duration", "closed" });
                foreach (var i in data.Intervals)
                    intervals.Rows.Add(new[] { S(i.Session.Profile), S(i.Session.Id), S(i.Run?.Id), S(i.Run?.TagOrDash), S(i.Name), S(i.Subject), N(i.Start), N(i.End), N(i.Duration), JsonValue.Of(i.Closed) });
                tables.Add(intervals);
            }

            // сводки по субъектам и воронки — все теги в одной таблице, тег первой колонкой
            var subjectTables = new Dictionary<string, ReportTable>(StringComparer.Ordinal);
            var funnels = new Dictionary<string, ReportTable>(StringComparer.Ordinal);
            foreach (var tag in data.Tags)
            {
                foreach (var t in tag.Tables)
                {
                    if (!subjectTables.TryGetValue(t.Id, out var all))
                    {
                        all = new ReportTable { Id = t.Id, Title = t.Title };
                        all.Add("tag", "Тег");
                        for (var i = 0; i < t.Columns.Count; i++) all.Add(t.Columns[i], i < t.Labels.Count ? t.Labels[i] : t.Columns[i]);
                        subjectTables.Add(t.Id, all);
                        tables.Add(all);
                    }

                    foreach (var row in t.Rows)
                    {
                        var r = new JsonValue[row.Length + 1];
                        r[0] = S(tag.Tag);
                        Array.Copy(row, 0, r, 1, row.Length);
                        all.Rows.Add(r);
                    }
                }

                foreach (var f in tag.Funnels)
                {
                    var id = "funnel_" + f.Interval;
                    if (!funnels.TryGetValue(id, out var all))
                    {
                        all = new ReportTable { Id = id, Title = "Воронка " + f.Interval };
                        all.Columns.AddRange(new[] { "tag", "step", f.SubjectField, "runs", "reached", "closed", "median_duration" });
                        funnels.Add(id, all);
                        tables.Add(all);
                    }

                    for (var i = 0; i < f.Steps.Count; i++)
                    {
                        var st = f.Steps[i];
                        all.Rows.Add(new[] { S(tag.Tag), N(i + 1), S(st.Subject), N(f.Runs), N(st.Reached), N(st.Closed), N(st.MedianDuration) });
                    }
                }
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
                    {
                        var sb = new StringBuilder();
                        var json = new JsonOut(sb).BeginObject();
                        WriteMeta(json, data);
                        WriteCatalog(json, data);
                        json.EndObject();
                        w.Write(sb.ToString());
                    }
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

        private static string FullJson(ReportData data)
        {
            var sb = new StringBuilder(1 << 16);
            var w = new JsonOut(sb).BeginObject();
            WriteMeta(w, data);
            WriteCatalog(w, data);
            w.Name("tables").BeginObject();
            foreach (var table in Tables(data))
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
