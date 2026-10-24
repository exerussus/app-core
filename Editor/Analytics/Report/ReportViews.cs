using System;
using System.Collections.Generic;
using System.Globalization;

namespace Exerussus.AppCore.Analytics.Reports
{
    /// <summary>Посчитанное представление: данные для report.html и таблицы для экспорта.</summary>
    public sealed class ViewResult
    {
        public ReportView View;
        /// <summary>null — по всем данным, иначе тег прогонов.</summary>
        public string Scope;
        public JsonNode Data = JsonNode.Obj();
        public readonly List<ReportTable> Tables = new List<ReportTable>();
        public double Headline = double.NaN;
        public string Error;
    }

    /// <summary>Таймлайн прогона: дорожки представлений (метки, интервалы, состояния).</summary>
    public sealed class RunTimeline
    {
        public ReportRun Run;
        public readonly List<JsonNode> Lanes = new List<JsonNode>();
    }

    /// <summary>
    /// Движок представлений: события → привязки → фильтр и вычисляемые поля → расчёт по типу. AppCore не знает,
    /// что значат события проекта: только роли и поля, которые указал каталог.
    /// </summary>
    public static partial class ReportViews
    {
        /// <summary>Попадание: событие в представлении с конкретной привязкой.</summary>
        internal sealed class Hit
        {
            public ReportEvent E;
            public ViewBinding B;
            public Dictionary<string, JsonValue> Computed;
        }

        /// <summary>Область расчёта: события, прогоны, сессии, игроки (все данные или прогоны одного тега).</summary>
        internal sealed class Scope
        {
            public string Tag;
            public List<ReportEvent> Events;
            public List<ReportRun> Runs;
            public List<ReportSession> Sessions;
            public List<string> Profiles;
        }

        internal sealed class Ctx
        {
            public ReportData Data;
            public ReportView View;
            public ReportTime Time;
            public CountUnit Unit;
            public int MinSample;
            public ReportExpr Where;
            public readonly List<KeyValuePair<string, ReportExpr>> Computed = new List<KeyValuePair<string, ReportExpr>>();
            public List<string> GroupBy;
            public List<string> Aggs;
            public string Compare;
            /// <summary>Имя поля-субъекта первой привязки: подпись «субъекта» в таблицах.</summary>
            public string SubjectField;
            public readonly List<string> Warnings = new List<string>();

            public string DimTitle(string dim) =>
                dim == "subject" && !string.IsNullOrEmpty(SubjectField) ? SubjectField : ViewTypes.DimensionTitle(dim);
        }

        // ============================================================ вход

        /// <summary>Посчитать все представления: по всем данным, по тегам и таймлайны последних прогонов.</summary>
        public static void Compute(ReportData data, bool full = true)
        {
            var config = data.Catalog.Config ?? new ReportConfig();
            var tz = data.Options.TimeZone ?? config.TimeZone;
            data.Time = ReportTime.Parse(tz, out var tzWarning);
            if (tzWarning != null) data.Warnings.Add(tzWarning);

            data.Views.Clear();
            foreach (var v in config.EffectiveViews())
            {
                if (data.Options.Views.Count > 0 && !data.Options.Views.Contains(v.Id)) continue;
                data.Views.Add(v);
            }

            // устойчивая сортировка по order: при равном — порядок файла
            var position = new Dictionary<ReportView, int>();
            for (var i = 0; i < data.Views.Count; i++) position[data.Views[i]] = i;
            data.Views.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : position[a].CompareTo(position[b]));

            var bindings = BindingIndex(config, data);
            var all = AllScope(data);
            CountTones(data, bindings);

            foreach (var view in data.Views)
            {
                var r = ComputeView(data, view, all, bindings, config);
                data.ViewResults.Add(r);
            }

            if (!full) return;

            foreach (var tag in data.Tags)
            {
                var scope = TagScope(tag);
                foreach (var view in data.Views)
                {
                    if (!view.ShowOnTag) continue;
                    tag.Views.Add(ComputeView(data, view, scope, bindings, config));
                }
            }

            BuildTimelines(data, bindings, config);
        }

        /// <summary>Ключ события → привязки по всем представлениям отчёта.</summary>
        private static Dictionary<string, List<ViewBinding>> BindingIndex(ReportConfig config, ReportData data)
        {
            var index = new Dictionary<string, List<ViewBinding>>(StringComparer.Ordinal);
            var all = config.EffectiveViews();
            foreach (var s in data.Sessions)
            {
                foreach (var e in s.Events)
                {
                    if (index.ContainsKey(e.Key)) continue;
                    index[e.Key] = config.BindingsOf(e.Key, all);
                }
            }

            return index;
        }

        private static List<ViewBinding> Bindings(Dictionary<string, List<ViewBinding>> index, string key) =>
            index.TryGetValue(key, out var list) ? list : new List<ViewBinding>();

        /// <summary>Окраска успех/провал из привязок — счётчики в таблице прогонов.</summary>
        private static void CountTones(ReportData data, Dictionary<string, List<ViewBinding>> index)
        {
            foreach (var run in data.Runs)
            {
                run.Positive = 0;
                run.Negative = 0;
                foreach (var e in run.Events)
                {
                    var tone = Tone.Neutral;
                    foreach (var b in Bindings(index, e.Key))
                        if (b.Tone == Tone.Positive || b.Tone == Tone.Negative) tone = b.Tone;
                    if (tone == Tone.Positive) run.Positive++;
                    else if (tone == Tone.Negative) run.Negative++;
                }
            }

            foreach (var p in data.Profiles)
            {
                p.Negative = 0;
                foreach (var run in data.Runs)
                    if (run.Profile == p.Id) p.Negative += run.Negative;
            }
        }

        private static Scope AllScope(ReportData data)
        {
            var scope = new Scope { Events = new List<ReportEvent>(), Runs = new List<ReportRun>(data.Runs), Sessions = new List<ReportSession>(data.Sessions), Profiles = new List<string>() };
            foreach (var s in data.Sessions) scope.Events.AddRange(s.Events);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in data.Sessions)
                if (seen.Add(s.Profile)) scope.Profiles.Add(s.Profile);
            return scope;
        }

        private static Scope TagScope(ReportTag tag)
        {
            var scope = new Scope { Tag = tag.Tag, Events = new List<ReportEvent>(), Runs = new List<ReportRun>(tag.Runs), Sessions = new List<ReportSession>(), Profiles = new List<string>() };
            var sessions = new HashSet<ReportSession>();
            var profiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in tag.Runs)
            {
                scope.Events.AddRange(r.Events);
                if (sessions.Add(r.Session)) scope.Sessions.Add(r.Session);
                if (profiles.Add(r.Profile)) scope.Profiles.Add(r.Profile);
            }

            return scope;
        }

        // ============================================================ одно представление

        private static ViewResult ComputeView(ReportData data, ReportView view, Scope scope, Dictionary<string, List<ViewBinding>> index, ReportConfig config)
        {
            var result = new ViewResult { View = view, Scope = scope.Tag };
            var ctx = new Ctx
            {
                Data = data,
                View = view,
                Time = data.Time,
                Unit = view.EffectiveUnit,
                MinSample = view.MinSample > 0 ? view.MinSample : config.MinSample,
                GroupBy = data.Options.GroupBy.Count > 0 ? data.Options.GroupBy : view.GroupBy,
                Aggs = data.Options.Aggs.Count > 0 ? data.Options.Aggs : view.Aggs,
                Compare = !string.IsNullOrWhiteSpace(data.Options.Compare) ? data.Options.Compare.Trim() : view.Compare,
            };

            try
            {
                if (view.BadType != null) throw new FormatException($"тип «{view.BadType}» неизвестен — представление не считается");
                Prepare(ctx, view, data.Options.Where);
                scope = FilterScope(ctx, scope);
                var hits = CollectHits(ctx, view, scope, index);
                var node = Calculate(ctx, scope, hits, result.Tables, true);

                if (!string.IsNullOrWhiteSpace(ctx.Compare) && ViewTypes.Of(view.Type).Type != ViewType.Retention)
                    node.Set("compare", CompareGroups(ctx, scope, hits));

                if (view.ComparePrevious && scope.Tag == null) node.Set("previous", PreviousNode(data, view, node));

                ApplyTarget(view, node);
                result.Headline = node.Num("headline");
                result.Data = Header(ctx, view, scope, node);
            }
            catch (Exception e) when (e is FormatException || e is InvalidOperationException || e is ArgumentException)
            {
                result.Error = e.Message;
                result.Data = Header(ctx, view, scope, JsonNode.Obj().Set("error", e.Message));
                if (scope.Tag == null) data.Warnings.Add($"Представление «{view.DisplayTitle}»: {e.Message}");
            }

            if (ctx.Warnings.Count > 0)
            {
                var arr = JsonNode.Arr();
                foreach (var w in ctx.Warnings) arr.Add(JsonNode.Of(w));
                result.Data.Set("warnings", arr);
                if (scope.Tag == null)
                    foreach (var w in ctx.Warnings) data.Warnings.Add($"Представление «{view.DisplayTitle}»: {w}");
            }

            return result;
        }

        private static void Prepare(Ctx ctx, ReportView view, string extraWhere)
        {
            foreach (var c in view.Computed)
            {
                if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Expr)) continue;
                ctx.Computed.Add(new KeyValuePair<string, ReportExpr>(c.Name.Trim(), ReportExpr.Compile(c.Expr)));
            }

            var where = view.Where;
            if (!string.IsNullOrWhiteSpace(extraWhere))
                where = string.IsNullOrWhiteSpace(where) ? extraWhere : "(" + where + ") && (" + extraWhere + ")";
            if (!string.IsNullOrWhiteSpace(where)) ctx.Where = ReportExpr.Compile(where);
        }

        /// <summary>
        /// Фильтр только по измерениям единицы (@build, @tag, @prop…) сужает и знаменатель: доля «среди прогонов билда»,
        /// а не «среди всех». Фильтр по полям событий знаменатель не трогает — о чём сказано в предупреждении.
        /// </summary>
        private static Scope FilterScope(Ctx ctx, Scope scope)
        {
            if (ctx.Where == null) return scope;
            var unitLevel = true;
            foreach (var name in ctx.Where.Names)
                if (!ViewTypes.IsUnitLevel(name, CountUnit.Runs) && !ViewTypes.IsUnitLevel(name, CountUnit.Sessions)) unitLevel = false;

            var t = ctx.View.Type;
            var usesPopulation = t == ViewType.Share || t == ViewType.Funnel || t == ViewType.Progress || t == ViewType.Conversion ||
                                 t == ViewType.Outcome || t == ViewType.Activity || t == ViewType.Retention;
            if (!unitLevel)
            {
                if (usesPopulation) ctx.Warnings.Add("фильтр по полям событий не сужает знаменатель (все единицы области)");
                return scope;
            }

            var filtered = new Scope { Tag = scope.Tag, Events = new List<ReportEvent>(), Runs = new List<ReportRun>(), Sessions = new List<ReportSession>(), Profiles = new List<string>() };
            foreach (var r in scope.Runs)
                if (ctx.Where.Test(n => JsonValue.Of(n.Length > 0 && n[0] == '@' ? DimOfRun(ctx, r, n) : null))) filtered.Runs.Add(r);
            foreach (var s in scope.Sessions)
                if (ctx.Where.Test(n => JsonValue.Of(n.Length > 0 && n[0] == '@' ? DimOfSession(ctx, s, n) : null))) filtered.Sessions.Add(s);
            var keepProfiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in filtered.Sessions) keepProfiles.Add(s.Profile);
            foreach (var p in scope.Profiles)
                if (keepProfiles.Contains(p)) filtered.Profiles.Add(p);
            var runs = new HashSet<ReportRun>(filtered.Runs);
            var sessions = new HashSet<ReportSession>(filtered.Sessions);
            foreach (var e in scope.Events)
                if (e.Run != null ? runs.Contains(e.Run) : sessions.Contains(e.Session)) filtered.Events.Add(e);
            return filtered;
        }

        private static List<Hit> CollectHits(Ctx ctx, ReportView view, Scope scope, Dictionary<string, List<ViewBinding>> index)
        {
            var hits = new List<Hit>();
            foreach (var e in scope.Events)
            {
                foreach (var b in Bindings(index, e.Key))
                {
                    if (b.View != view.Id) continue;
                    var hit = new Hit { E = e, B = b };
                    if (ctx.Computed.Count > 0)
                    {
                        hit.Computed = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                        foreach (var c in ctx.Computed)
                        {
                            var h = hit;
                            hit.Computed[c.Key] = c.Value.Eval(n => Resolve(ctx, h, n));
                        }
                    }

                    // where — к фактам; у пар (начало/конец, A/B) — тоже к каждому событию отдельно
                    if (ctx.Where != null)
                    {
                        var h = hit;
                        if (!ctx.Where.Test(n => Resolve(ctx, h, n))) continue;
                    }

                    hits.Add(hit);
                    if (ctx.SubjectField == null && b.Subject != null) ctx.SubjectField = b.Subject;
                }
            }

            return hits;
        }

        private static JsonNode Header(Ctx ctx, ReportView view, Scope scope, JsonNode body)
        {
            body.Set("id", view.Id ?? string.Empty)
                .Set("type", ViewTypes.Name(view.Type))
                .Set("title", view.DisplayTitle)
                .SetIf("note", view.Note)
                .Set("unit", ViewTypes.Name(ctx.Unit))
                .Set("minSample", ctx.MinSample)
                .Set("pin", view.Pin)
                .Set("builtin", view.Builtin)
                .Set("onTag", view.ShowOnTag)
                .Set("onRun", view.ShowOnRun);
            if (view.Target != null && view.Target.IsSet)
                body.Set("target", JsonNode.Obj().SetIf("agg", view.Target.Agg).Set("value", view.Target.Value).Set("better", view.Target.LowerIsBetter ? "lower" : "higher"));
            if (scope.Tag != null) body.Set("scope", scope.Tag);
            return body;
        }

        // ============================================================ расчёт по типу

        private static JsonNode Calculate(Ctx ctx, Scope scope, List<Hit> hits, List<ReportTable> tables, bool main)
        {
            var node = JsonNode.Obj();
            var t = ctx.View.Type;
            switch (t)
            {
                case ViewType.Counter: Counter(ctx, scope, hits, node, tables); break;
                case ViewType.Interval: IntervalView(ctx, scope, hits, node, tables); break;
                case ViewType.Funnel: Funnel(ctx, scope, hits, node, tables, false); break;
                case ViewType.Progress: Funnel(ctx, scope, hits, node, tables, true); break;
                case ViewType.Breakdown: Breakdown(ctx, scope, hits, node, tables); break;
                case ViewType.Distribution: Distribution(ctx, scope, hits, node, tables); break;
                case ViewType.Outcome: Outcome(ctx, scope, hits, node, tables); break;
                case ViewType.Lane: Lane(ctx, scope, hits, node, tables); break;
                case ViewType.Share: Share(ctx, scope, hits, node, tables); break;
                case ViewType.Conversion: Conversion(ctx, scope, hits, node, tables); break;
                case ViewType.Series: Series(ctx, scope, hits, node, tables); break;
                case ViewType.Activity: Activity(ctx, scope, node, tables); break;
                case ViewType.Retention: Retention(ctx, scope, node, tables); break;
                case ViewType.Heatmap: Heatmap(ctx, scope, hits, node, tables); break;
                case ViewType.Correlation: Correlation(ctx, scope, hits, node, tables); break;
                case ViewType.Transitions: Transitions(ctx, scope, hits, node, tables); break;
                case ViewType.State: State(ctx, scope, hits, node, tables); break;
                case ViewType.Flow: Flow(ctx, scope, hits, node, tables); break;
            }

            if (main) node.Set("coverage", Coverage(ctx, scope, hits));
            var n = node.Num("n", 0);
            node.Set("low", n < ctx.MinSample);
            return node;
        }

        /// <summary>Сравнение: тот же расчёт по частям, разбитым по измерению (до 8 частей, самые крупные).</summary>
        private static JsonNode CompareGroups(Ctx ctx, Scope scope, List<Hit> hits)
        {
            var dim = ctx.Compare;
            var parts = new Dictionary<string, Scope>(StringComparer.Ordinal);
            var partHits = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
            var order = new List<string>();

            Scope Part(string key)
            {
                if (parts.TryGetValue(key, out var p)) return p;
                p = new Scope { Tag = scope.Tag, Events = new List<ReportEvent>(), Runs = new List<ReportRun>(), Sessions = new List<ReportSession>(), Profiles = new List<string>() };
                parts[key] = p;
                partHits[key] = new List<Hit>();
                order.Add(key);
                return p;
            }

            var unitLevel = ViewTypes.IsUnitLevel(dim, ctx.Unit) || ctx.View.Type == ViewType.Activity;
            foreach (var h in hits)
            {
                var key = Text(Resolve(ctx, h, dim));
                Part(key);
                partHits[key].Add(h);
            }

            // население: для измерений уровня единицы — по значению единицы, иначе — общее у всех частей
            foreach (var r in scope.Runs)
            {
                if (unitLevel) Part(DimOfRun(ctx, r, dim)).Runs.Add(r);
            }

            foreach (var s in scope.Sessions)
            {
                if (unitLevel) Part(DimOfSession(ctx, s, dim)).Sessions.Add(s);
            }

            foreach (var p in scope.Profiles)
            {
                if (unitLevel) Part(DimOfProfile(ctx, p, dim)).Profiles.Add(p);
            }

            foreach (var key in order)
            {
                var p = parts[key];
                if (!unitLevel)
                {
                    p.Runs = scope.Runs;
                    p.Sessions = scope.Sessions;
                    p.Profiles = scope.Profiles;
                }
                else
                {
                    var runs = new HashSet<ReportRun>(p.Runs);
                    var sessions = new HashSet<ReportSession>(p.Sessions);
                    foreach (var e in scope.Events)
                        if ((e.Run != null && runs.Contains(e.Run)) || (e.Run == null && sessions.Contains(e.Session))) p.Events.Add(e);
                }

                if (p.Events.Count == 0) p.Events = scope.Events;
            }

            order.Sort((a, b) =>
            {
                var c = Size(parts[b], partHits[b]).CompareTo(Size(parts[a], partHits[a]));
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });

            var arr = JsonNode.Arr();
            var shown = 0;
            foreach (var key in order)
            {
                if (shown++ >= 8) break;
                var sub = Calculate(ctx, parts[key], partHits[key], new List<ReportTable>(), false);
                sub.Set("group", key);
                arr.Add(sub);
            }

            return JsonNode.Obj().Set("dim", dim).Set("title", ctx.DimTitle(dim)).Set("groups", arr).Set("total", order.Count);
        }

        private static int Size(Scope s, List<Hit> hits) => Math.Max(hits.Count, s.Runs.Count + s.Sessions.Count);

        private static JsonNode PreviousNode(ReportData data, ReportView view, JsonNode current)
        {
            var node = JsonNode.Obj();
            if (data.Previous == null)
            {
                node.Set("missing", "задайте период (с/по или «последние N дней») — сравнивать не с чем");
                return node;
            }

            foreach (var r in data.Previous.ViewResults)
            {
                if (r.View.Id != view.Id) continue;
                var prev = r.Headline;
                var cur = current.Num("headline");
                node.Set("headline", prev);
                if (!double.IsNaN(prev) && !double.IsNaN(cur)) node.Set("delta", cur - prev);
                if (!double.IsNaN(prev) && prev != 0 && !double.IsNaN(cur)) node.Set("ratio", (cur - prev) / Math.Abs(prev));
                node.Set("from", ReportExport.Iso(data.Previous.PeriodFrom)).Set("to", ReportExport.Iso(data.Previous.PeriodTo));
                return node;
            }

            node.Set("missing", "в прошлом периоде нет данных");
            return node;
        }

        private static void ApplyTarget(ReportView view, JsonNode node)
        {
            if (view.Target == null || !view.Target.IsSet) return;
            var totals = node["totals"];
            var value = node.Num("headline");
            if (!string.IsNullOrEmpty(view.Target.Agg) && totals != null && totals[view.Target.Agg] != null) value = totals.Num(view.Target.Agg);
            if (double.IsNaN(value)) return;
            var ok = view.Target.LowerIsBetter ? value <= view.Target.Value : value >= view.Target.Value;
            node.Set("targetOk", ok).Set("targetValue", value);
        }

        // ============================================================ покрытие данных

        private static JsonNode Coverage(Ctx ctx, Scope scope, List<Hit> hits)
        {
            var sessions = new HashSet<ReportSession>();
            var runs = new HashSet<ReportRun>();
            if (hits.Count > 0)
            {
                foreach (var h in hits)
                {
                    sessions.Add(h.E.Session);
                    if (h.E.Run != null) runs.Add(h.E.Run);
                }
            }
            else
            {
                foreach (var s in scope.Sessions) sessions.Add(s);
                foreach (var r in scope.Runs) runs.Add(r);
            }

            long missing = 0, dropped = 0;
            foreach (var s in sessions)
            {
                missing += s.Missing;
                dropped += s.Dropped;
            }

            var unfinished = 0;
            foreach (var r in runs)
                if (!r.Finished) unfinished++;

            return JsonNode.Obj().Set("sessions", sessions.Count).Set("runs", runs.Count).Set("unfinished", unfinished)
                           .Set("missing", missing).Set("dropped", dropped).Set("hits", hits.Count);
        }

        // ============================================================ значения и измерения

        internal static JsonValue Resolve(Ctx ctx, Hit h, string name)
        {
            if (string.IsNullOrEmpty(name)) return JsonValue.Null;
            if (h.Computed != null && h.Computed.TryGetValue(name, out var c)) return c;
            switch (name)
            {
                case "subject": return h.B.Subject != null ? h.E.Field(h.B.Subject) : JsonValue.Null;
                case "measure": return h.B.Measure != null ? h.E.Field(h.B.Measure) : JsonValue.Null;
                case "value": return h.B.Value != null ? h.E.Field(h.B.Value) : JsonValue.Null;
            }

            if (name[0] == '@') return JsonValue.Of(Dim(ctx, h.E, name));
            return h.E.Field(name);
        }

        internal static string Text(JsonValue v) => v.IsNull ? "—" : (v.AsText() ?? "—");

        internal static double Number(JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.Number: return v.Number;
                case JsonKind.Bool: return v.Flag ? 1 : 0;
                case JsonKind.String:
                    return double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
                default: return double.NaN;
            }
        }

        internal static string Dim(Ctx ctx, ReportEvent e, string dim)
        {
            switch (dim)
            {
                case "@tag": return e.Run != null ? e.Run.TagOrDash : "—";
                case "@result": return e.Run?.Result ?? "—";
                case "@build": return e.Session.Build ?? "—";
                case "@platform": return e.Session.Platform ?? "—";
                case "@profile": return e.Session.Profile ?? "—";
                case "@session": return e.Session.Id;
                case "@run": return e.Run?.Id ?? "—";
                case "@event": return e.Key;
                case "@day":
                case "@week":
                case "@hour":
                case "@weekday":
                    return TimeDim(ctx, e.Utc, dim);
            }

            if (dim.StartsWith("@prop.", StringComparison.Ordinal)) return e.Prop(dim.Substring(6)) ?? "—";
            return Text(e.Field(dim.TrimStart('@')));
        }

        private static string TimeDim(Ctx ctx, DateTime? utc, string dim)
        {
            if (utc == null) return "—";
            switch (dim)
            {
                case "@day": return ctx.Time.Day(utc.Value);
                case "@week": return ctx.Time.Week(utc.Value);
                case "@hour": return ctx.Time.Hour(utc.Value);
                default: return ctx.Time.Weekday(utc.Value);
            }
        }

        internal static string DimOfRun(Ctx ctx, ReportRun r, string dim)
        {
            switch (dim)
            {
                case "@tag": return r.TagOrDash;
                case "@result": return r.Result ?? "—";
                case "@run": return r.Id;
                case "@day":
                case "@week":
                case "@hour":
                case "@weekday":
                    return TimeDim(ctx, r.StartTime, dim);
            }

            if (dim.StartsWith("@prop.", StringComparison.Ordinal))
            {
                var name = dim.Substring(6);
                foreach (var e in r.Events)
                    if (e.Props.TryGetValue(name, out var v)) return v;
            }

            return DimOfSession(ctx, r.Session, dim);
        }

        internal static string DimOfSession(Ctx ctx, ReportSession s, string dim)
        {
            switch (dim)
            {
                case "@build": return s.Build ?? "—";
                case "@platform": return s.Platform ?? "—";
                case "@profile": return s.Profile ?? "—";
                case "@session": return s.Id;
                case "@day":
                case "@week":
                case "@hour":
                case "@weekday":
                    return TimeDim(ctx, s.StartTime, dim);
            }

            if (dim.StartsWith("@prop.", StringComparison.Ordinal)) return s.Props.TryGetValue(dim.Substring(6), out var v) ? v : "—";
            return NotApplicable;
        }

        /// <summary>Измерение не применимо к единице (итог прогона у сессии) — не путать с «без итога» («—»).</summary>
        internal const string NotApplicable = "(неприменимо)";

        internal static string DimOfProfile(Ctx ctx, string profile, string dim)
        {
            if (dim == "@profile") return profile;
            // остальное — по первой сессии игрока (день прихода, билд и платформа первого запуска)
            ReportSession first = null;
            foreach (var s in ctx.Data.Sessions)
                if (s.Profile == profile && (first == null || Nullable.Compare(s.StartTime, first.StartTime) < 0)) first = s;
            if (first == null) return "—";
            if (dim.StartsWith("@prop.", StringComparison.Ordinal))
            {
                var name = dim.Substring(6);
                foreach (var p in ctx.Data.Profiles)
                    if (p.Id == profile && p.Props.TryGetValue(name, out var v)) return v;
                return "—";
            }

            return DimOfSession(ctx, first, dim);
        }

        /// <summary>Единица попадания: событие, прогон, сессия или игрок. null — у события нет такой единицы (вне прогона).</summary>
        internal static object UnitOf(Ctx ctx, ReportEvent e, CountUnit unit)
        {
            switch (unit)
            {
                case CountUnit.Runs: return e.Run;
                case CountUnit.Sessions: return e.Session;
                case CountUnit.Profiles: return e.Session.Profile;
                default: return e;
            }
        }

        /// <summary>Население единиц области (для долей): все прогоны, сессии или игроки.</summary>
        internal static List<object> Population(Scope scope, CountUnit unit)
        {
            var list = new List<object>();
            switch (unit)
            {
                case CountUnit.Runs:
                    foreach (var r in scope.Runs) list.Add(r);
                    break;
                case CountUnit.Sessions:
                    foreach (var s in scope.Sessions) list.Add(s);
                    break;
                case CountUnit.Profiles:
                    foreach (var p in scope.Profiles) list.Add(p);
                    break;
            }

            return list;
        }

        internal static string DimOfUnit(Ctx ctx, object unit, string dim)
        {
            switch (unit)
            {
                case ReportRun r: return DimOfRun(ctx, r, dim);
                case ReportSession s: return DimOfSession(ctx, s, dim);
                case string p: return DimOfProfile(ctx, p, dim);
                case ReportEvent e: return Dim(ctx, e, dim);
                default: return "—";
            }
        }

        internal static DateTime? UnitTime(object unit)
        {
            switch (unit)
            {
                case ReportRun r: return r.StartTime;
                case ReportSession s: return s.StartTime;
                case ReportEvent e: return e.Utc;
                default: return null;
            }
        }

        // ============================================================ таблицы и форматы

        internal static JsonNode Column(string id, string label, string fmt = null) =>
            JsonNode.Obj().Set("id", id).Set("label", label).SetIf("fmt", fmt);

        internal static JsonNode Row(params JsonNode[] cells)
        {
            var row = JsonNode.Arr();
            foreach (var c in cells) row.Add(c ?? new JsonNode());
            return row;
        }

        internal static JsonNode N(double v) => double.IsNaN(v) || double.IsInfinity(v) ? new JsonNode() : JsonNode.Of(Math.Round(v, 4));

        internal static JsonValue V(double v) => double.IsNaN(v) || double.IsInfinity(v) ? JsonValue.Null : JsonValue.Of(Math.Round(v, 4));

        internal static JsonValue V(JsonNode n)
        {
            if (n == null) return JsonValue.Null;
            switch (n.Kind)
            {
                case JsonNodeKind.Number: return JsonValue.Of(n.Number);
                case JsonNodeKind.String: return JsonValue.Of(n.Text);
                case JsonNodeKind.Bool: return JsonValue.Of(n.Flag);
                default: return JsonValue.Null;
            }
        }

        /// <summary>Таблица экспорта из колонок и строк узла (то же, что видит report.html).</summary>
        internal static ReportTable TableOf(Ctx ctx, string suffix, string title, JsonNode columns, JsonNode rows)
        {
            var id = "view_" + ctx.View.Id + (string.IsNullOrEmpty(suffix) ? string.Empty : "_" + suffix);
            var table = new ReportTable { Id = id, Title = title };
            foreach (var c in columns.Items) table.Add(c.Str("id"), c.Str("label"));
            foreach (var r in rows.Items)
            {
                var row = new JsonValue[table.Columns.Count];
                for (var i = 0; i < row.Length && i < r.Items.Count; i++) row[i] = V(r.Items[i]);
                table.Rows.Add(row);
            }

            return table;
        }

        internal static JsonNode Strings(IEnumerable<string> values)
        {
            var arr = JsonNode.Arr();
            foreach (var v in values) arr.Add(JsonNode.Of(v));
            return arr;
        }

        internal static JsonNode Numbers(IEnumerable<double> values)
        {
            var arr = JsonNode.Arr();
            foreach (var v in values) arr.Add(N(v));
            return arr;
        }
    }
}
