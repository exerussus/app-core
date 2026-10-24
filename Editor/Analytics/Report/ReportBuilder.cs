using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Exerussus.AppCore.Analytics.Reports
{
    /// <summary>
    /// Сборка отчёта из пакетов AppCore Analytics (JSONL: строка-заголовок + события). Без Unity: на входе
    /// пути к файлам или папкам и каталог, на выходе <see cref="ReportData"/>.
    /// </summary>
    /// <remarks>
    /// Пакет может прийти дважды (доставлен, но подтверждение не успело) — дубли отбрасываются по id пакета,
    /// события — по (сессия, номер). Битые строки (оборванный файл) пропускаются и считаются.
    /// </remarks>
    public static class ReportBuilder
    {
        public const string Unfinished = "unfinished";

        public static ReportData Build(IEnumerable<string> sources, ReportCatalog catalog, ReportOptions options = null)
        {
            options ??= new ReportOptions();
            catalog ??= new ReportCatalog();

            var data = new ReportData
            {
                Catalog = catalog,
                Options = options,
                Generated = DateTime.UtcNow,
            };

            var files = new List<string>();
            foreach (var source in sources ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                data.Sources.Add(source);
                Collect(source, files, data);
            }

            files.Sort(StringComparer.Ordinal);
            data.Files = files.Count;

            var sessions = new Dictionary<string, ReportSession>(StringComparer.Ordinal);
            var runs = new Dictionary<string, ReportRun>(StringComparer.Ordinal);
            var packets = new HashSet<string>(StringComparer.Ordinal);
            var fields = new List<KeyValuePair<string, JsonValue>>();

            foreach (var file in files)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch (Exception e)
                {
                    data.Warnings.Add($"{file}: не читается ({e.Message})");
                    continue;
                }

                ReadPacket(lines, file, data, sessions, runs, packets, fields);
            }

            foreach (var s in sessions.Values)
            {
                s.Events.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                data.Sessions.Add(s);
            }

            data.Sessions.Sort((a, b) => Nullable.Compare(a.StartTime, b.StartTime));
            foreach (var s in data.Sessions)
            {
                s.Runs.Sort((a, b) => a.Start.CompareTo(b.Start));
                data.Runs.AddRange(s.Runs);
            }

            Digest(data);
            ApplyFilter(data, options.Filter);
            CollectFieldNames(data);
            BuildIntervals(data);
            BuildProfiles(data);
            BuildTags(data);

            if (options.Anonymize) Anonymize(data);
            return data;
        }

        // ============================================================ чтение

        private static void Collect(string source, List<string> files, ReportData data)
        {
            try
            {
                if (Directory.Exists(source))
                    files.AddRange(Directory.GetFiles(source, "*.jsonl", SearchOption.AllDirectories));
                else if (File.Exists(source))
                    files.Add(source);
                else
                    data.Warnings.Add($"{source}: нет такого файла или папки");
            }
            catch (Exception e)
            {
                data.Warnings.Add($"{source}: {e.Message}");
            }
        }

        private static void ReadPacket(string[] lines, string file, ReportData data,
                                       Dictionary<string, ReportSession> sessions, Dictionary<string, ReportRun> runs,
                                       HashSet<string> packets, List<KeyValuePair<string, JsonValue>> fields)
        {
            // в файле может быть несколько пакетов подряд (склеенный JSONL, выгрузка с сервера):
            // каждая строка-заголовок открывает новый пакет
            ReportSession session = null;
            ReportRun run = null;
            var skip = false;
            var headers = 0;

            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!JsonLine.TryParse(raw, fields))
                {
                    data.BrokenLines++;
                    continue;
                }

                if (Get(fields, "type").AsText() == "header")
                {
                    headers++;
                    var packetId = Get(fields, "packet").AsText() ?? Path.GetFileNameWithoutExtension(file) + "#" + headers;
                    if (!packets.Add(packetId))
                    {
                        data.DuplicatePackets++;
                        skip = true;
                        continue;
                    }

                    skip = false;
                    data.Packets++;
                    data.RawLines.Add(raw);
                    session = SessionOf(fields, sessions, data);
                    run = RunOf(fields, session, runs);
                    continue;
                }

                if (skip) continue;
                if (session == null)
                {
                    data.Warnings.Add($"{Path.GetFileName(file)}: события до заголовка пакета — пропущены");
                    skip = true;
                    continue;
                }

                data.RawLines.Add(raw);

                var seq = Get(fields, "s").AsNumber();
                if (double.IsNaN(seq)) continue;
                var s = (long)seq;
                if (!session.Seqs.Add(s)) continue;

                var e = new ReportEvent
                {
                    Session = session,
                    Run = run,
                    Seq = s,
                    T = Get(fields, "t").AsNumber(),
                    G = Get(fields, "g").AsNumber(),
                    Key = Get(fields, "e").AsText() ?? "?",
                };
                if (double.IsNaN(e.T)) e.T = 0;
                e.Info = data.Catalog.Find(e.Key);

                foreach (var f in fields)
                {
                    if (f.Key == "s" || f.Key == "t" || f.Key == "g" || f.Key == "e") continue;
                    e.Fields.Add(f);
                }

                session.Events.Add(e);
                run?.Events.Add(e);
                data.EventCount++;
            }
        }

        private static ReportSession SessionOf(List<KeyValuePair<string, JsonValue>> header, Dictionary<string, ReportSession> sessions, ReportData data)
        {
            var id = Get(header, "session").AsText() ?? "?";
            if (!sessions.TryGetValue(id, out var s))
            {
                s = new ReportSession
                {
                    Id = id,
                    Profile = Get(header, "profile").AsText() ?? "?",
                    StartUtc = Get(header, "sessionStartUtc").AsText(),
                    Platform = Get(header, "platform").AsText(),
                    Build = Get(header, "build").AsText(),
                    Unity = Get(header, "unity").AsText(),
                };

                if (DateTime.TryParse(s.StartUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
                    s.StartTime = start;

                sessions.Add(id, s);
                if (!string.IsNullOrEmpty(s.Build)) data.Builds.Add(s.Build);
                if (!string.IsNullOrEmpty(s.Platform)) data.Platforms.Add(s.Platform);
            }

            var schema = Get(header, "schema").AsText();
            if (!string.IsNullOrEmpty(schema))
            {
                s.Schemas.Add(schema);
                data.Schemas.Add(schema);
            }

            return s;
        }

        private static ReportRun RunOf(List<KeyValuePair<string, JsonValue>> header, ReportSession session, Dictionary<string, ReportRun> runs)
        {
            var id = Get(header, "run").AsText();
            if (string.IsNullOrEmpty(id)) return null;

            if (!runs.TryGetValue(id, out var run))
            {
                run = new ReportRun { Id = id, Session = session };
                runs.Add(id, run);
                session.Runs.Add(run);
            }

            var start = Get(header, "runStart").AsNumber();
            if (!double.IsNaN(start) && double.IsNaN(run.Start)) run.Start = start;
            return run;
        }

        private static JsonValue Get(List<KeyValuePair<string, JsonValue>> fields, string name)
        {
            foreach (var f in fields)
                if (f.Key == name) return f.Value;
            return JsonValue.Null;
        }

        private static void CollectFieldNames(ReportData data)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in data.Catalog.Events)
                foreach (var f in info.Fields)
                    if (!string.IsNullOrEmpty(f.Name) && seen.Add(f.Name)) data.FieldNames.Add(f.Name);

            foreach (var e in data.AllEvents())
                foreach (var f in e.Fields)
                    if (seen.Add(f.Key)) data.FieldNames.Add(f.Key);
        }

        // ============================================================ сессии и прогоны

        private static void Digest(ReportData data)
        {
            foreach (var s in data.Sessions)
            {
                foreach (var e in s.Events)
                {
                    var at = e.SessionTime;
                    if (at > s.Duration) s.Duration = at;

                    switch (e.Key)
                    {
                        case "app.run.begin":
                            if (e.Run != null && e.Run.Tag == null) e.Run.Tag = e.Field("tag").AsText();
                            break;

                        case "app.run.end":
                            if (e.Run != null)
                            {
                                e.Run.Finished = true;
                                e.Run.Result = e.Field("result").AsText();
                                var d = e.Field("duration").AsNumber();
                                if (!double.IsNaN(d)) e.Run.Duration = d;
                            }
                            break;

                        case "app.focus.gained":
                            var away = e.Field("away").AsNumber();
                            if (!double.IsNaN(away) && away > 0)
                            {
                                s.Away += away;
                                if (e.Run != null) e.Run.Away += away;
                            }
                            break;

                        case "app.session.end":
                            s.Ended = true;
                            break;

                        case "app.analytics.dropped":
                            var count = e.Field("count").AsNumber();
                            if (!double.IsNaN(count)) s.Dropped += (long)count;
                            break;
                    }

                    if (e.Run != null && e.Info != null && e.Info.Role == ReportRole.Mark)
                    {
                        if (e.Info.Tone == ReportTone.Positive) e.Run.Positive++;
                        else if (e.Info.Tone == ReportTone.Negative) e.Run.Negative++;
                    }
                }

                foreach (var run in s.Runs)
                {
                    var lastT = 0.0;
                    foreach (var e in run.Events)
                    {
                        if (e.T > lastT) lastT = e.T;
                        if (!double.IsNaN(e.G) && (double.IsNaN(run.GameTime) || e.G > run.GameTime)) run.GameTime = e.G;
                    }

                    if (!run.Finished)
                    {
                        run.Duration = lastT;
                        run.Result = Unfinished;
                    }
                    else if (string.IsNullOrEmpty(run.Result))
                    {
                        run.Result = "—";
                    }
                }
            }
        }

        // ============================================================ фильтр

        /// <summary>
        /// После Digest: тег прогона уже известен. Сессия не прошла — уходит целиком; прогон не прошёл — уходит
        /// с событиями. Прогоны без событий (только заголовок) тоже уходят.
        /// </summary>
        private static void ApplyFilter(ReportData data, ReportFilter filter)
        {
            filter ??= new ReportFilter();
            data.Sessions.RemoveAll(s => !filter.Accepts(s));

            foreach (var s in data.Sessions)
            {
                s.Runs.RemoveAll(r => !filter.Accepts(r) || r.Events.Count == 0);
                if (filter.Tags.Count > 0)
                {
                    // фильтр по тегу: вне выбранных прогонов события сессии не нужны
                    s.Events.RemoveAll(e => e.Run == null ? true : !s.Runs.Contains(e.Run));
                }
                else
                {
                    s.Events.RemoveAll(e => e.Run != null && !s.Runs.Contains(e.Run));
                }
            }

            data.Sessions.RemoveAll(s => s.Events.Count == 0);
            data.Runs.Clear();
            foreach (var s in data.Sessions) data.Runs.AddRange(s.Runs);

            data.EventCount = 0;
            data.Builds.Clear();
            data.Platforms.Clear();
            data.Schemas.Clear();
            foreach (var s in data.Sessions)
            {
                data.EventCount += s.Events.Count;
                if (!string.IsNullOrEmpty(s.Build)) data.Builds.Add(s.Build);
                if (!string.IsNullOrEmpty(s.Platform)) data.Platforms.Add(s.Platform);
                foreach (var schema in s.Schemas) data.Schemas.Add(schema);
            }

            if (!filter.IsEmpty) FilterRawLines(data);
        }

        /// <summary>Сырые строки для JSONL — только пакеты оставшихся сессий и прогонов.</summary>
        private static void FilterRawLines(ReportData data)
        {
            var sessions = new HashSet<string>(StringComparer.Ordinal);
            var runs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in data.Sessions) sessions.Add(s.Id);
            foreach (var r in data.Runs) runs.Add(r.Id);
            var tagFilter = data.Options.Filter.Tags.Count > 0;

            var kept = new List<string>(data.RawLines.Count);
            var fields = new List<KeyValuePair<string, JsonValue>>();
            var keep = false;
            foreach (var line in data.RawLines)
            {
                if (JsonLine.TryParse(line, fields) && Get(fields, "type").AsText() == "header")
                {
                    var session = Get(fields, "session").AsText();
                    var run = Get(fields, "run").AsText();
                    keep = session != null && sessions.Contains(session) &&
                           (string.IsNullOrEmpty(run) ? !tagFilter : runs.Contains(run));
                }

                if (keep) kept.Add(line);
            }

            data.RawLines.Clear();
            data.RawLines.AddRange(kept);
        }

        // ============================================================ интервалы

        private static void BuildIntervals(ReportData data)
        {
            var open = new Dictionary<string, ReportInterval>(StringComparer.Ordinal);

            foreach (var s in data.Sessions)
            {
                // прогоны и «вне прогона» — отдельные области: интервал не переходит через границу прогона
                ReportRun scope = null;
                open.Clear();
                var lastAxis = 0.0;

                foreach (var e in s.Events)
                {
                    if (e.Run != scope)
                    {
                        CloseAll(open, lastAxis, data);
                        scope = e.Run;
                        lastAxis = 0;
                    }

                    var axis = scope != null ? e.Axis : e.T;
                    if (axis > lastAxis) lastAxis = axis;

                    var info = e.Info;
                    if (info == null || string.IsNullOrEmpty(info.Interval)) continue;
                    if (info.Role != ReportRole.Begin && info.Role != ReportRole.End) continue;

                    var subject = string.IsNullOrEmpty(info.Subject) ? null : e.Field(info.Subject).AsText();
                    var key = info.Interval + "\u0001" + subject;

                    if (info.Role == ReportRole.Begin)
                    {
                        // повторное начало без конца — прошлое закрывается этим моментом
                        if (open.TryGetValue(key, out var previous))
                        {
                            previous.End = axis;
                            data.Intervals.Add(previous);
                        }

                        open[key] = new ReportInterval { Session = s, Run = scope, Name = info.Interval, Subject = subject, Start = axis, End = axis };
                        continue;
                    }

                    if (!open.TryGetValue(key, out var interval)) continue; // конец без начала
                    open.Remove(key);
                    interval.End = axis;
                    interval.Closed = true;
                    interval.Measure = string.IsNullOrEmpty(info.Measure) ? double.NaN : e.Field(info.Measure).AsNumber();
                    data.Intervals.Add(interval);
                }

                CloseAll(open, lastAxis, data);
            }

            data.Intervals.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(a.Session.Id, b.Session.Id);
                return c != 0 ? c : a.Start.CompareTo(b.Start);
            });
        }

        private static void CloseAll(Dictionary<string, ReportInterval> open, double end, ReportData data)
        {
            foreach (var interval in open.Values)
            {
                interval.End = Math.Max(interval.Start, end);
                data.Intervals.Add(interval);
            }

            open.Clear();
        }

        // ============================================================ профили

        private static void BuildProfiles(ReportData data)
        {
            var map = new Dictionary<string, ReportProfile>(StringComparer.Ordinal);
            foreach (var s in data.Sessions)
            {
                if (!map.TryGetValue(s.Profile, out var p))
                {
                    p = new ReportProfile { Id = s.Profile };
                    map.Add(s.Profile, p);
                    data.Profiles.Add(p);
                }

                p.Sessions++;
                p.Time += s.Duration;
                p.Away += s.Away;
                if (!string.IsNullOrEmpty(s.Platform)) p.Platform = s.Platform;
                if (!string.IsNullOrEmpty(s.Build)) p.Build = s.Build;

                if (s.StartTime != null)
                {
                    if (p.First == null || s.StartTime < p.First) p.First = s.StartTime;
                    var end = s.StartTime.Value.AddSeconds(s.Duration);
                    if (p.Last == null || end > p.Last) p.Last = end;
                }

                foreach (var run in s.Runs)
                {
                    p.Runs++;
                    p.Negative += run.Negative;
                    p.Results.TryGetValue(run.Result, out var n);
                    p.Results[run.Result] = n + 1;
                }
            }

            data.Profiles.Sort((a, b) => Nullable.Compare(b.Last, a.Last));
        }

        // ============================================================ теги: таблицы и воронки

        private static void BuildTags(ReportData data)
        {
            var map = new Dictionary<string, ReportTag>(StringComparer.Ordinal);
            foreach (var run in data.Runs)
            {
                var tag = run.TagOrDash;
                if (!map.TryGetValue(tag, out var t))
                {
                    t = new ReportTag { Tag = tag };
                    map.Add(tag, t);
                    data.Tags.Add(t);
                }

                t.Runs.Add(run);
                t.Results.TryGetValue(run.Result, out var n);
                t.Results[run.Result] = n + 1;
            }

            data.Tags.Sort((a, b) => b.Runs.Count != a.Runs.Count ? b.Runs.Count.CompareTo(a.Runs.Count) : string.CompareOrdinal(a.Tag, b.Tag));

            var intervalsByRun = new Dictionary<ReportRun, List<ReportInterval>>();
            foreach (var interval in data.Intervals)
            {
                if (interval.Run == null) continue;
                if (!intervalsByRun.TryGetValue(interval.Run, out var list)) intervalsByRun[interval.Run] = list = new List<ReportInterval>();
                list.Add(interval);
            }

            foreach (var tag in data.Tags)
            {
                foreach (var field in data.Catalog.SubjectFields())
                {
                    var table = SubjectTable(data.Catalog, field, tag, intervalsByRun);
                    if (table.Rows.Count > 0) tag.Tables.Add(table);
                }

                foreach (var name in data.Catalog.Intervals())
                {
                    var funnel = Funnel(data.Catalog, name, tag, intervalsByRun);
                    if (funnel != null && funnel.Steps.Count > 0) tag.Funnels.Add(funnel);
                }
            }
        }

        /// <summary>
        /// Сводка по полю-субъекту (module…): строка на значение, колонки — счётчики событий с этим субъектом,
        /// медиана и p90 их мер, число и длительности интервалов.
        /// </summary>
        private static ReportTable SubjectTable(ReportCatalog catalog, string field, ReportTag tag, Dictionary<ReportRun, List<ReportInterval>> intervalsByRun)
        {
            var events = new List<ReportEventInfo>();
            // метки и состояния — счётчик; конец интервала — только ради меры (число интервалов считают колонки интервала)
            foreach (var info in catalog.Events)
            {
                if (!info.HasRole || info.Subject != field || info.Role == ReportRole.Begin) continue;
                if (info.Role == ReportRole.End && string.IsNullOrEmpty(info.Measure)) continue;
                events.Add(info);
            }

            var intervals = new List<string>();
            foreach (var info in catalog.Events)
                if (info.Role == ReportRole.Begin && info.Subject == field && !string.IsNullOrEmpty(info.Interval) && !intervals.Contains(info.Interval))
                    intervals.Add(info.Interval);

            var table = new ReportTable { Id = "subjects_" + field, Title = field };
            table.Columns.Add(field);
            table.Columns.Add("прогонов");

            var shortNames = ShortNames(events);
            foreach (var info in events)
            {
                if (info.Role != ReportRole.End) table.Columns.Add(shortNames[info.Key]);
                if (!string.IsNullOrEmpty(info.Measure))
                {
                    table.Columns.Add(shortNames[info.Key] + "." + info.Measure + " медиана");
                    table.Columns.Add(shortNames[info.Key] + "." + info.Measure + " p90");
                }
            }

            foreach (var name in intervals)
            {
                table.Columns.Add(name + ": число");
                table.Columns.Add(name + ": закрыто");
                table.Columns.Add(name + ": медиана, с");
                table.Columns.Add(name + ": на прогон, с");
            }

            // значения субъекта в порядке первого появления
            var order = new List<string>();
            var runsWith = new Dictionary<string, HashSet<ReportRun>>(StringComparer.Ordinal);
            var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var measures = new Dictionary<string, Dictionary<string, List<double>>>(StringComparer.Ordinal);

            foreach (var run in tag.Runs)
            {
                foreach (var e in run.Events)
                {
                    if (e.Info == null || e.Info.Subject != field) continue;
                    var subject = e.Field(field).AsText();
                    if (subject == null) continue;

                    if (!runsWith.TryGetValue(subject, out var set))
                    {
                        runsWith[subject] = set = new HashSet<ReportRun>();
                        counts[subject] = new Dictionary<string, int>(StringComparer.Ordinal);
                        measures[subject] = new Dictionary<string, List<double>>(StringComparer.Ordinal);
                        order.Add(subject);
                    }

                    set.Add(run);
                    counts[subject].TryGetValue(e.Key, out var n);
                    counts[subject][e.Key] = n + 1;

                    if (!string.IsNullOrEmpty(e.Info.Measure))
                    {
                        var v = e.Field(e.Info.Measure).AsNumber();
                        if (!double.IsNaN(v))
                        {
                            if (!measures[subject].TryGetValue(e.Key, out var list)) measures[subject][e.Key] = list = new List<double>();
                            list.Add(v);
                        }
                    }
                }
            }

            foreach (var subject in order)
            {
                var row = new List<JsonValue> { JsonValue.Of(subject), JsonValue.Of(runsWith[subject].Count) };
                foreach (var info in events)
                {
                    if (info.Role != ReportRole.End)
                    {
                        counts[subject].TryGetValue(info.Key, out var n);
                        row.Add(JsonValue.Of(n));
                    }

                    if (!string.IsNullOrEmpty(info.Measure))
                    {
                        measures[subject].TryGetValue(info.Key, out var list);
                        row.Add(Num(Stats.Median(list)));
                        row.Add(Num(Stats.Percentile(list, 0.9)));
                    }
                }

                foreach (var name in intervals)
                {
                    var durations = new List<double>();
                    var closed = 0;
                    var runs = 0;
                    var total = 0.0;
                    foreach (var run in tag.Runs)
                    {
                        if (!intervalsByRun.TryGetValue(run, out var list)) continue;
                        var any = false;
                        foreach (var interval in list)
                        {
                            if (interval.Name != name || interval.Subject != subject) continue;
                            durations.Add(interval.Duration);
                            total += interval.Duration;
                            if (interval.Closed) closed++;
                            any = true;
                        }

                        if (any) runs++;
                    }

                    row.Add(JsonValue.Of(durations.Count));
                    row.Add(JsonValue.Of(closed));
                    row.Add(Num(Stats.Median(durations)));
                    row.Add(runs > 0 ? JsonValue.Of(Math.Round(total / runs, 3)) : JsonValue.Null);
                }

                table.Rows.Add(row.ToArray());
            }

            return table;
        }

        /// <summary>Субъекты интервала по медианному порядку первого начала в прогоне: сколько прогонов дошло и закрыло.</summary>
        private static ReportFunnel Funnel(ReportCatalog catalog, string name, ReportTag tag, Dictionary<ReportRun, List<ReportInterval>> intervalsByRun)
        {
            string field = null;
            foreach (var info in catalog.Events)
                if (info.Role == ReportRole.Begin && info.Interval == name) field = info.Subject;
            if (string.IsNullOrEmpty(field)) return null;

            var funnel = new ReportFunnel { Interval = name, SubjectField = field, Runs = tag.Runs.Count };
            var reached = new Dictionary<string, int>(StringComparer.Ordinal);
            var closed = new Dictionary<string, int>(StringComparer.Ordinal);
            var positions = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            var durations = new Dictionary<string, List<double>>(StringComparer.Ordinal);

            foreach (var run in tag.Runs)
            {
                if (!intervalsByRun.TryGetValue(run, out var list)) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var done = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;
                foreach (var interval in list)
                {
                    if (interval.Name != name || interval.Subject == null) continue;
                    if (seen.Add(interval.Subject))
                    {
                        if (!positions.TryGetValue(interval.Subject, out var p)) positions[interval.Subject] = p = new List<double>();
                        p.Add(index++);
                    }

                    if (interval.Closed && done.Add(interval.Subject))
                    {
                        if (!durations.TryGetValue(interval.Subject, out var d)) durations[interval.Subject] = d = new List<double>();
                        d.Add(interval.Duration);
                    }
                }

                foreach (var s in seen)
                {
                    reached.TryGetValue(s, out var n);
                    reached[s] = n + 1;
                }

                foreach (var s in done)
                {
                    closed.TryGetValue(s, out var n);
                    closed[s] = n + 1;
                }
            }

            var subjects = new List<string>(reached.Keys);
            subjects.Sort((a, b) =>
            {
                var c = Stats.Median(positions[a]).CompareTo(Stats.Median(positions[b]));
                return c != 0 ? c : reached[b].CompareTo(reached[a]);
            });

            foreach (var s in subjects)
            {
                closed.TryGetValue(s, out var c);
                durations.TryGetValue(s, out var d);
                funnel.Steps.Add(new ReportFunnelStep { Subject = s, Reached = reached[s], Closed = c, MedianDuration = Stats.Median(d) });
            }

            return funnel;
        }

        private static Dictionary<string, string> ShortNames(List<ReportEventInfo> events)
        {
            var last = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var info in events)
            {
                var s = LastSegment(info.Key);
                last.TryGetValue(s, out var n);
                last[s] = n + 1;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var info in events)
            {
                var s = LastSegment(info.Key);
                result[info.Key] = last[s] > 1 ? info.Key : s;
            }

            return result;
        }

        private static string LastSegment(string key)
        {
            var i = key.LastIndexOf('.');
            return i >= 0 ? key.Substring(i + 1) : key;
        }

        private static string JsonQuoted(string s)
        {
            var sb = new StringBuilder();
            JsonOut.WriteString(sb, s);
            return sb.ToString();
        }

        private static JsonValue Num(double v) => double.IsNaN(v) ? JsonValue.Null : JsonValue.Of(Math.Round(v, 3));

        // ============================================================ обезличивание

        private static void Anonymize(ReportData data)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string Hash(string id)
            {
                if (id == null) return null;
                if (map.TryGetValue(id, out var h)) return h;
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(id));
                    var sb = new StringBuilder("p_");
                    for (var i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                    h = sb.ToString();
                }

                map[id] = h;
                return h;
            }

            foreach (var s in data.Sessions) s.Profile = Hash(s.Profile);
            foreach (var p in data.Profiles) p.Id = Hash(p.Id);

            // «previous» у app.profile.new — тоже профиль
            foreach (var e in data.AllEvents())
            {
                if (e.Key != "app.profile.new") continue;
                for (var i = 0; i < e.Fields.Count; i++)
                    if (e.Fields[i].Key == "previous" && e.Fields[i].Value.Kind == JsonKind.String)
                        e.Fields[i] = new KeyValuePair<string, JsonValue>("previous", JsonValue.Of(Hash(e.Fields[i].Value.Text)));
            }

            // в сырых строках — только значения полей profile и previous, как их пишет AppCore ("ключ":"значение")
            for (var i = 0; i < data.RawLines.Count; i++)
            {
                var line = data.RawLines[i];
                foreach (var pair in map)
                {
                    if (pair.Key.Length < 3) continue;
                    var quoted = JsonQuoted(pair.Key);
                    if (line.IndexOf(quoted, StringComparison.Ordinal) < 0) continue;
                    var hashed = JsonQuoted(pair.Value);
                    line = line.Replace("\"profile\":" + quoted, "\"profile\":" + hashed)
                               .Replace("\"previous\":" + quoted, "\"previous\":" + hashed);
                }
                data.RawLines[i] = line;
            }
        }
    }

    public static class Stats
    {
        public static double Median(List<double> values) => Percentile(values, 0.5);

        /// <summary>Перцентиль с линейной интерполяцией; пусто — NaN.</summary>
        public static double Percentile(List<double> values, double p)
        {
            if (values == null || values.Count == 0) return double.NaN;
            var sorted = new List<double>(values);
            sorted.Sort();
            var pos = (sorted.Count - 1) * p;
            var lo = (int)Math.Floor(pos);
            var hi = (int)Math.Ceiling(pos);
            return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }
    }
}
