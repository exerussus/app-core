using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Analytics.Reports
{
    public static partial class ReportViews
    {
        private const int DefaultTop = 50;

        // ============================================================ счётчик

        private static void Counter(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var aggs = ParseAggs(ctx.Aggs, "count");
            var total = GroupAgg(ctx, scope, hits, new List<string>(), aggs);
            var units = total.Count > 0 ? total[0].Units.Count : 0;
            var headline = total.Count > 0 ? AggValue(total[0], aggs[0]) : (aggs[0].Op == "count" ? 0 : double.NaN);

            node.Set("headline", headline).Set("headlineLabel", aggs[0].Label).SetIf("headlineFmt", aggs[0].Fmt).Set("n", units);
            var totals = JsonNode.Obj();
            foreach (var a in aggs) totals.Set(a.Id, N(total.Count > 0 ? AggValue(total[0], a) : double.NaN));
            node.Set("totals", totals);

            var runs = scope.Runs.Count;
            if (runs > 0) node.Set("perRun", N((double)hits.Count / runs));
            node.Set("hits", hits.Count);

            DaySeries(ctx, hits, aggs[0], node, "series");

            if (ctx.GroupBy != null && ctx.GroupBy.Count > 0)
                GroupTable(ctx, GroupAgg(ctx, scope, hits, ctx.GroupBy, aggs), ctx.GroupBy, aggs, DefaultTop, node, tables, null, ctx.View.DisplayTitle);
            else
                GroupTable(ctx, GroupAgg(ctx, scope, hits, new List<string> { "@day" }, aggs), new List<string> { "@day" }, aggs, int.MaxValue, node, tables, null, ctx.View.DisplayTitle, true);
        }

        /// <summary>Ряд по дням: значение агрегата в каждой корзине (для спарклайнов и линий).</summary>
        private static void DaySeries(Ctx ctx, List<Hit> hits, AggSpec agg, JsonNode node, string name, string bucket = "day")
        {
            var byBucket = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
            foreach (var h in hits)
            {
                var key = BucketOf(ctx, h.E.Utc, bucket);
                if (key == null) continue;
                if (!byBucket.TryGetValue(key, out var list)) byBucket[key] = list = new List<Hit>();
                list.Add(h);
            }

            var keys = Buckets(ctx, bucket, byBucket.Keys);
            var values = new List<double>();
            var empty = new Scope { Events = new List<ReportEvent>(), Runs = new List<ReportRun>(), Sessions = new List<ReportSession>(), Profiles = new List<string>() };
            foreach (var k in keys)
            {
                if (!byBucket.TryGetValue(k, out var list))
                {
                    values.Add(agg.Op == "count" ? 0 : double.NaN);
                    continue;
                }

                var g = GroupAgg(ctx, empty, list, new List<string>(), new List<AggSpec> { agg });
                values.Add(g.Count > 0 ? AggValue(g[0], agg) : double.NaN);
            }

            node.Set(name, JsonNode.Obj().Set("x", Strings(keys)).Set("y", Numbers(values)).Set("bucket", bucket));
        }

        // ============================================================ интервал

        private static List<ReportInterval> Pair(Ctx ctx, List<Hit> hits, out Dictionary<ReportInterval, double> measures)
        {
            measures = new Dictionary<ReportInterval, double>();
            var result = new List<ReportInterval>();
            var open = new Dictionary<string, ReportInterval>(StringComparer.Ordinal);

            // области: прогон или «вне прогона» сессии — интервал не переходит через границу
            var byScope = new Dictionary<object, List<Hit>>();
            var scopes = new List<object>();
            foreach (var h in hits)
            {
                object key = (object)h.E.Run ?? h.E.Session;
                if (!byScope.TryGetValue(key, out var list))
                {
                    byScope[key] = list = new List<Hit>();
                    scopes.Add(key);
                }

                list.Add(h);
            }

            foreach (var key in scopes)
            {
                open.Clear();
                var list = byScope[key];
                list.Sort((a, b) => a.E.Session == b.E.Session ? a.E.Seq.CompareTo(b.E.Seq) : 0);
                var run = key as ReportRun;
                var real = ctx.View.RealClock;
                // вне прогона интервал без конца закрывается концом сессии
                var end = run != null ? (real ? run.RealEnd : run.AxisEnd) : ((ReportSession)key).Duration;

                foreach (var h in list)
                {
                    var axis = run != null && !real ? h.E.Axis : h.E.T;
                    if (run == null && axis > end) end = axis;
                    var subject = h.B.Subject != null ? Text(h.E.Field(h.B.Subject)) : "—";

                    if (h.B.Role == BindRole.Begin)
                    {
                        if (open.TryGetValue(subject, out var previous))
                        {
                            // повторное начало закрывает прошлый интервал (не «закрыт событием конца», но конец известен)
                            previous.End = Math.Max(previous.Start, axis);
                            previous.EndEvent = h.E;
                            result.Add(previous);
                        }

                        open[subject] = new ReportInterval
                        {
                            Session = h.E.Session, Run = run, Name = ctx.View.Id, Subject = subject, Start = axis, End = axis, BeginEvent = h.E,
                        };
                        continue;
                    }

                    if (h.B.Role != BindRole.End || !open.TryGetValue(subject, out var interval)) continue;
                    open.Remove(subject);
                    interval.End = Math.Max(interval.Start, axis);
                    interval.Closed = true;
                    interval.EndEvent = h.E;
                    if (h.B.Measure != null)
                    {
                        var m = Number(h.E.Field(h.B.Measure));
                        if (!double.IsNaN(m)) measures[interval] = m;
                    }

                    result.Add(interval);
                }

                foreach (var interval in open.Values)
                {
                    interval.End = Math.Max(interval.Start, end);
                    result.Add(interval);
                }
            }

            return result;
        }

        private static void IntervalView(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var intervals = Pair(ctx, hits, out var measures);
            var durations = new List<double>();
            var closed = 0;
            foreach (var i in intervals)
            {
                durations.Add(i.Duration);
                if (i.Closed) closed++;
            }

            var median = Stats.Median(durations);
            node.Set("headline", median).Set("headlineLabel", "Медиана длительности").Set("headlineFmt", "dur").Set("n", intervals.Count);
            node.Set("totals", JsonNode.Obj().Set("count", intervals.Count).Set("closed", closed).Set("median", N(median))
                                        .Set("p90", N(Stats.Percentile(durations, 0.9))).Set("sum", N(Sum(durations))));
            node.Set("histogram", Histogram(durations, ctx.View.Bins > 0 ? ctx.View.Bins : 20, "dur"));

            // группировка: по субъекту (по умолчанию) или измерениям события начала
            var dims = ctx.GroupBy != null && ctx.GroupBy.Count > 0 ? ctx.GroupBy : new List<string> { "subject" };
            var groups = new Dictionary<string, List<ReportInterval>>(StringComparer.Ordinal);
            var order = new List<string>();
            var keysOf = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var i in intervals)
            {
                var keys = new string[dims.Count];
                for (var k = 0; k < dims.Count; k++)
                    keys[k] = dims[k] == "subject" ? i.Subject : Dim(ctx, i.BeginEvent, dims[k].StartsWith("@", StringComparison.Ordinal) ? dims[k] : "@" + dims[k]);
                var id = string.Join("\u0001", keys);
                if (!groups.TryGetValue(id, out var list))
                {
                    groups[id] = list = new List<ReportInterval>();
                    order.Add(id);
                    keysOf[id] = keys;
                }

                list.Add(i);
            }

            var anyMeasure = measures.Count > 0;
            var columns = JsonNode.Arr();
            foreach (var d in dims) columns.Add(Column(Slug(d), ctx.DimTitle(d)));
            columns.Add(Column("count", "Заходов")).Add(Column("closed", "Закрыто")).Add(Column("runs", "Прогонов"))
                   .Add(Column("median", "Медиана", "dur")).Add(Column("p90", "p90", "dur")).Add(Column("per_run", "На прогон", "dur"));
            if (anyMeasure) columns.Add(Column("measure_median", "Мера, медиана"));

            var rows = new List<KeyValuePair<double, JsonNode>>();
            foreach (var id in order)
            {
                var list = groups[id];
                var d = new List<double>();
                var c = 0;
                var runs = new HashSet<ReportRun>();
                var m = new List<double>();
                var total = 0.0;
                foreach (var i in list)
                {
                    d.Add(i.Duration);
                    total += i.Duration;
                    if (i.Closed) c++;
                    if (i.Run != null) runs.Add(i.Run);
                    if (measures.TryGetValue(i, out var mv)) m.Add(mv);
                }

                var row = JsonNode.Arr();
                foreach (var k in keysOf[id]) row.Add(JsonNode.Of(k));
                row.Add(JsonNode.Of(list.Count)).Add(JsonNode.Of(c)).Add(JsonNode.Of(runs.Count))
                   .Add(N(Stats.Median(d))).Add(N(Stats.Percentile(d, 0.9))).Add(runs.Count > 0 ? N(total / runs.Count) : new JsonNode());
                if (anyMeasure) row.Add(N(Stats.Median(m)));
                rows.Add(new KeyValuePair<double, JsonNode>(total, row));
            }

            rows.Sort((a, b) => b.Key.CompareTo(a.Key));
            var arr = JsonNode.Arr();
            var low = JsonNode.Arr();
            foreach (var r in rows)
            {
                arr.Add(r.Value);
                low.Add(JsonNode.Of(r.Value.Items[dims.Count].Number < ctx.MinSample));
            }

            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", arr).Set("low", low).Set("dims", dims.Count).Set("total", rows.Count));
            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, arr));

            // доля времени области под интервалом (по прогонам с осью)
            var runTime = 0.0;
            foreach (var r in scope.Runs) runTime += ctx.View.RealClock ? r.RealEnd : r.AxisEnd;
            if (runTime > 0) node.Set("timeShare", N(Math.Min(1, Sum(durations) / runTime)));

            if (scope.Tag == null)
            {
                // сами интервалы — для экспорта (CSV intervals_<id>)
                var it = new ReportTable { Id = "view_" + ctx.View.Id + "_intervals", Title = ctx.View.DisplayTitle + ": интервалы" };
                foreach (var c in new[] { "profile", "session", "run", "tag", "subject", "start", "end", "duration", "closed", "measure" }) it.Add(c, c);
                foreach (var i in intervals)
                    it.Rows.Add(new[]
                    {
                        JsonValue.Of(i.Session.Profile), JsonValue.Of(i.Session.Id), JsonValue.Of(i.Run?.Id), JsonValue.Of(i.Run?.TagOrDash),
                        JsonValue.Of(i.Subject), V(i.Start), V(i.End), V(i.Duration), JsonValue.Of(i.Closed),
                        measures.TryGetValue(i, out var mv) ? V(mv) : JsonValue.Null,
                    });
                tables.Add(it);
            }
        }

        private static double Sum(List<double> values)
        {
            var s = 0.0;
            foreach (var v in values) s += v;
            return s;
        }

        // ============================================================ воронка и прогресс

        private static void Funnel(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables, bool progress)
        {
            var unit = ctx.Unit;
            var steps = new Dictionary<object, List<KeyValuePair<double, string>>>();
            var done = new Dictionary<object, HashSet<string>>();
            foreach (var h in hits)
            {
                var u = UnitOf(ctx, h.E, unit);
                if (u == null || h.B.Subject == null) continue;
                var step = Text(h.E.Field(h.B.Subject));
                if (h.B.Role == BindRole.Done)
                {
                    if (!done.TryGetValue(u, out var set)) done[u] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(step);
                    continue;
                }

                if (!steps.TryGetValue(u, out var list)) steps[u] = list = new List<KeyValuePair<double, string>>();
                list.Add(new KeyValuePair<double, string>(h.E.Absolute, step));
            }

            // порядок шагов: заданный или по медиане позиции первого появления
            var order = new List<string>(ctx.View.Steps);
            var positions = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            var firstTime = new Dictionary<object, Dictionary<string, double>>();
            foreach (var pair in steps)
            {
                pair.Value.Sort((a, b) => a.Key.CompareTo(b.Key));
                var seen = new Dictionary<string, double>(StringComparer.Ordinal);
                var index = 0;
                foreach (var s in pair.Value)
                {
                    if (seen.ContainsKey(s.Value)) continue;
                    seen[s.Value] = s.Key;
                    if (!positions.TryGetValue(s.Value, out var p)) positions[s.Value] = p = new List<double>();
                    p.Add(index++);
                }

                firstTime[pair.Key] = seen;
            }

            if (order.Count == 0)
            {
                order.AddRange(positions.Keys);
                order.Sort((a, b) =>
                {
                    var c = Stats.Median(positions[a]).CompareTo(Stats.Median(positions[b]));
                    return c != 0 ? c : positions[b].Count.CompareTo(positions[a].Count);
                });
            }

            var population = Population(scope, unit).Count;
            var reached = new int[order.Count];
            var closed = new int[order.Count];
            var stuck = new int[order.Count];
            var times = new List<double>[order.Count];
            for (var i = 0; i < order.Count; i++) times[i] = new List<double>();

            var started = 0;
            foreach (var pair in steps)
            {
                if (progress)
                {
                    // прогресс: самый дальний шаг из списка — значит, «прошёл» все до него
                    var max = -1;
                    for (var i = 0; i < order.Count; i++)
                        if (firstTime[pair.Key].ContainsKey(order[i])) max = i;
                    if (max < 0) continue;
                    started++;
                    for (var i = 0; i <= max; i++) reached[i]++;
                    stuck[max]++;
                    continue;
                }

                // воронка — по порядку: шаг засчитан, только если пройдены все до него и он случился не раньше прошлого
                var list = pair.Value; // отсортирован по времени
                double origin = double.NaN, prev = double.NegativeInfinity;
                for (var i = 0; i < order.Count; i++)
                {
                    var t = double.NaN;
                    foreach (var s in list)
                        if (s.Value == order[i] && s.Key >= prev)
                        {
                            t = s.Key;
                            break;
                        }

                    if (double.IsNaN(t)) break;
                    if (i == 0)
                    {
                        origin = t;
                        started++;
                    }

                    prev = t;
                    reached[i]++;
                    times[i].Add(t - origin);
                    if (done.TryGetValue(pair.Key, out var set) && set.Contains(order[i])) closed[i]++;
                }
            }

            var columns = JsonNode.Arr().Add(Column("step", "Шаг")).Add(Column("index", "№")).Add(Column("reached", "Дошли"))
                                    .Add(Column("rate", "От начавших", "pct")).Add(Column("drop", "Отвал от прошлого", "pct"));
            if (progress) columns.Add(Column("stuck", "Остановились"));
            else columns.Add(Column("done", "Прошли")).Add(Column("time", "От первого шага, медиана", "dur"));

            var rows = JsonNode.Arr();
            for (var i = 0; i < order.Count; i++)
            {
                var prev = i == 0 ? started : reached[i - 1];
                var row = JsonNode.Arr().Add(JsonNode.Of(order[i])).Add(JsonNode.Of(i + 1)).Add(JsonNode.Of(reached[i]))
                                  .Add(N(started > 0 ? (double)reached[i] / started : double.NaN))
                                  .Add(N(prev > 0 ? 1 - (double)reached[i] / prev : double.NaN));
                if (progress) row.Add(JsonNode.Of(stuck[i]));
                else row.Add(JsonNode.Of(closed[i])).Add(N(Stats.Median(times[i])));
                rows.Add(row);
            }

            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", rows).Set("dims", 1).Set("total", order.Count));
            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));

            node.Set("n", started).Set("population", population).Set("started", started);
            if (progress)
            {
                // медианный достигнутый шаг
                var maxes = new List<double>();
                foreach (var pair in firstTime)
                {
                    var mx = -1;
                    for (var i = 0; i < order.Count; i++)
                        if (pair.Value.ContainsKey(order[i])) mx = i;
                    if (mx >= 0) maxes.Add(mx);
                }

                var med = Stats.Median(maxes);
                node.Set("headline", double.IsNaN(med) ? double.NaN : med + 1).Set("headlineLabel", "Медианный шаг")
                    .SetIf("headlineText", double.IsNaN(med) ? null : order[(int)Math.Round(med)]);
            }
            else
            {
                var last = order.Count > 0 ? reached[order.Count - 1] : 0;
                node.Set("headline", started > 0 ? (double)last / started : double.NaN).Set("headlineLabel", "Дошли до последнего шага").Set("headlineFmt", "pct");
            }

            node.Set("totals", JsonNode.Obj().Set("started", started).Set("population", population)
                                        .Set("share_started", N(population > 0 ? (double)started / population : double.NaN)));
        }

        // ============================================================ разбивка

        private static void Breakdown(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var dims = DefaultDims(ctx, hits);
            var aggs = ParseAggs(ctx.Aggs, AnyMeasure(hits) ? new[] { "count", "sum:measure", "median:measure" } : new[] { "count" });
            var groups = GroupAgg(ctx, scope, hits, dims, aggs);
            var top = ctx.View.Top > 0 ? ctx.View.Top : DefaultTop;
            GroupTable(ctx, groups, dims, aggs, top, node, tables, null, ctx.View.DisplayTitle);

            var units = new HashSet<object>();
            foreach (var g in groups)
                foreach (var u in g.Units) units.Add(u);
            node.Set("n", units.Count).Set("headline", groups.Count).Set("headlineLabel", "Групп");
        }

        // ============================================================ распределение

        private static void Distribution(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var values = UnitValues(ctx, hits, "measure");
            DistributionNode(values, ctx.View.Bins > 0 ? ctx.View.Bins : 20, node);
            node.Set("n", values.Count).Set("headline", Stats.Median(values)).Set("headlineLabel", "Медиана");

            var columns = JsonNode.Arr().Add(Column("from", "От")).Add(Column("to", "До")).Add(Column("count", "Количество"));
            var rows = JsonNode.Arr();
            var hist = node["histogram"];
            if (hist != null)
            {
                var edges = hist["edges"].Items;
                var counts = hist["counts"].Items;
                for (var i = 0; i < counts.Count; i++) rows.Add(Row(edges[i], edges[i + 1], counts[i]));
            }

            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }

        /// <summary>Значения поля: по событиям (единица — события) или суммы внутри прогона/сессии/игрока.</summary>
        private static List<double> UnitValues(Ctx ctx, List<Hit> hits, string field)
        {
            var sums = new Dictionary<object, double>();
            var order = new List<object>();
            foreach (var h in hits)
            {
                var u = UnitOf(ctx, h.E, ctx.Unit);
                if (u == null) continue;
                var v = Number(Resolve(ctx, h, field));
                if (double.IsNaN(v)) continue;
                if (!sums.ContainsKey(u))
                {
                    sums[u] = 0;
                    order.Add(u);
                }

                sums[u] += v;
            }

            var list = new List<double>(order.Count);
            foreach (var u in order) list.Add(sums[u]);
            return list;
        }

        private static void DistributionNode(List<double> values, int bins, JsonNode node)
        {
            var stats = JsonNode.Obj().Set("n", values.Count);
            if (values.Count > 0)
            {
                var sum = 0.0;
                foreach (var v in values) sum += v;
                stats.Set("mean", N(sum / values.Count));
                foreach (var p in new[] { 0.0, 0.1, 0.25, 0.5, 0.75, 0.9, 0.99, 1.0 })
                    stats.Set(p == 0 ? "min" : p == 1 ? "max" : "p" + (int)Math.Round(p * 100), N(Stats.Percentile(values, p)));
            }

            node.Set("stats", stats);
            node.Set("totals", stats);
            node.Set("histogram", Histogram(values, bins, null));
        }

        private static JsonNode Histogram(List<double> values, int bins, string fmt)
        {
            var node = JsonNode.Obj().SetIf("fmt", fmt);
            if (values.Count == 0)
            {
                node.Set("edges", JsonNode.Arr()).Set("counts", JsonNode.Arr());
                return node;
            }

            bins = Math.Max(1, Math.Min(100, bins));
            var min = double.PositiveInfinity;
            var max = double.NegativeInfinity;
            foreach (var v in values)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            // длинный хвост не сплющивает гистограмму: верх — p99, выше — в последнюю корзину
            var hi = Stats.Percentile(values, 0.99);
            if (hi > min) max = hi;
            if (max <= min) max = min + 1;
            var width = (max - min) / bins;
            var counts = new int[bins];
            foreach (var v in values)
            {
                var i = (int)Math.Floor((v - min) / width);
                counts[Math.Max(0, Math.Min(bins - 1, i))]++;
            }

            var edges = JsonNode.Arr();
            for (var i = 0; i <= bins; i++) edges.Add(N(min + width * i));
            var arr = JsonNode.Arr();
            foreach (var c in counts) arr.Add(JsonNode.Of(c));
            return node.Set("edges", edges).Set("counts", arr);
        }

        // ============================================================ исход

        private static void Outcome(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var view = ctx.View;
            var classes = new Dictionary<string, OutcomeClass>(StringComparer.Ordinal);
            foreach (var o in view.Outcomes)
                if (o.Value != null) classes[o.Value] = o;
            if (view.Value == "@result")
            {
                // итоги, которые пишет сам AppCore, — служебные, даже если проект их не перечислил
                var core = ViewTypes.BuiltinRunsView();
                foreach (var o in core.Outcomes)
                    if (!classes.ContainsKey(o.Value)) classes[o.Value] = o;
            }

            // значение на единицу: измерение (без привязок) или последнее значение привязки в единице
            var values = new List<KeyValuePair<object, string>>();
            if (!string.IsNullOrEmpty(view.Value) && view.Value.StartsWith("@", StringComparison.Ordinal))
            {
                foreach (var u in Population(scope, ctx.Unit == CountUnit.Events ? CountUnit.Runs : ctx.Unit))
                    values.Add(new KeyValuePair<object, string>(u, DimOfUnit(ctx, u, view.Value)));
            }
            else
            {
                var last = new Dictionary<object, string>();
                var order = new List<object>();
                foreach (var h in hits)
                {
                    var u = UnitOf(ctx, h.E, ctx.Unit);
                    if (u == null) continue;
                    var v = h.B.Value != null ? Text(h.E.Field(h.B.Value)) : (view.Value != null ? Text(Resolve(ctx, h, view.Value)) : "—");
                    if (!last.ContainsKey(u)) order.Add(u);
                    last[u] = v;
                }

                foreach (var u in order) values.Add(new KeyValuePair<object, string>(u, last[u]));
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in values)
            {
                counts.TryGetValue(p.Value, out var c);
                counts[p.Value] = c + 1;
            }

            // порядок классов: из каталога, затем по частоте
            var keys = new List<string>();
            foreach (var o in view.Outcomes)
                if (o.Value != null && counts.ContainsKey(o.Value) && !keys.Contains(o.Value)) keys.Add(o.Value);
            var rest = new List<string>();
            foreach (var k in counts.Keys)
                if (!keys.Contains(k)) rest.Add(k);
            rest.Sort((a, b) =>
            {
                var ga = classes.TryGetValue(a, out var ca) && ca.Tone == Tone.Gray;
                var gb = classes.TryGetValue(b, out var cb) && cb.Tone == Tone.Gray;
                if (ga != gb) return ga ? 1 : -1;
                return counts[b].CompareTo(counts[a]);
            });
            keys.AddRange(rest);

            var total = values.Count;
            int pos = 0, neg = 0;
            var classArr = JsonNode.Arr();
            var columns = JsonNode.Arr().Add(Column("value", "Значение")).Add(Column("label", "Подпись")).Add(Column("tone", "Окраска"))
                                    .Add(Column("count", "Количество")).Add(Column("share", "Доля", "pct"));
            var rows = JsonNode.Arr();
            foreach (var k in keys)
            {
                classes.TryGetValue(k, out var oc);
                var tone = oc?.Tone ?? Tone.Neutral;
                if (tone == Tone.Positive) pos += counts[k];
                if (tone == Tone.Negative) neg += counts[k];
                var label = string.IsNullOrWhiteSpace(oc?.Label) ? k : oc.Label;
                classArr.Add(JsonNode.Obj().Set("value", k).Set("label", label).Set("tone", ViewTypes.Name(tone)).Set("count", counts[k]));
                rows.Add(Row(JsonNode.Of(k), JsonNode.Of(label), JsonNode.Of(ViewTypes.Name(tone)), JsonNode.Of(counts[k]), N(total > 0 ? (double)counts[k] / total : double.NaN)));
            }

            node.Set("classes", classArr).Set("n", total);
            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", rows).Set("dims", 1).Set("total", keys.Count));
            tables.Add(TableOf(ctx, null, view.DisplayTitle, columns, rows));

            if (pos + neg > 0)
                node.Set("headline", (double)pos / (pos + neg)).Set("headlineLabel", "Успех среди исходов «успех/провал»").Set("headlineFmt", "pct");
            else
            {
                var topKey = keys.Count > 0 ? keys[0] : null;
                node.Set("headline", topKey != null && total > 0 ? (double)counts[topKey] / total : double.NaN).Set("headlineFmt", "pct")
                    .Set("headlineLabel", topKey != null ? "Доля «" + (classes.TryGetValue(topKey, out var oc) && !string.IsNullOrWhiteSpace(oc.Label) ? oc.Label : topKey) + "»" : "Доля");
            }

            var totals = JsonNode.Obj().Set("positive", pos).Set("negative", neg).Set("total", total);
            node.Set("totals", totals);

            // по группам (например, @tag) и по дням — стопки классов
            var dims = ctx.GroupBy ?? new List<string>();
            if (dims.Count > 0) node.Set("groups", OutcomeSplit(ctx, values, dims[0], keys));
            node.Set("byDay", OutcomeSplit(ctx, values, "@day", keys, true));
        }

        private static JsonNode OutcomeSplit(Ctx ctx, List<KeyValuePair<object, string>> values, string dim, List<string> keys, bool time = false)
        {
            var map = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var p in values)
            {
                var g = DimOfUnit(ctx, p.Key, dim);
                if (!map.TryGetValue(g, out var c)) map[g] = c = new Dictionary<string, int>(StringComparer.Ordinal);
                c.TryGetValue(p.Value, out var n);
                c[p.Value] = n + 1;
            }

            var groups = time ? Buckets(ctx, "day", map.Keys) : new List<string>(map.Keys);
            if (!time)
                groups.Sort((a, b) =>
                {
                    var sa = 0;
                    var sb = 0;
                    foreach (var v in map[a].Values) sa += v;
                    foreach (var v in map[b].Values) sb += v;
                    return sb.CompareTo(sa);
                });

            var arr = JsonNode.Arr();
            foreach (var g in groups)
            {
                var counts = JsonNode.Arr();
                map.TryGetValue(g, out var c);
                foreach (var k in keys) counts.Add(JsonNode.Of(c != null && c.TryGetValue(k, out var n) ? n : 0));
                arr.Add(JsonNode.Obj().Set("group", g).Set("counts", counts));
            }

            return JsonNode.Obj().Set("dim", dim).Set("title", ctx.DimTitle(dim)).Set("items", arr);
        }

        // ============================================================ дорожка

        private static void Lane(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            int pos = 0, neg = 0;
            foreach (var h in hits)
            {
                if (h.B.Tone == Tone.Positive) pos++;
                else if (h.B.Tone == Tone.Negative) neg++;
            }

            node.Set("n", hits.Count).Set("headline", scope.Runs.Count > 0 ? (double)hits.Count / scope.Runs.Count : double.NaN).Set("headlineLabel", "Меток на прогон");
            node.Set("totals", JsonNode.Obj().Set("positive", pos).Set("negative", neg).Set("count", hits.Count));
            var dims = DefaultDims(ctx, hits);
            var aggs = new List<AggSpec> { AggSpec.Parse("count") };
            GroupTable(ctx, GroupAgg(ctx, scope, hits, dims, aggs), dims, aggs, DefaultTop, node, tables, null, ctx.View.DisplayTitle);
        }

        // ============================================================ доля

        private static void Share(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            if (ctx.Unit == CountUnit.Events) throw new InvalidOperationException("доля считается по прогонам, сессиям или игрокам — единица «события» не подходит");
            var population = Population(scope, ctx.Unit);
            var with = new HashSet<object>();
            foreach (var h in hits)
            {
                var u = UnitOf(ctx, h.E, ctx.Unit);
                if (u != null) with.Add(u);
            }

            var share = population.Count > 0 ? (double)with.Count / population.Count : double.NaN;
            node.Set("headline", share).Set("headlineLabel", "Доля " + ViewTypes.Genitive(ctx.Unit) + " с событием").Set("headlineFmt", "pct")
                .Set("n", population.Count).Set("totals", JsonNode.Obj().Set("with", with.Count).Set("population", population.Count).Set("share", N(share)));

            // тренд: доля по дням начала единицы
            var byDay = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var u in population)
            {
                var t = UnitTime(u);
                if (t == null) continue;
                var d = ctx.Time.Day(t.Value);
                if (!byDay.TryGetValue(d, out var c)) byDay[d] = c = new int[2];
                c[1]++;
                if (with.Contains(u)) c[0]++;
            }

            var days = Buckets(ctx, "day", byDay.Keys);
            var y = new List<double>();
            foreach (var d in days) y.Add(byDay.TryGetValue(d, out var c) && c[1] > 0 ? (double)c[0] / c[1] : double.NaN);
            node.Set("series", JsonNode.Obj().Set("x", Strings(days)).Set("y", Numbers(y)).Set("bucket", "day").Set("fmt", "pct"));

            var dims = ctx.GroupBy != null && ctx.GroupBy.Count > 0 ? ctx.GroupBy : new List<string>();
            if (dims.Count > 0)
            {
                var aggs = new List<AggSpec> { AggSpec.Parse("share"), AggSpec.Parse("count") };
                GroupTable(ctx, GroupAgg(ctx, scope, hits, dims, aggs), dims, aggs, DefaultTop, node, tables, null, ctx.View.DisplayTitle);
            }
            else
            {
                var columns = JsonNode.Arr().Add(Column("day", "День")).Add(Column("share", "Доля", "pct")).Add(Column("with", "С событием")).Add(Column("total", "Всего"));
                var rows = JsonNode.Arr();
                foreach (var d in days)
                {
                    byDay.TryGetValue(d, out var c);
                    rows.Add(Row(JsonNode.Of(d), N(c != null && c[1] > 0 ? (double)c[0] / c[1] : double.NaN), JsonNode.Of(c?[0] ?? 0), JsonNode.Of(c?[1] ?? 0)));
                }

                tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
            }
        }

        // ============================================================ конверсия

        private static void Conversion(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            if (ctx.Unit == CountUnit.Events) throw new InvalidOperationException("конверсия считается по прогонам, сессиям или игрокам — единица «события» не подходит");
            var a = new Dictionary<object, double>();
            var bs = new Dictionary<object, List<double>>();
            foreach (var h in hits)
            {
                var u = UnitOf(ctx, h.E, ctx.Unit);
                if (u == null) continue;
                var t = h.E.Absolute;
                if (h.B.Role == BindRole.A)
                {
                    if (!a.TryGetValue(u, out var first) || t < first) a[u] = t;
                }
                else if (h.B.Role == BindRole.B)
                {
                    if (!bs.TryGetValue(u, out var list)) bs[u] = list = new List<double>();
                    list.Add(t);
                }
            }

            var within = ctx.View.Within;
            var converted = new HashSet<object>();
            var times = new List<double>();
            foreach (var pair in a)
            {
                if (!bs.TryGetValue(pair.Key, out var list)) continue;
                var best = double.PositiveInfinity;
                foreach (var t in list)
                    if (t >= pair.Value - 1e-6 && t < best) best = t;
                if (double.IsInfinity(best)) continue;
                var dt = best - pair.Value;
                if (within > 0 && dt > within) continue;
                converted.Add(pair.Key);
                times.Add(dt);
            }

            var rate = a.Count > 0 ? (double)converted.Count / a.Count : double.NaN;
            node.Set("headline", rate).Set("headlineLabel", "Дошли от A до B").Set("headlineFmt", "pct").Set("n", a.Count);
            node.Set("totals", JsonNode.Obj().Set("a", a.Count).Set("b", converted.Count).Set("rate", N(rate))
                                        .Set("time_median", N(Stats.Median(times))).Set("time_p90", N(Stats.Percentile(times, 0.9))));
            node.Set("histogram", Histogram(times, 20, "dur"));

            var dims = ctx.GroupBy != null && ctx.GroupBy.Count > 0 ? ctx.GroupBy : new List<string>();
            var columns = JsonNode.Arr();
            foreach (var d in dims) columns.Add(Column(Slug(d), ctx.DimTitle(d)));
            columns.Add(Column("a", "A")).Add(Column("b", "Дошли до B")).Add(Column("rate", "Конверсия", "pct")).Add(Column("time", "Время до B, медиана", "dur"));
            var groups = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var u in a.Keys)
            {
                var keys = new string[dims.Count];
                for (var i = 0; i < dims.Count; i++) keys[i] = DimOfUnit(ctx, u, dims[i]);
                var id = string.Join("\u0001", keys);
                if (!groups.TryGetValue(id, out var list))
                {
                    groups[id] = list = new List<object>();
                    order.Add(id);
                }

                list.Add(u);
            }

            var rows = JsonNode.Arr();
            var low = JsonNode.Arr();
            foreach (var id in order)
            {
                var list = groups[id];
                var c = 0;
                var t = new List<double>();
                foreach (var u in list)
                {
                    if (!converted.Contains(u)) continue;
                    c++;
                    var first = a[u];
                    var best = double.PositiveInfinity;
                    foreach (var bt in bs[u])
                        if (bt >= first - 1e-6 && bt < best) best = bt;
                    t.Add(best - first);
                }

                var row = JsonNode.Arr();
                if (dims.Count > 0)
                    foreach (var k in id.Split('\u0001')) row.Add(JsonNode.Of(k));
                row.Add(JsonNode.Of(list.Count)).Add(JsonNode.Of(c)).Add(N((double)c / list.Count)).Add(N(Stats.Median(t)));
                rows.Add(row);
                low.Add(JsonNode.Of(list.Count < ctx.MinSample));
            }

            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", rows).Set("low", low).Set("dims", dims.Count).Set("total", order.Count));
            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }

        // ============================================================ временной ряд

        private static void Series(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var bucket = string.IsNullOrEmpty(ctx.View.Bucket) ? "day" : ctx.View.Bucket;
            var aggs = ParseAggs(ctx.Aggs, "count");
            var agg = aggs[0];
            DaySeries(ctx, hits, agg, node, "series", bucket);
            node["series"].Set("label", agg.Label).SetIf("fmt", agg.Fmt);

            var y = node["series"]["y"].Items;
            var sum = 0.0;
            var count = 0;
            foreach (var v in y)
                if (v.Kind == JsonNodeKind.Number)
                {
                    sum += v.Number;
                    count++;
                }

            var units = new HashSet<object>();
            foreach (var h in hits)
            {
                var u = UnitOf(ctx, h.E, ctx.Unit);
                if (u != null) units.Add(u);
            }

            node.Set("n", units.Count).Set("headline", count > 0 ? sum / count : double.NaN).Set("headlineLabel", "В среднем за " + (bucket == "hour" ? "час" : bucket == "week" ? "неделю" : "день"));
            node.Set("totals", JsonNode.Obj().Set("avg", N(count > 0 ? sum / count : double.NaN)).Set("sum", N(sum)));

            var columns = JsonNode.Arr().Add(Column("bucket", "Период"));
            foreach (var a in aggs) columns.Add(Column(a.Id, a.Label, a.Fmt));
            var rows = JsonNode.Arr();
            var xs = node["series"]["x"].Items;
            var perBucket = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
            foreach (var h in hits)
            {
                var k = BucketOf(ctx, h.E.Utc, bucket);
                if (k == null) continue;
                if (!perBucket.TryGetValue(k, out var l)) perBucket[k] = l = new List<Hit>();
                l.Add(h);
            }

            var empty = new Scope { Events = new List<ReportEvent>(), Runs = new List<ReportRun>(), Sessions = new List<ReportSession>(), Profiles = new List<string>() };
            foreach (var x in xs)
            {
                var row = JsonNode.Arr().Add(JsonNode.Of(x.Text));
                perBucket.TryGetValue(x.Text, out var l);
                var g = l != null ? GroupAgg(ctx, empty, l, new List<string>(), aggs) : new List<Group>();
                foreach (var a in aggs) row.Add(g.Count > 0 ? N(AggValue(g[0], a)) : (a.Op == "count" ? JsonNode.Of(0) : new JsonNode()));
                rows.Add(row);
            }

            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }

        // ============================================================ активность

        private static void Activity(Ctx ctx, Scope scope, JsonNode node, List<ReportTable> tables)
        {
            var byDay = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var sessionsByDay = new Dictionary<string, int>(StringComparer.Ordinal);
            var lengthByDay = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            foreach (var s in scope.Sessions)
            {
                if (s.StartTime == null) continue;
                var d = ctx.Time.Day(s.StartTime.Value);
                if (!byDay.TryGetValue(d, out var set)) byDay[d] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(s.Profile);
                sessionsByDay.TryGetValue(d, out var n);
                sessionsByDay[d] = n + 1;
                if (!lengthByDay.TryGetValue(d, out var list)) lengthByDay[d] = list = new List<double>();
                list.Add(s.Duration);
            }

            var days = Buckets(ctx, "day", byDay.Keys);
            var dau = new List<double>();
            var wau = new List<double>();
            var mau = new List<double>();
            var spp = new List<double>();
            var len = new List<double>();
            for (var i = 0; i < days.Count; i++)
            {
                byDay.TryGetValue(days[i], out var today);
                dau.Add(today?.Count ?? 0);
                wau.Add(Window(byDay, days, i, 7));
                mau.Add(Window(byDay, days, i, 30));
                sessionsByDay.TryGetValue(days[i], out var sc);
                spp.Add(today != null && today.Count > 0 ? (double)sc / today.Count : double.NaN);
                lengthByDay.TryGetValue(days[i], out var l);
                len.Add(Stats.Median(l));
            }

            var avgDau = Avg(dau);
            var stickiness = mau.Count > 0 && mau[mau.Count - 1] > 0 ? dau[dau.Count - 1] / mau[mau.Count - 1] : double.NaN;
            var allLengths = new List<double>();
            foreach (var s in scope.Sessions) allLengths.Add(s.Duration);

            node.Set("n", scope.Profiles.Count).Set("headline", avgDau).Set("headlineLabel", "DAU в среднем");
            node.Set("totals", JsonNode.Obj().Set("dau", N(avgDau)).Set("wau", N(wau.Count > 0 ? wau[wau.Count - 1] : double.NaN))
                                        .Set("mau", N(mau.Count > 0 ? mau[mau.Count - 1] : double.NaN)).Set("stickiness", N(stickiness))
                                        .Set("profiles", scope.Profiles.Count).Set("sessions", scope.Sessions.Count)
                                        .Set("sessions_per_profile", N(scope.Profiles.Count > 0 ? (double)scope.Sessions.Count / scope.Profiles.Count : double.NaN))
                                        .Set("session_median", N(Stats.Median(allLengths))));
            node.Set("series", JsonNode.Obj().Set("x", Strings(days)).Set("dau", Numbers(dau)).Set("wau", Numbers(wau)).Set("mau", Numbers(mau))
                                        .Set("sessionsPerProfile", Numbers(spp)).Set("sessionMedian", Numbers(len)));

            var columns = JsonNode.Arr().Add(Column("day", "День")).Add(Column("dau", "DAU")).Add(Column("wau", "WAU")).Add(Column("mau", "MAU"))
                                    .Add(Column("sessions_per_profile", "Сессий на игрока")).Add(Column("session_median", "Длина сессии, медиана", "dur"));
            var rows = JsonNode.Arr();
            for (var i = 0; i < days.Count; i++) rows.Add(Row(JsonNode.Of(days[i]), N(dau[i]), N(wau[i]), N(mau[i]), N(spp[i]), N(len[i])));
            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", rows).Set("dims", 1).Set("total", days.Count));
            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }

        private static double Window(Dictionary<string, HashSet<string>> byDay, List<string> days, int end, int size)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = Math.Max(0, end - size + 1); i <= end; i++)
                if (byDay.TryGetValue(days[i], out var s)) set.UnionWith(s);
            return set.Count;
        }

        private static double Avg(List<double> values)
        {
            var s = 0.0;
            var n = 0;
            foreach (var v in values)
                if (!double.IsNaN(v))
                {
                    s += v;
                    n++;
                }

            return n > 0 ? s / n : double.NaN;
        }

        // ============================================================ удержание

        private static void Retention(Ctx ctx, Scope scope, JsonNode node, List<ReportTable> tables)
        {
            var periods = ctx.View.Periods.Count > 0 ? ctx.View.Periods : new List<int> { 1, 3, 7, 14, 30 };
            var days = new Dictionary<string, SortedSet<DateTime>>(StringComparer.Ordinal);
            var lastDay = DateTime.MinValue;
            foreach (var s in ctx.Data.Sessions)
            {
                if (s.StartTime == null) continue;
                var d = ctx.Time.LocalDate(s.StartTime.Value);
                if (!days.TryGetValue(s.Profile, out var set)) days[s.Profile] = set = new SortedSet<DateTime>();
                set.Add(d);
                if (d > lastDay) lastDay = d;
            }

            // когорта — день первой сессии игрока в данных; область (тег) ограничивает игроков, не их дни
            var cohorts = new SortedDictionary<DateTime, List<string>>();
            var skipped = 0;
            foreach (var p in scope.Profiles)
            {
                if (!days.TryGetValue(p, out var set) || set.Count == 0) continue;
                // когорта — день первого появления во всех данных (до фильтра): пришедший раньше периода — не новичок
                var first = set.Min;
                if (ctx.Data.FirstSeen.TryGetValue(p, out var seen))
                {
                    var seenDay = ctx.Time.LocalDate(seen);
                    if (seenDay < first)
                    {
                        skipped++;
                        continue;
                    }
                }

                if (!cohorts.TryGetValue(first, out var list)) cohorts[first] = list = new List<string>();
                list.Add(p);
            }

            if (skipped > 0) node.Set("returning", skipped);

            var columns = JsonNode.Arr().Add(Column("cohort", "Когорта")).Add(Column("size", "Игроков"));
            foreach (var p in periods) columns.Add(Column("d" + p, "D" + p, "pct"));
            var rows = JsonNode.Arr();
            var weighted = new double[periods.Count];
            var weights = new int[periods.Count];
            foreach (var pair in cohorts)
            {
                var row = JsonNode.Arr().Add(JsonNode.Of(ReportTime.DayKey(pair.Key))).Add(JsonNode.Of(pair.Value.Count));
                for (var i = 0; i < periods.Count; i++)
                {
                    var target = pair.Key.AddDays(periods[i]);
                    if (target > lastDay)
                    {
                        row.Add(new JsonNode()); // ещё не наступило — не ноль
                        continue;
                    }

                    var back = 0;
                    foreach (var p in pair.Value)
                        if (days[p].Contains(target)) back++;
                    var share = (double)back / pair.Value.Count;
                    row.Add(N(share));
                    weighted[i] += back;
                    weights[i] += pair.Value.Count;
                }

                rows.Add(row);
            }

            var avg = JsonNode.Obj();
            for (var i = 0; i < periods.Count; i++) avg.Set("d" + periods[i], N(weights[i] > 0 ? weighted[i] / weights[i] : double.NaN));
            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", rows).Set("dims", 1).Set("total", cohorts.Count));
            node.Set("totals", avg).Set("periods", Numbers(periods.ConvertAll(p => (double)p)));
            var head = weights.Length > 0 && weights[0] > 0 ? weighted[0] / weights[0] : double.NaN;
            node.Set("n", scope.Profiles.Count).Set("headline", head).Set("headlineLabel", "D" + periods[0] + " в среднем").Set("headlineFmt", "pct");
            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }

        // ============================================================ тепловая карта

        private static void Heatmap(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var x = string.IsNullOrEmpty(ctx.View.X) ? (ctx.GroupBy != null && ctx.GroupBy.Count > 0 ? ctx.GroupBy[0] : "subject") : ctx.View.X;
            var y = string.IsNullOrEmpty(ctx.View.Y) ? (ctx.GroupBy != null && ctx.GroupBy.Count > 1 ? ctx.GroupBy[1] : "@tag") : ctx.View.Y;
            var aggs = ParseAggs(ctx.Aggs, "count");
            var agg = aggs[0];
            var groups = GroupAgg(ctx, scope, hits, new List<string> { x, y }, new List<AggSpec> { agg });

            var xTotals = new Dictionary<string, int>(StringComparer.Ordinal);
            var yTotals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var g in groups)
            {
                xTotals.TryGetValue(g.Keys[0], out var a);
                xTotals[g.Keys[0]] = a + g.Units.Count;
                yTotals.TryGetValue(g.Keys[1], out var b);
                yTotals[g.Keys[1]] = b + g.Units.Count;
            }

            var limit = ctx.View.Top > 0 ? ctx.View.Top : 30;
            var xs = TopKeys(xTotals, limit, x);
            var ys = TopKeys(yTotals, limit, y);
            var cells = new double[ys.Count, xs.Count];
            var ns = new int[ys.Count, xs.Count];
            for (var i = 0; i < ys.Count; i++)
                for (var j = 0; j < xs.Count; j++)
                    cells[i, j] = double.NaN;
            var max = double.NaN;
            foreach (var g in groups)
            {
                var j = xs.IndexOf(g.Keys[0]);
                var i = ys.IndexOf(g.Keys[1]);
                if (i < 0 || j < 0) continue;
                cells[i, j] = AggValue(g, agg);
                ns[i, j] = g.Units.Count;
                if (!double.IsNaN(cells[i, j]) && (double.IsNaN(max) || cells[i, j] > max)) max = cells[i, j];
            }

            var matrix = JsonNode.Arr();
            var nMatrix = JsonNode.Arr();
            for (var i = 0; i < ys.Count; i++)
            {
                var row = JsonNode.Arr();
                var nrow = JsonNode.Arr();
                for (var j = 0; j < xs.Count; j++)
                {
                    row.Add(N(cells[i, j]));
                    nrow.Add(JsonNode.Of(ns[i, j]));
                }

                matrix.Add(row);
                nMatrix.Add(nrow);
            }

            node.Set("x", Strings(xs)).Set("y", Strings(ys)).Set("xTitle", ctx.DimTitle(x)).Set("yTitle", ctx.DimTitle(y))
                .Set("cells", matrix).Set("ns", nMatrix).Set("label", agg.Label).SetIf("fmt", agg.Fmt);
            node.Set("n", hits.Count).Set("headline", max).Set("headlineLabel", "Максимум: " + agg.Label.ToLowerInvariant());
            GroupTable(ctx, groups, new List<string> { x, y }, new List<AggSpec> { agg }, int.MaxValue, JsonNode.Obj(), tables, null, ctx.View.DisplayTitle);
        }

        private static List<string> TopKeys(Dictionary<string, int> totals, int limit, string dim)
        {
            var keys = new List<string>(totals.Keys);
            var timeDim = dim == "@day" || dim == "@week" || dim == "@hour" || dim == "@weekday";
            if (timeDim)
            {
                keys.Sort(StringComparer.Ordinal);
                if (keys.Count > limit) keys.RemoveRange(0, keys.Count - limit); // по времени — последние
                return keys;
            }

            keys.Sort((a, b) => totals[b].CompareTo(totals[a]));
            if (keys.Count > limit) keys.RemoveRange(limit, keys.Count - limit);
            return keys;
        }

        // ============================================================ корреляция

        private static void Correlation(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var xs = new Dictionary<object, double>();
            var ys = new Dictionary<object, double>();
            var order = new List<object>();
            foreach (var h in hits)
            {
                var u = UnitOf(ctx, h.E, ctx.Unit);
                if (u == null) continue;
                var amount = h.B.Measure != null ? Number(h.E.Field(h.B.Measure)) : 1;
                if (double.IsNaN(amount)) continue;
                var map = h.B.Role == BindRole.X ? xs : h.B.Role == BindRole.Y ? ys : null;
                if (map == null) continue;
                if (!xs.ContainsKey(u) && !ys.ContainsKey(u)) order.Add(u);
                map.TryGetValue(u, out var s);
                map[u] = s + amount;
            }

            // единица без событий оси Y — ноль (прогон без ошибок — это 0 ошибок); без X — пропуск
            var px = new List<double>();
            var py = new List<double>();
            foreach (var u in order)
            {
                if (!xs.TryGetValue(u, out var x)) continue;
                ys.TryGetValue(u, out var y);
                px.Add(x);
                py.Add(y);
            }

            var r = Pearson(px, py, out var slope, out var intercept);
            var points = JsonNode.Arr();
            var stride = Math.Max(1, px.Count / 1500);
            for (var i = 0; i < px.Count; i += stride) points.Add(JsonNode.Arr().Add(N(px[i])).Add(N(py[i])));

            node.Set("points", points).Set("n", px.Count).Set("headline", r).Set("headlineLabel", "Коэффициент корреляции r");
            node.Set("totals", JsonNode.Obj().Set("r", N(r)).Set("slope", N(slope)).Set("intercept", N(intercept)).Set("n", px.Count));

            var columns = JsonNode.Arr().Add(Column("x", "X")).Add(Column("y", "Y"));
            var rows = JsonNode.Arr();
            for (var i = 0; i < px.Count; i++) rows.Add(Row(N(px[i]), N(py[i])));
            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }

        private static double Pearson(List<double> x, List<double> y, out double slope, out double intercept)
        {
            slope = intercept = double.NaN;
            var n = x.Count;
            if (n < 3) return double.NaN;
            double mx = 0, my = 0;
            for (var i = 0; i < n; i++)
            {
                mx += x[i];
                my += y[i];
            }

            mx /= n;
            my /= n;
            double sxy = 0, sxx = 0, syy = 0;
            for (var i = 0; i < n; i++)
            {
                var dx = x[i] - mx;
                var dy = y[i] - my;
                sxy += dx * dy;
                sxx += dx * dx;
                syy += dy * dy;
            }

            if (sxx <= 0) return double.NaN;
            slope = sxy / sxx;
            intercept = my - slope * mx;
            return syy <= 0 ? double.NaN : sxy / Math.Sqrt(sxx * syy);
        }

        // ============================================================ переходы

        private const string StartNode = "▶ начало";
        private const string EndNode = "■ конец";

        private static void Transitions(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var unit = ctx.Unit == CountUnit.Events ? CountUnit.Runs : ctx.Unit;
            var seqs = new Dictionary<object, List<KeyValuePair<double, string>>>();
            foreach (var h in hits)
            {
                var u = UnitOf(ctx, h.E, unit);
                if (u == null || h.B.Subject == null) continue;
                if (!seqs.TryGetValue(u, out var list)) seqs[u] = list = new List<KeyValuePair<double, string>>();
                list.Add(new KeyValuePair<double, string>(h.E.Absolute, Text(h.E.Field(h.B.Subject))));
            }

            var pairs = new Dictionary<string, int>(StringComparer.Ordinal);
            var from = new Dictionary<string, int>(StringComparer.Ordinal);
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var list in seqs.Values)
            {
                list.Sort((a, b) => a.Key.CompareTo(b.Key));
                var prev = StartNode;
                foreach (var s in list)
                {
                    if (s.Value == prev) continue; // повтор того же шага — не переход
                    Count(pairs, prev + "\u0001" + s.Value);
                    Count(from, prev);
                    Count(totals, s.Value);
                    prev = s.Value;
                }

                Count(pairs, prev + "\u0001" + EndNode);
                Count(from, prev);
            }

            var limit = ctx.View.Top > 0 ? ctx.View.Top : 12;
            var nodes = TopKeys(totals, limit, null);
            var rowsKeys = new List<string> { StartNode };
            rowsKeys.AddRange(nodes);
            var colsKeys = new List<string>(nodes) { EndNode };
            var matrix = JsonNode.Arr();
            foreach (var a in rowsKeys)
            {
                var row = JsonNode.Arr();
                foreach (var b in colsKeys)
                {
                    pairs.TryGetValue(a + "\u0001" + b, out var c);
                    row.Add(JsonNode.Of(c));
                }

                matrix.Add(row);
            }

            var columns = JsonNode.Arr().Add(Column("from", "Откуда")).Add(Column("to", "Куда")).Add(Column("count", "Переходов")).Add(Column("share", "Доля из «откуда»", "pct"));
            var list2 = new List<KeyValuePair<string, int>>(pairs);
            list2.Sort((a, b) => b.Value.CompareTo(a.Value));
            var rows = JsonNode.Arr();
            foreach (var p in list2)
            {
                var parts = p.Key.Split('\u0001');
                from.TryGetValue(parts[0], out var f);
                rows.Add(Row(JsonNode.Of(parts[0]), JsonNode.Of(parts[1]), JsonNode.Of(p.Value), N(f > 0 ? (double)p.Value / f : double.NaN)));
            }

            node.Set("rows", Strings(rowsKeys)).Set("cols", Strings(colsKeys)).Set("matrix", matrix).Set("n", seqs.Count);
            var topRows = JsonNode.Arr();
            for (var i = 0; i < rows.Items.Count && i < 30; i++) topRows.Add(rows.Items[i]);
            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", topRows).Set("dims", 2).Set("total", rows.Items.Count));
            if (list2.Count > 0)
            {
                var best = list2[0].Key.Split('\u0001');
                node.Set("headline", list2[0].Value).Set("headlineLabel", "Частый переход").Set("headlineText", best[0] + " → " + best[1]);
            }

            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }

        private static void Count(Dictionary<string, int> map, string key)
        {
            map.TryGetValue(key, out var c);
            map[key] = c + 1;
        }

        // ============================================================ состояние

        /// <summary>Отрезки состояния прогона: значение держится до следующего события или конца прогона.</summary>
        internal static List<(double start, double end, string value)> StateSegments(ReportRun run, List<Hit> runHits, bool real)
        {
            var result = new List<(double, double, string)>();
            runHits.Sort((a, b) => a.E.Seq.CompareTo(b.E.Seq));
            var end = real ? run.RealEnd : run.AxisEnd;
            for (var i = 0; i < runHits.Count; i++)
            {
                var h = runHits[i];
                var start = real ? h.E.T : h.E.Axis;
                var stop = i + 1 < runHits.Count ? (real ? runHits[i + 1].E.T : runHits[i + 1].E.Axis) : end;
                var value = h.B.Value != null ? Text(h.E.Field(h.B.Value)) : "—";
                result.Add((start, Math.Max(start, stop), value));
            }

            return result;
        }

        private static void State(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var byRun = new Dictionary<ReportRun, List<Hit>>();
            foreach (var h in hits)
            {
                if (h.E.Run == null) continue;
                if (!byRun.TryGetValue(h.E.Run, out var list)) byRun[h.E.Run] = list = new List<Hit>();
                list.Add(h);
            }

            var time = new Dictionary<string, double>(StringComparer.Ordinal);
            var last = new Dictionary<string, int>(StringComparer.Ordinal);
            var runsWith = new Dictionary<string, int>(StringComparer.Ordinal);
            var total = 0.0;
            foreach (var pair in byRun)
            {
                var segs = StateSegments(pair.Key, pair.Value, ctx.View.RealClock);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in segs)
                {
                    time.TryGetValue(s.value, out var t);
                    time[s.value] = t + (s.end - s.start);
                    total += s.end - s.start;
                    if (seen.Add(s.value)) Count(runsWith, s.value);
                }

                if (segs.Count > 0) Count(last, segs[segs.Count - 1].value);
            }

            var keys = new List<string>(time.Keys);
            keys.Sort((a, b) => time[b].CompareTo(time[a]));
            var columns = JsonNode.Arr().Add(Column("value", "Значение")).Add(Column("time", "Время", "dur")).Add(Column("share", "Доля времени", "pct"))
                                    .Add(Column("runs", "Прогонов с ним")).Add(Column("last", "Последнее в прогоне"));
            var rows = JsonNode.Arr();
            foreach (var k in keys)
            {
                runsWith.TryGetValue(k, out var rw);
                last.TryGetValue(k, out var l);
                rows.Add(Row(JsonNode.Of(k), N(time[k]), N(total > 0 ? time[k] / total : double.NaN), JsonNode.Of(rw), JsonNode.Of(l)));
            }

            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", rows).Set("dims", 1).Set("total", keys.Count));
            node.Set("n", byRun.Count);
            if (keys.Count > 0)
            {
                var label = keys[0];
                foreach (var o in ctx.View.Outcomes)
                    if (o.Value == label && !string.IsNullOrWhiteSpace(o.Label)) label = o.Label;
                node.Set("headline", total > 0 ? time[keys[0]] / total : double.NaN).Set("headlineFmt", "pct").Set("headlineLabel", "Доля времени «" + label + "»");
            }
            node.Set("totals", JsonNode.Obj().Set("time", N(total)).Set("values", keys.Count));
            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }

        // ============================================================ поток ресурса

        private static void Flow(Ctx ctx, Scope scope, List<Hit> hits, JsonNode node, List<ReportTable> tables)
        {
            var income = new Dictionary<string, double>(StringComparer.Ordinal);
            var spend = new Dictionary<string, double>(StringComparer.Ordinal);
            var byDay = new Dictionary<string, double[]>(StringComparer.Ordinal);
            var units = new HashSet<object>();
            double inTotal = 0, outTotal = 0;
            foreach (var h in hits)
            {
                if (h.B.Measure == null) continue;
                var v = Number(h.E.Field(h.B.Measure));
                if (double.IsNaN(v)) continue;
                var signed = h.B.Tone == Tone.Positive ? Math.Abs(v) : h.B.Tone == Tone.Negative ? -Math.Abs(v) : v;
                var source = h.B.Subject != null ? Text(h.E.Field(h.B.Subject)) : h.E.Key;
                var u = UnitOf(ctx, h.E, ctx.Unit);
                if (u != null) units.Add(u);
                var day = BucketOf(ctx, h.E.Utc, "day");
                if (day != null)
                {
                    if (!byDay.TryGetValue(day, out var d)) byDay[day] = d = new double[2];
                    if (signed >= 0) d[0] += signed;
                    else d[1] += -signed;
                }

                if (signed >= 0)
                {
                    income.TryGetValue(source, out var s);
                    income[source] = s + signed;
                    inTotal += signed;
                }
                else
                {
                    spend.TryGetValue(source, out var s);
                    spend[source] = s - signed;
                    outTotal += -signed;
                }
            }

            var days = Buckets(ctx, "day", byDay.Keys);
            var inc = new List<double>();
            var outs = new List<double>();
            var balance = new List<double>();
            var running = 0.0;
            foreach (var d in days)
            {
                byDay.TryGetValue(d, out var v);
                inc.Add(v?[0] ?? 0);
                outs.Add(v?[1] ?? 0);
                running += (v?[0] ?? 0) - (v?[1] ?? 0);
                balance.Add(running);
            }

            node.Set("series", JsonNode.Obj().Set("x", Strings(days)).Set("income", Numbers(inc)).Set("spend", Numbers(outs)).Set("balance", Numbers(balance)));
            node.Set("n", units.Count).Set("headline", inTotal - outTotal).Set("headlineLabel", "Баланс (приход − расход)");
            node.Set("totals", JsonNode.Obj().Set("income", N(inTotal)).Set("spend", N(outTotal)).Set("net", N(inTotal - outTotal))
                                        .Set("net_per_unit", N(units.Count > 0 ? (inTotal - outTotal) / units.Count : double.NaN)));

            var columns = JsonNode.Arr().Add(Column("source", "Источник")).Add(Column("direction", "Направление")).Add(Column("amount", "Сумма")).Add(Column("share", "Доля в направлении", "pct"));
            var rows = JsonNode.Arr();
            void AddRows(Dictionary<string, double> map, string dir, double sum)
            {
                var keys = new List<string>(map.Keys);
                keys.Sort((a, b) => map[b].CompareTo(map[a]));
                foreach (var k in keys) rows.Add(Row(JsonNode.Of(k), JsonNode.Of(dir), N(map[k]), N(sum > 0 ? map[k] / sum : double.NaN)));
            }

            AddRows(income, "приход", inTotal);
            AddRows(spend, "расход", outTotal);
            node.Set("table", JsonNode.Obj().Set("columns", columns).Set("rows", rows).Set("dims", 2).Set("total", rows.Items.Count));
            tables.Add(TableOf(ctx, null, ctx.View.DisplayTitle, columns, rows));
        }
    }
}
