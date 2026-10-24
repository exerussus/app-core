using System;
using System.Collections.Generic;
using System.Globalization;

namespace Exerussus.AppCore.Analytics.Reports
{
    public static partial class ReportViews
    {
        /// <summary>Агрегат: <c>count</c>, <c>share</c>, <c>distinct:f</c>, <c>sum:f</c>, <c>avg:f</c>, <c>median:f</c>, <c>pNN:f</c>, <c>min:f</c>, <c>max:f</c>.</summary>
        internal sealed class AggSpec
        {
            public string Raw;
            public string Op;
            public string Field;
            public double P = double.NaN;

            public string Id => Field == null ? Op : Op + "_" + Field;

            public string Label
            {
                get
                {
                    var f = Field == "measure" ? "мера" : Field;
                    switch (Op)
                    {
                        case "count": return "Количество";
                        case "share": return "Доля";
                        case "distinct": return "Разных " + f;
                        case "sum": return "Сумма " + f;
                        case "avg": return "Среднее " + f;
                        case "median": return "Медиана " + f;
                        case "min": return "Минимум " + f;
                        case "max": return "Максимум " + f;
                        default: return Op + " " + f;
                    }
                }
            }

            public string Fmt => Op == "share" ? "pct" : null;

            public static AggSpec Parse(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) throw new FormatException("пустой агрегат");
                s = s.Trim();
                var spec = new AggSpec { Raw = s };
                var colon = s.IndexOf(':');
                spec.Op = (colon < 0 ? s : s.Substring(0, colon)).Trim().ToLowerInvariant();
                spec.Field = colon < 0 ? null : s.Substring(colon + 1).Trim();
                if (spec.Field == string.Empty) spec.Field = null;

                switch (spec.Op)
                {
                    case "count":
                    case "share":
                        spec.Field = null;
                        return spec;
                    case "distinct":
                    case "sum":
                    case "avg":
                    case "median":
                    case "min":
                    case "max":
                        break;
                    default:
                        if (spec.Op.Length > 1 && spec.Op[0] == 'p' &&
                            double.TryParse(spec.Op.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0 && p < 100)
                        {
                            spec.P = p / 100.0;
                            break;
                        }

                        throw new FormatException($"агрегат «{s}»: count, share, distinct:f, sum:f, avg:f, median:f, p90:f, min:f, max:f");
                }

                if (spec.Field == null) throw new FormatException($"агрегат «{s}»: нужно поле после «:»");
                return spec;
            }

            public bool Numeric => Op != "count" && Op != "share" && Op != "distinct";
        }

        /// <summary>Проверить запись агрегата (для каталога и CLI). Ошибка — <see cref="FormatException"/>.</summary>
        public static void ValidateAgg(string spec) => AggSpec.Parse(spec);

        internal static List<AggSpec> ParseAggs(List<string> list, params string[] defaults)
        {
            var result = new List<AggSpec>();
            var source = list != null && list.Count > 0 ? list : new List<string>(defaults);
            foreach (var s in source) result.Add(AggSpec.Parse(s));
            return result;
        }

        /// <summary>Группа агрегирования: единицы, значения полей (по событиям или суммы по единицам).</summary>
        internal sealed class Group
        {
            public string[] Keys;
            public readonly HashSet<object> Units = new HashSet<object>();
            public int Hits;
            /// <summary>Поле → единица → сумма (единица — событие при unit=events).</summary>
            public readonly Dictionary<string, Dictionary<object, double>> Sums = new Dictionary<string, Dictionary<object, double>>(StringComparer.Ordinal);
            public readonly Dictionary<string, HashSet<string>> Distinct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            public int Population;
        }

        /// <summary>
        /// Сгруппировать попадания по измерениям и посчитать агрегаты. Числовые агрегаты при единице «события» — по
        /// значениям событий; при прогонах/сессиях/игроках — по суммам поля внутри единицы (медиана суммы на прогон).
        /// </summary>
        internal static List<Group> GroupAgg(Ctx ctx, Scope scope, List<Hit> hits, List<string> dims, List<AggSpec> aggs,
                                             Func<Hit, string, JsonValue> value = null)
        {
            value ??= (h, f) => Resolve(ctx, h, f);
            var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
            var order = new List<Group>();

            foreach (var h in hits)
            {
                var unit = UnitOf(ctx, h.E, ctx.Unit);
                if (unit == null) continue;

                var keys = new string[dims.Count];
                for (var i = 0; i < dims.Count; i++) keys[i] = Text(Resolve(ctx, h, dims[i]));
                var id = string.Join("\u0001", keys);
                if (!groups.TryGetValue(id, out var g))
                {
                    g = new Group { Keys = keys };
                    groups[id] = g;
                    order.Add(g);
                }

                g.Units.Add(unit);
                g.Hits++;

                foreach (var a in aggs)
                {
                    if (a.Field == null) continue;
                    var v = value(h, a.Field);
                    if (a.Op == "distinct")
                    {
                        if (!g.Distinct.TryGetValue(a.Field, out var set)) g.Distinct[a.Field] = set = new HashSet<string>(StringComparer.Ordinal);
                        if (!v.IsNull) set.Add(Text(v));
                        continue;
                    }

                    var n = Number(v);
                    if (double.IsNaN(n)) continue;
                    if (!g.Sums.TryGetValue(a.Field, out var sums)) g.Sums[a.Field] = sums = new Dictionary<object, double>();
                    sums.TryGetValue(unit, out var s);
                    sums[unit] = s + n;
                }
            }

            // население группы для доли: измерения уровня единицы — своя группа, иначе — вся область
            var needShare = false;
            foreach (var a in aggs)
                if (a.Op == "share") needShare = true;

            if (needShare)
            {
                var population = Population(scope, ctx.Unit);
                var unitLevel = dims.Count > 0;
                foreach (var d in dims)
                    if (!ViewTypes.IsUnitLevel(d, ctx.Unit)) unitLevel = false;

                if (unitLevel)
                {
                    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var u in population)
                    {
                        var keys = new string[dims.Count];
                        for (var i = 0; i < dims.Count; i++) keys[i] = DimOfUnit(ctx, u, dims[i]);
                        var id = string.Join("\u0001", keys);
                        counts.TryGetValue(id, out var c);
                        counts[id] = c + 1;
                    }

                    foreach (var g in order) g.Population = counts.TryGetValue(string.Join("\u0001", g.Keys), out var c) ? c : g.Units.Count;
                }
                else
                {
                    foreach (var g in order) g.Population = population.Count;
                }
            }

            return order;
        }

        internal static double AggValue(Group g, AggSpec a)
        {
            switch (a.Op)
            {
                case "count": return g.Units.Count;
                case "share": return g.Population > 0 ? (double)g.Units.Count / g.Population : double.NaN;
                case "distinct": return g.Distinct.TryGetValue(a.Field, out var set) ? set.Count : 0;
            }

            if (!g.Sums.TryGetValue(a.Field, out var sums) || sums.Count == 0) return double.NaN;
            var values = new List<double>(sums.Values);
            switch (a.Op)
            {
                case "sum":
                {
                    var s = 0.0;
                    foreach (var v in values) s += v;
                    return s;
                }
                case "avg":
                {
                    var s = 0.0;
                    foreach (var v in values) s += v;
                    return s / values.Count;
                }
                case "median": return Stats.Median(values);
                case "min":
                {
                    var m = double.PositiveInfinity;
                    foreach (var v in values) m = Math.Min(m, v);
                    return m;
                }
                case "max":
                {
                    var m = double.NegativeInfinity;
                    foreach (var v in values) m = Math.Max(m, v);
                    return m;
                }
                default: return Stats.Percentile(values, a.P);
            }
        }

        /// <summary>Таблица групп: измерения, n, агрегаты. Сортировка — по первому агрегату, по убыванию.</summary>
        internal static void GroupTable(Ctx ctx, List<Group> groups, List<string> dims, List<AggSpec> aggs, int top, JsonNode node, List<ReportTable> tables,
                                        string suffix, string title, bool sortByKey = false)
        {
            var columns = JsonNode.Arr();
            foreach (var d in dims) columns.Add(Column(Slug(d), ctx.DimTitle(d)));
            columns.Add(Column("n", "n, " + ViewTypes.Genitive(ctx.Unit)));
            foreach (var a in aggs) columns.Add(Column(a.Id, a.Label, a.Fmt));

            var sorted = new List<Group>(groups);
            if (sortByKey) sorted.Sort((a, b) => string.CompareOrdinal(string.Join("\u0001", a.Keys), string.Join("\u0001", b.Keys)));
            else if (aggs.Count > 0)
            {
                var first = aggs[0];
                sorted.Sort((a, b) =>
                {
                    var x = AggValue(a, first);
                    var y = AggValue(b, first);
                    if (double.IsNaN(x)) x = double.NegativeInfinity;
                    if (double.IsNaN(y)) y = double.NegativeInfinity;
                    return y.CompareTo(x);
                });
            }

            var rows = JsonNode.Arr();
            var low = JsonNode.Arr();
            var all = JsonNode.Arr();
            foreach (var g in sorted)
            {
                var row = JsonNode.Arr();
                foreach (var k in g.Keys) row.Add(JsonNode.Of(k));
                row.Add(JsonNode.Of(g.Units.Count));
                foreach (var a in aggs) row.Add(N(AggValue(g, a)));
                all.Add(row);
                if (rows.Items.Count < top)
                {
                    rows.Add(row);
                    low.Add(JsonNode.Of(g.Units.Count < ctx.MinSample));
                }
            }

            var table = JsonNode.Obj().Set("columns", columns).Set("rows", rows).Set("low", low).Set("dims", dims.Count).Set("total", sorted.Count);
            node.Set(string.IsNullOrEmpty(suffix) ? "table" : "table_" + suffix, table);
            tables.Add(TableOf(ctx, suffix, title, columns, all));
        }

        internal static string Slug(string dim)
        {
            if (string.IsNullOrEmpty(dim)) return "value";
            var s = dim.TrimStart('@').Replace('.', '_');
            return s.Length == 0 ? "value" : s;
        }

        internal static List<string> DefaultDims(Ctx ctx, List<Hit> hits, string fallback = "@event")
        {
            if (ctx.GroupBy != null && ctx.GroupBy.Count > 0) return ctx.GroupBy;
            foreach (var h in hits)
                if (h.B.Subject != null) return new List<string> { "subject" };
            return new List<string> { fallback };
        }

        internal static bool AnyMeasure(List<Hit> hits)
        {
            foreach (var h in hits)
                if (h.B.Measure != null) return true;
            return false;
        }

        // ============================================================ время

        /// <summary>Ключ корзины времени: hour | day | week.</summary>
        internal static string BucketOf(Ctx ctx, DateTime? utc, string bucket)
        {
            if (utc == null) return null;
            switch (bucket)
            {
                case "hour": return ctx.Time.HourBucket(utc.Value);
                case "week": return ctx.Time.Week(utc.Value);
                default: return ctx.Time.Day(utc.Value);
            }
        }

        /// <summary>Все корзины периода отчёта по порядку (пустые дни тоже — разрывы видны на графике).</summary>
        internal static List<string> Buckets(Ctx ctx, string bucket, IEnumerable<string> seen)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in seen)
                if (k != null) keys.Add(k);
            if (keys.Count == 0) return new List<string>();
            if (bucket == "hour") return new List<string>(keys);

            var first = keys.Min;
            var last = keys.Max;
            if (!ReportTime.TryDay(first, out var from) || !ReportTime.TryDay(last, out var to)) return new List<string>(keys);
            var step = bucket == "week" ? 7 : 1;
            var list = new List<string>();
            for (var d = from; d <= to && list.Count < 3660; d = d.AddDays(step)) list.Add(ReportTime.DayKey(d));
            return list;
        }
    }
}
