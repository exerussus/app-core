using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Analytics.Reports
{
    public static partial class ReportViews
    {
        /// <summary>
        /// Таймлайны последних прогонов (до <see cref="ReportConfig.TimelineRuns"/>): дорожки представлений
        /// «Дорожка» (метки), «Интервал» (полосы) и «Состояние» (участки). Считаются здесь — report.html не тащит события.
        /// </summary>
        private static void BuildTimelines(ReportData data, Dictionary<string, List<ViewBinding>> index, ReportConfig config)
        {
            var limit = config.TimelineRuns;
            if (limit <= 0 || data.Runs.Count == 0) return;

            var runs = new List<ReportRun>(data.Runs);
            runs.Sort((a, b) => Nullable.Compare(b.StartTime, a.StartTime));
            if (runs.Count > limit) runs.RemoveRange(limit, runs.Count - limit);

            var views = new List<KeyValuePair<ReportView, Ctx>>();
            foreach (var v in data.Views)
            {
                if (!v.ShowOnRun) continue;
                if (v.Type != ViewType.Lane && v.Type != ViewType.Interval && v.Type != ViewType.State && v.Type != ViewType.Counter) continue;
                var ctx = new Ctx { Data = data, View = v, Time = data.Time, Unit = CountUnit.Events, MinSample = 0, GroupBy = v.GroupBy, Aggs = v.Aggs };
                try
                {
                    Prepare(ctx, v, data.Options.Where);
                }
                catch (FormatException)
                {
                    continue; // ошибка выражения уже в предупреждениях представления
                }

                views.Add(new KeyValuePair<ReportView, Ctx>(v, ctx));
            }

            foreach (var run in runs)
            {
                var timeline = new RunTimeline { Run = run };
                var scope = new Scope { Tag = run.TagOrDash, Events = run.Events, Runs = new List<ReportRun> { run }, Sessions = new List<ReportSession> { run.Session }, Profiles = new List<string> { run.Profile } };
                foreach (var pair in views)
                {
                    var view = pair.Key;
                    var ctx = pair.Value;
                    var hits = CollectHits(ctx, view, scope, index);
                    if (hits.Count == 0) continue;

                    var lane = JsonNode.Obj().Set("view", view.Id).Set("title", view.DisplayTitle).Set("type", ViewTypes.Name(view.Type));
                    var items = JsonNode.Arr();
                    switch (view.Type)
                    {
                        // элементы — в обеих осях: [игровое начало, конец, …, реальное начало, конец]; страница переключает ось
                        case ViewType.Interval:
                        {
                            var realEnd = run.RealEnd;
                            foreach (var i in Pair(ctx, hits, out _))
                            {
                                var g0 = i.BeginEvent.Axis;
                                var g1 = i.EndEvent != null ? i.EndEvent.Axis : (ctx.View.RealClock ? run.AxisEnd : i.End);
                                var r0 = i.BeginEvent.T;
                                var r1 = i.EndEvent != null ? i.EndEvent.T : (ctx.View.RealClock ? i.End : realEnd);
                                items.Add(JsonNode.Arr().Add(N(g0)).Add(N(Math.Max(g0, g1))).Add(JsonNode.Of(i.Subject)).Add(JsonNode.Of(i.Closed))
                                                  .Add(N(r0)).Add(N(Math.Max(r0, r1))));
                            }

                            break;
                        }
                        case ViewType.State:
                        {
                            var game = StateSegments(run, new List<Hit>(hits), false);
                            var real = StateSegments(run, new List<Hit>(hits), true);
                            for (var k = 0; k < game.Count; k++)
                                items.Add(JsonNode.Arr().Add(N(game[k].start)).Add(N(game[k].end)).Add(JsonNode.Of(game[k].value))
                                                  .Add(N(real[k].start)).Add(N(real[k].end)));
                            break;
                        }
                        default:
                            foreach (var h in hits)
                            {
                                var label = h.B.Subject != null ? Text(h.E.Field(h.B.Subject)) : (h.E.Info?.Title ?? h.E.Key);
                                items.Add(JsonNode.Arr().Add(N(h.E.Axis)).Add(JsonNode.Of(ViewTypes.Name(h.B.Tone))).Add(JsonNode.Of(label)).Add(JsonNode.Of(h.E.Key))
                                                  .Add(N(h.E.T)));
                            }

                            break;
                    }

                    lane.Set("items", items);
                    timeline.Lanes.Add(lane);
                }

                data.Timelines.Add(timeline);
            }
        }
    }
}
