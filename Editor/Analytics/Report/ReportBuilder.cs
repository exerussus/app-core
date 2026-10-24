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
        /// <summary>Итог прогона без app.run.end (вылет, закрыли вкладку).</summary>
        public const string Unfinished = "unfinished";
        /// <summary>Итог прогона, прерванного новым BeginRun (пишет AppAnalytics).</summary>
        public const string Interrupted = "interrupted";
        /// <summary>Прогон закончен без итога (EndRun()).</summary>
        public const string NoResult = "—";

        /// <summary>Больше — испорченное время (около 30 лет в секундах).</summary>
        public const double MaxSeconds = 1e9;

        private static double Sane(double v) => double.IsNaN(v) || Math.Abs(v) < MaxSeconds ? v : double.NaN;

        public static ReportData Build(IEnumerable<string> sources, ReportCatalog catalog, ReportOptions options = null)
        {
            options ??= new ReportOptions();
            catalog ??= new ReportCatalog();
            var list = new List<string>();
            foreach (var s in sources ?? Array.Empty<string>()) list.Add(s);

            var data = BuildCore(list, catalog, options, true);

            // «сравнить с прошлым периодом»: тот же расчёт на периоде той же длины перед выбранным
            if (!options.SkipViews && NeedsPrevious(catalog, options) && options.Filter.From != null && options.Filter.To != null)
            {
                var length = options.Filter.To.Value - options.Filter.From.Value;
                var prev = Clone(options);
                prev.Filter.From = options.Filter.From.Value - length - TimeSpan.FromTicks(1);
                prev.Filter.To = options.Filter.From.Value - TimeSpan.FromTicks(1);
                var previous = BuildCore(list, catalog, prev, false);
                if (previous.Sessions.Count > 0)
                {
                    data.Previous = previous;
                    previous.PeriodFrom = prev.Filter.From;
                    previous.PeriodTo = prev.Filter.To;
                }
            }

            // профили уже обезличены в BuildCore — представления считаются по хэшам
            if (!options.SkipViews) ReportViews.Compute(data);

            return data;
        }

        private static bool NeedsPrevious(ReportCatalog catalog, ReportOptions options)
        {
            foreach (var v in (catalog.Config ?? new ReportConfig()).EffectiveViews())
                if (v.ComparePrevious && (options.Views.Count == 0 || options.Views.Contains(v.Id))) return true;
            return false;
        }

        private static ReportOptions Clone(ReportOptions o)
        {
            var c = new ReportOptions { Anonymize = o.Anonymize, Title = o.Title, TimeZone = o.TimeZone, Compare = o.Compare, Where = o.Where };
            c.Views.UnionWith(o.Views);
            c.GroupBy.AddRange(o.GroupBy);
            c.Aggs.AddRange(o.Aggs);
            c.Filter.Builds.UnionWith(o.Filter.Builds);
            c.Filter.Platforms.UnionWith(o.Filter.Platforms);
            c.Filter.Tags.UnionWith(o.Filter.Tags);
            c.Filter.Profiles.UnionWith(o.Filter.Profiles);
            return c;
        }

        private static ReportData BuildCore(List<string> sources, ReportCatalog catalog, ReportOptions options, bool main)
        {

            var data = new ReportData
            {
                Catalog = catalog,
                Options = options,
                Generated = DateTime.UtcNow,
            };

            var files = new List<string>();
            foreach (var source in sources)
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
            foreach (var s in data.Sessions)
                if (s.StartTime != null && (!data.FirstSeen.TryGetValue(s.Profile, out var f) || s.StartTime < f)) data.FirstSeen[s.Profile] = s.StartTime.Value;
            ApplyFilter(data, options.Filter);
            CollectFieldNames(data);
            BuildProfiles(data);
            BuildTags(data);
            Period(data);

            if (options.Anonymize) Anonymize(data);
            if (!main && !options.SkipViews) ReportViews.Compute(data, false);
            return data;
        }

        private static void Period(ReportData data)
        {
            foreach (var s in data.Sessions)
            {
                if (s.StartTime == null) continue;
                if (data.PeriodFrom == null || s.StartTime < data.PeriodFrom) data.PeriodFrom = s.StartTime;
                var end = ReportSession.SafeAdd(s.StartTime, s.Duration) ?? s.StartTime.Value;
                if (data.PeriodTo == null || end > data.PeriodTo) data.PeriodTo = end;
            }
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
            var props = ReportSession.NoProps;
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
                    props = PropsOf(fields, session);
                    continue;
                }

                if (skip) continue;
                if (session == null)
                {
                    data.Warnings.Add($"{Path.GetFileName(file)}: события до заголовка пакета — пропущены");
                    skip = true;
                    continue;
                }

                var seq = Get(fields, "s").AsNumber();
                if (double.IsNaN(seq) || Math.Abs(seq) > 9e15) continue;
                var s = (long)seq;
                if (!session.Seqs.Add(s)) continue; // дубль события — и в JSONL не попадает

                // испорченное время (переполнение, мусор) — событие считается битым: иначе даты отчёта не строятся
                var t = Get(fields, "t").AsNumber();
                if (!double.IsNaN(t) && !(Math.Abs(t) < MaxSeconds))
                {
                    data.BrokenLines++;
                    continue;
                }

                data.RawLines.Add(raw);

                var e = new ReportEvent
                {
                    Session = session,
                    Run = run,
                    Seq = s,
                    T = t,
                    G = Sane(Get(fields, "g").AsNumber()),
                    Key = Get(fields, "e").AsText() ?? "?",
                    Props = props,
                };
                if (double.IsNaN(e.T)) e.T = 0;
                e.Info = data.Catalog.Find(e.Key);

                foreach (var f in fields)
                {
                    if (f.Key == "s" || f.Key == "t" || f.Key == "g" || f.Key == "e") continue;
                    e.Fields.Add(f);
                }

                // свойство изменилось внутри пакета: следующие события пакета — уже с новым значением
                if (e.Key == "app.property")
                {
                    var name = e.Field("name").AsText();
                    if (!string.IsNullOrEmpty(name))
                    {
                        var value = e.Field("value").AsText();
                        var next = new Dictionary<string, string>(props, StringComparer.Ordinal);
                        if (value == null) next.Remove(name);
                        else
                        {
                            next[name] = value;
                            session.Props[name] = value;
                        }

                        props = next;
                    }
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

        /// <summary>Свойства пакета: поля заголовка «p.имя» (AppAnalytics.SetProperty). Без свойств — общий пустой словарь.</summary>
        private static Dictionary<string, string> PropsOf(List<KeyValuePair<string, JsonValue>> header, ReportSession session)
        {
            Dictionary<string, string> props = null;
            foreach (var f in header)
            {
                if (f.Key.Length < 3 || !f.Key.StartsWith("p.", StringComparison.Ordinal)) continue;
                var value = f.Value.AsText();
                if (value == null) continue;
                props ??= new Dictionary<string, string>(StringComparer.Ordinal);
                var name = f.Key.Substring(2);
                props[name] = value;
                session.Props[name] = value;
            }

            return props ?? ReportSession.NoProps;
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

            var start = Sane(Get(header, "runStart").AsNumber());
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

                        case "app.session.end":
                            s.Ended = true;
                            break;

                        case "app.analytics.dropped":
                            var count = e.Field("count").AsNumber();
                            if (!double.IsNaN(count)) s.Dropped += (long)count;
                            break;
                    }

                }

                // пропуски номеров: события между первым и последним, которых нет (потерянные пакеты)
                if (s.Events.Count > 1)
                {
                    var span = s.Events[s.Events.Count - 1].Seq - s.Events[0].Seq + 1;
                    s.Missing = Math.Max(0, span - s.Events.Count);
                }

                foreach (var run in s.Runs)
                {
                    // игровые часы: событие без g в прогоне с часами (часы сняты перед EndRun, событие до SetGameClock)
                    // получает последнее известное g — иначе ось смешивает игровое и реальное время
                    var hasGame = false;
                    foreach (var e in run.Events)
                        if (!double.IsNaN(e.G)) hasGame = true;
                    if (hasGame)
                    {
                        var lastG = double.NaN;
                        foreach (var e in run.Events)
                            if (!double.IsNaN(e.G)) { lastG = e.G; break; }
                        foreach (var e in run.Events)
                        {
                            if (double.IsNaN(e.G)) e.G = lastG;
                            else lastG = e.G;
                        }
                    }

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
                        run.Result = NoResult;
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
                foreach (var prop in s.Props) p.Props[prop.Key] = prop.Value;
                if (!string.IsNullOrEmpty(s.Platform)) p.Platform = s.Platform;
                if (!string.IsNullOrEmpty(s.Build)) p.Build = s.Build;

                if (s.StartTime != null)
                {
                    if (p.First == null || s.StartTime < p.First) p.First = s.StartTime;
                    var end = ReportSession.SafeAdd(s.StartTime, s.Duration) ?? s.StartTime.Value;
                    if (p.Last == null || end > p.Last) p.Last = end;
                }

                foreach (var run in s.Runs)
                {
                    p.Runs++;
                    p.Results.TryGetValue(run.Result, out var n);
                    p.Results[run.Result] = n + 1;
                }
            }

            data.Profiles.Sort((a, b) => Nullable.Compare(b.Last, a.Last));
        }

        // ============================================================ теги

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
            var firstSeen = new List<KeyValuePair<string, DateTime>>(data.FirstSeen);
            data.FirstSeen.Clear();
            foreach (var pair in firstSeen) data.FirstSeen[Hash(pair.Key)] = pair.Value;
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
