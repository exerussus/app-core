using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Analytics.Reports
{
    /// <summary>
    /// Тип представления — один из примитивов, которые AppCore умеет считать и рисовать. Смысл (что такое
    /// «модуль», «ход», «уровень») задаёт проект: он привязывает к представлению свои события.
    /// </summary>
    public enum ViewType : byte
    {
        Counter, Interval, Funnel, Breakdown, Distribution, Outcome, Lane, Share, Conversion,
        Series, Activity, Retention, Progress, Heatmap, Correlation, Transitions, State, Flow,
    }

    /// <summary>Роль события в представлении.</summary>
    public enum BindRole : byte
    {
        /// <summary>Событие-факт: счётчик, точка на графике, метка на таймлайне.</summary>
        Point,
        /// <summary>Начало интервала (пара — по представлению и субъекту).</summary>
        Begin,
        /// <summary>Конец интервала: закрывает последнее открытое начало того же субъекта.</summary>
        End,
        /// <summary>Шаг: субъект — имя шага (воронка, прогресс, переходы).</summary>
        Step,
        /// <summary>Шаг пройден (воронка): субъект — имя шага.</summary>
        Done,
        /// <summary>Значение: поле value — класс исхода или значение состояния.</summary>
        Value,
        /// <summary>Конверсия: первое событие.</summary>
        A,
        /// <summary>Конверсия: целевое событие.</summary>
        B,
        /// <summary>Корреляция: ось X.</summary>
        X,
        /// <summary>Корреляция: ось Y.</summary>
        Y,
    }

    /// <summary>Окраска: нейтрально, успех, провал, серый (служебное — не учитывать в долях успеха).</summary>
    public enum Tone : byte
    {
        Neutral, Positive, Negative, Gray,
    }

    /// <summary>Что считается единицей: события, прогоны, сессии, игроки.</summary>
    public enum CountUnit : byte
    {
        Events, Runs, Sessions, Profiles,
    }

    /// <summary>Привязка события к представлению: роль и какие поля события чем служат.</summary>
    public sealed class ViewBinding
    {
        public string View;
        public BindRole Role;
        /// <summary>Строковое поле-субъект: по нему пары интервалов, шаги, группы.</summary>
        public string Subject;
        /// <summary>Числовое поле-мера: агрегаты, распределение, сумма потока.</summary>
        public string Measure;
        /// <summary>Поле значения: класс исхода, значение состояния.</summary>
        public string Value;
        public Tone Tone;
        /// <summary>Нераспознанная роль/окраска из файла — сохраняется как была и выдаётся проверкой ошибкой.</summary>
        public string BadRole;
        public string BadTone;

        public ViewBinding Clone() => (ViewBinding)MemberwiseClone();
    }

    /// <summary>Классификация события каталога: куда оно входит или «только сырые данные».</summary>
    public sealed class EventClass
    {
        public string Key;
        /// <summary>Событие сознательно не входит ни в одно представление — только в сырые данные и экспорт.</summary>
        public bool Raw;
        public readonly List<ViewBinding> Bindings = new List<ViewBinding>();
    }

    public sealed class OutcomeClass
    {
        public string Value;
        public Tone Tone;
        public string BadTone;
        public string Label;
    }

    public sealed class ComputedField
    {
        public string Name;
        public string Expr;
    }

    public sealed class ViewTarget
    {
        /// <summary>Агрегат, к которому относится цель: median, p90, count, share… Пусто — главный показатель.</summary>
        public string Agg;
        public double Value = double.NaN;
        /// <summary>true — лучше меньше (время, ошибки), false — лучше больше.</summary>
        public bool LowerIsBetter = true;

        public bool IsSet => !double.IsNaN(Value);
    }

    /// <summary>Представление: шаблон, куда идут события, как группируются и как выгружаются.</summary>
    public sealed class ReportView
    {
        public string Id;
        public string Title;
        public string Note;
        public ViewType Type;
        /// <summary>Встроенное представление AppCore (не из файла проекта).</summary>
        public bool Builtin;
        /// <summary>Скрыть (для встроенного — выключить его из отчёта).</summary>
        public bool Hidden;
        /// <summary>Нераспознанный тип/единица из файла — сохраняются как были; представление не считается, проверка — ошибка.</summary>
        public string BadType;
        public string BadUnit;

        /// <summary>Маски ключей (<c>Level.*</c>): все подходящие события входят с привязкой <see cref="Bind"/>.</summary>
        public readonly List<string> Keys = new List<string>();
        public ViewBinding Bind;

        public CountUnit? Unit;
        public readonly List<string> GroupBy = new List<string>();
        /// <summary>Агрегаты: <c>count</c>, <c>share</c>, <c>sum:f</c>, <c>avg:f</c>, <c>median:f</c>, <c>p90:f</c>, <c>min:f</c>, <c>max:f</c>.</summary>
        public readonly List<string> Aggs = new List<string>();
        /// <summary>Фильтр событий: выражение (<c>level = "boss" &amp;&amp; time > 5</c>).</summary>
        public string Where;
        /// <summary>Разбить по измерению для сравнения: <c>@build</c>, <c>@prop.ab</c>.</summary>
        public string Compare;
        /// <summary>Сравнить с прошлым периодом той же длины.</summary>
        public bool ComparePrevious;
        public ViewTarget Target;
        /// <summary>Минимальная выборка; 0 — из настроек файла.</summary>
        public int MinSample;

        /// <summary>Закрепить на обзоре.</summary>
        public bool Pin;
        public int Order;
        /// <summary>Показывать на странице тега (по прогонам тега).</summary>
        public bool? OnTag;
        /// <summary>Показывать на таймлайне прогона (дорожки, интервалы, состояния).</summary>
        public bool? OnRun;

        /// <summary>Выгружать таблицей в CSV и JSON.</summary>
        public bool Export = true;

        public readonly List<ComputedField> Computed = new List<ComputedField>();
        public readonly List<OutcomeClass> Outcomes = new List<OutcomeClass>();
        /// <summary>Порядок шагов (воронка, прогресс); пусто — по медиане первого появления.</summary>
        public readonly List<string> Steps = new List<string>();
        /// <summary>Источник значения без привязок: измерение (<c>@result</c>) — для «Исхода» по прогонам.</summary>
        public string Value;
        /// <summary>Шаг времени ряда: hour | day | week.</summary>
        public string Bucket;
        /// <summary>Дни удержания: 1, 3, 7…</summary>
        public readonly List<int> Periods = new List<int>();
        public int Bins;
        /// <summary>Тепловая карта: измерения осей.</summary>
        public string X;
        public string Y;
        /// <summary>Ограничение строк (разбивка, переходы); 0 — по умолчанию.</summary>
        public int Top;
        /// <summary>Конверсия: окно в секундах от A до B; 0 — без окна.</summary>
        public double Within;
        /// <summary>Часы длительностей в прогоне: game — игровое время (стоит на паузе), real — реальное. По умолчанию game, если игра его пишет.</summary>
        public string Clock;

        public bool RealClock => string.Equals(Clock, "real", StringComparison.OrdinalIgnoreCase);

        public CountUnit EffectiveUnit => Unit ?? ViewTypes.DefaultUnit(Type, Value);
        public bool ShowOnTag => OnTag ?? ViewTypes.DefaultOnTag(Type);
        public bool ShowOnRun => OnRun ?? ViewTypes.DefaultOnRun(Type);

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Id : Title.Trim();
    }

    /// <summary>Профиль экспорта: что, в каком формате и с каким фильтром выгружать. Хранится в файле — общий для команды.</summary>
    public sealed class ExportProfile
    {
        public string Id;
        public string Title;
        /// <summary>Представления; пусто — все.</summary>
        public readonly List<string> Views = new List<string>();
        /// <summary>html, csv, json, jsonl.</summary>
        public readonly List<string> Formats = new List<string>();
        /// <summary>Добавить сырые события (таблица events, JSONL).</summary>
        public bool Raw;
        public bool Anonymize;
        /// <summary>Последние N дней (0 — без ограничения). Перекрывает from/to.</summary>
        public int LastDays;
        public string From;
        public string To;
        public readonly List<string> Tags = new List<string>();
        public readonly List<string> Builds = new List<string>();
        public readonly List<string> Platforms = new List<string>();
        public string TimeZone;
    }

    /// <summary>Свойство игрока или сессии (<c>AppAnalytics.SetProperty</c>) — для подсказок в редакторе.</summary>
    public sealed class PropertyInfo
    {
        public string Name;
        public string Title;
        public string Note;
    }

    /// <summary>
    /// Файл представлений проекта (<c>Assets/App/Settings/Editor/AnalyticsReport.json</c>): представления,
    /// классификация событий каталога, профили экспорта. Только редактор — в сборку не попадает.
    /// </summary>
    public sealed class ReportConfig
    {
        public const int CurrentSchema = 1;
        public int Schema = CurrentSchema;
        /// <summary>Часовой пояс для «по дням»: UTC, +03:00 или id системы.</summary>
        public string TimeZone = "UTC";
        public int MinSample = 5;
        /// <summary>Сколько последних прогонов кладётся в report.html с таймлайном.</summary>
        public int TimelineRuns = 300;

        public readonly List<ReportView> Views = new List<ReportView>();
        public readonly List<EventClass> Events = new List<EventClass>();
        public readonly List<ExportProfile> Profiles = new List<ExportProfile>();
        public readonly List<PropertyInfo> Properties = new List<PropertyInfo>();

        public ReportView FindView(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var v in Views)
                if (v.Id == id) return v;
            return null;
        }

        public EventClass FindEvent(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var e in Events)
                if (e.Key == key) return e;
            return null;
        }

        public EventClass GetOrAddEvent(string key)
        {
            var e = FindEvent(key);
            if (e != null) return e;
            e = new EventClass { Key = key };
            Events.Add(e);
            return e;
        }

        public ExportProfile FindProfile(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in Profiles)
                if (p.Id == id) return p;
            return null;
        }

        /// <summary>Представления отчёта: встроенные AppCore (если проект их не перекрыл) + проекта, без скрытых.</summary>
        public List<ReportView> EffectiveViews()
        {
            var result = new List<ReportView>();
            foreach (var b in ViewTypes.Builtins())
                if (FindView(b.Id) == null) result.Add(b);
            foreach (var v in Views)
                if (!v.Hidden) result.Add(v);
            return result;
        }

        /// <summary>
        /// Привязки события: явные (из классификации) + по маскам ключей представлений. Явная привязка к тому же
        /// представлению с той же ролью перекрывает маску.
        /// </summary>
        public List<ViewBinding> BindingsOf(string key, List<ReportView> views = null)
        {
            var result = new List<ViewBinding>();
            var explicitClass = FindEvent(key);
            if (explicitClass != null)
                foreach (var b in explicitClass.Bindings)
                    if (b.BadRole == null) result.Add(b);

            foreach (var v in views ?? EffectiveViews())
            {
                if (v.Keys.Count == 0 || v.Bind == null || v.Bind.BadRole != null || !MatchesAny(v.Keys, key)) continue;
                var duplicate = false;
                foreach (var b in result)
                    if (b.View == v.Id && b.Role == v.Bind.Role) duplicate = true;
                if (duplicate) continue;
                var copy = v.Bind.Clone();
                copy.View = v.Id;
                result.Add(copy);
            }

            return result;
        }

        public static bool MatchesAny(List<string> masks, string key)
        {
            foreach (var m in masks)
                if (Matches(m, key)) return true;
            return false;
        }

        /// <summary>Маска ключа: точное совпадение, <c>A.B.*</c> — всё под A.B, <c>*</c> — всё, <c>*.X</c> — оканчивается на .X.</summary>
        public static bool Matches(string mask, string key)
        {
            if (string.IsNullOrEmpty(mask) || key == null) return false;
            if (mask == "*") return true;
            if (mask.EndsWith(".*", StringComparison.Ordinal))
                return key.StartsWith(mask.Substring(0, mask.Length - 1), StringComparison.Ordinal);
            if (mask.StartsWith("*.", StringComparison.Ordinal))
                return key.EndsWith(mask.Substring(1), StringComparison.Ordinal);
            return string.Equals(mask, key, StringComparison.Ordinal);
        }

        // ============================================================ JSON

        public static ReportConfig Parse(string json)
        {
            var root = JsonNode.Parse(json);
            if (!root.IsObject) throw new FormatException("файл представлений: ожидался объект");
            return FromNode(root);
        }

        public static ReportConfig FromNode(JsonNode root)
        {
            var c = new ReportConfig
            {
                Schema = root.Int("schema", CurrentSchema),
                TimeZone = root.Str("timezone", "UTC"),
                MinSample = Math.Max(0, root.Int("minSample", 5)),
                TimelineRuns = Math.Max(0, root.Int("timelineRuns", 300)),
            };

            var views = root["views"];
            if (views != null)
                foreach (var n in views.Items)
                    if (n.IsObject) c.Views.Add(ReadView(n));

            var events = root["events"];
            if (events != null && events.IsObject)
            {
                foreach (var p in events.Props)
                {
                    var e = new EventClass { Key = p.Key };
                    var n = p.Value;
                    if (n.Kind == JsonNodeKind.String && n.Text == "raw") e.Raw = true;
                    else if (n.IsArray)
                    {
                        foreach (var b in n.Items)
                            if (b.IsObject) e.Bindings.Add(ReadBinding(b));
                    }
                    else if (n.IsObject)
                    {
                        e.Raw = n.Bool("raw");
                        var list = n["bind"];
                        if (list != null)
                            foreach (var b in list.Items)
                                if (b.IsObject) e.Bindings.Add(ReadBinding(b));
                    }

                    c.Events.Add(e);
                }
            }

            var profiles = root["profiles"];
            if (profiles != null)
                foreach (var n in profiles.Items)
                    if (n.IsObject) c.Profiles.Add(ReadProfile(n));

            var props = root["properties"];
            if (props != null)
                foreach (var n in props.Items)
                    if (n.IsObject) c.Properties.Add(new PropertyInfo { Name = n.Str("name"), Title = n.Str("title"), Note = n.Str("note") });

            return c;
        }

        private static ReportView ReadView(JsonNode n)
        {
            var v = new ReportView
            {
                Id = n.Str("id"),
                Title = n.Str("title"),
                Note = n.Str("note"),
                Type = ViewTypes.ParseType(n.Str("type"), out var typeOk),
                Hidden = n.Bool("hidden"),
                Where = n.Str("where"),
                Compare = n.Str("compare"),
                ComparePrevious = n.Bool("comparePrevious"),
                MinSample = n.Int("minSample"),
                Pin = n.Bool("pin"),
                Order = n.Int("order"),
                Export = n.Bool("export", true),
                Value = n.Str("value"),
                Bucket = n.Str("bucket"),
                Bins = n.Int("bins"),
                X = n.Str("x"),
                Y = n.Str("y"),
                Top = n.Int("top"),
                Within = n.Num("within", 0),
                Clock = n.Str("clock"),
            };
            if (!typeOk) v.BadType = n.Str("type") ?? string.Empty;

            if (n["onTag"] != null) v.OnTag = n.Bool("onTag");
            if (n["onRun"] != null) v.OnRun = n.Bool("onRun");
            var unit = n.Str("unit");
            if (!string.IsNullOrEmpty(unit))
            {
                if (ViewTypes.TryParseUnit(unit, out var u)) v.Unit = u;
                else v.BadUnit = unit;
            }

            v.Keys.AddRange(n.Strings("keys"));
            var bind = n["bind"];
            if (bind != null && bind.IsObject) v.Bind = ReadBinding(bind);
            else if (v.Keys.Count > 0) v.Bind = new ViewBinding { Role = ViewTypes.DefaultRole(v.Type) };

            v.GroupBy.AddRange(n.Strings("groupBy"));
            v.Aggs.AddRange(n.Strings("aggs"));
            v.Steps.AddRange(n.Strings("steps"));

            var periods = n["periods"];
            if (periods != null)
                foreach (var p in periods.Items)
                    if (p.Kind == JsonNodeKind.Number && p.Number >= 1 && p.Number <= 3650) v.Periods.Add((int)p.Number);

            var target = n["target"];
            if (target != null && target.IsObject)
                v.Target = new ViewTarget { Agg = target.Str("agg"), Value = target.Num("value"), LowerIsBetter = target.Str("better", "lower") != "higher" };

            var computed = n["computed"];
            if (computed != null)
                foreach (var cf in computed.Items)
                    if (cf.IsObject) v.Computed.Add(new ComputedField { Name = cf.Str("name"), Expr = cf.Str("expr") });

            var outcomes = n["outcomes"];
            if (outcomes != null)
                foreach (var o in outcomes.Items)
                    if (o.IsObject)
                    {
                        var tone = o.Str("tone");
                        var oc = new OutcomeClass { Value = o.Str("value"), Tone = ViewTypes.ParseTone(tone), Label = o.Str("label") };
                        if (!ViewTypes.IsTone(tone)) oc.BadTone = tone;
                        v.Outcomes.Add(oc);
                    }

            return v;
        }

        private static ViewBinding ReadBinding(JsonNode n)
        {
            var b = new ViewBinding
            {
                View = n.Str("view"),
                Role = ViewTypes.ParseRole(n.Str("role"), out var roleOk),
                Subject = Clean(n.Str("subject")),
                Measure = Clean(n.Str("measure")),
                Value = Clean(n.Str("value")),
                Tone = ViewTypes.ParseTone(n.Str("tone")),
            };
            if (!roleOk) b.BadRole = n.Str("role") ?? string.Empty;
            if (!ViewTypes.IsTone(n.Str("tone"))) b.BadTone = n.Str("tone");
            return b;
        }

        private static ExportProfile ReadProfile(JsonNode n)
        {
            var p = new ExportProfile
            {
                Id = n.Str("id"),
                Title = n.Str("title"),
                Raw = n.Bool("raw"),
                Anonymize = n.Bool("anonymize"),
                LastDays = Math.Max(0, n.Int("lastDays")),
                From = n.Str("from"),
                To = n.Str("to"),
                TimeZone = n.Str("timezone"),
            };
            p.Views.AddRange(n.Strings("views"));
            p.Formats.AddRange(n.Strings("formats"));
            p.Tags.AddRange(n.Strings("tags"));
            p.Builds.AddRange(n.Strings("builds"));
            p.Platforms.AddRange(n.Strings("platforms"));
            return p;
        }

        private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        public string ToJson() => ToNode().ToPretty();

        public JsonNode ToNode()
        {
            var root = JsonNode.Obj()
                               .Set("schema", Schema)
                               .Set("timezone", string.IsNullOrWhiteSpace(TimeZone) ? "UTC" : TimeZone)
                               .Set("minSample", MinSample)
                               .Set("timelineRuns", TimelineRuns);

            var views = JsonNode.Arr();
            foreach (var v in Views) views.Add(WriteView(v));
            root.Set("views", views);

            var events = JsonNode.Obj();
            var sorted = new List<EventClass>(Events);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            foreach (var e in sorted)
            {
                if (string.IsNullOrEmpty(e.Key)) continue;
                if (e.Raw && e.Bindings.Count == 0)
                {
                    events.Set(e.Key, "raw");
                    continue;
                }

                if (e.Bindings.Count == 0) continue;
                var arr = JsonNode.Arr();
                foreach (var b in e.Bindings) arr.Add(WriteBinding(b, true));
                events.Set(e.Key, arr);
            }
            root.Set("events", events);

            var profiles = JsonNode.Arr();
            foreach (var p in Profiles) profiles.Add(WriteProfile(p));
            root.Set("profiles", profiles);

            var props = JsonNode.Arr();
            foreach (var p in Properties)
                props.Add(JsonNode.Obj().Set("name", p.Name ?? string.Empty).SetIf("title", p.Title).SetIf("note", p.Note));
            root.Set("properties", props);
            return root;
        }

        public static JsonNode WriteView(ReportView v)
        {
            var n = JsonNode.Obj().Set("id", v.Id ?? string.Empty).Set("type", v.BadType ?? ViewTypes.Name(v.Type)).SetIf("title", v.Title).SetIf("note", v.Note);
            if (v.Hidden) n.Set("hidden", true);
            n.SetIf("keys", v.Keys);
            if (v.Bind != null && v.Keys.Count > 0) n.Set("bind", WriteBinding(v.Bind, false));
            if (v.BadUnit != null) n.Set("unit", v.BadUnit);
            else if (v.Unit != null) n.Set("unit", ViewTypes.Name(v.Unit.Value));
            n.SetIf("groupBy", v.GroupBy).SetIf("aggs", v.Aggs).SetIf("where", v.Where).SetIf("compare", v.Compare);
            if (v.ComparePrevious) n.Set("comparePrevious", true);
            if (v.Target != null && v.Target.IsSet)
                n.Set("target", JsonNode.Obj().SetIf("agg", v.Target.Agg).Set("value", v.Target.Value).Set("better", v.Target.LowerIsBetter ? "lower" : "higher"));
            if (v.MinSample > 0) n.Set("minSample", v.MinSample);
            if (v.Pin) n.Set("pin", true);
            if (v.Order != 0) n.Set("order", v.Order);
            if (v.OnTag != null) n.Set("onTag", v.OnTag.Value);
            if (v.OnRun != null) n.Set("onRun", v.OnRun.Value);
            if (!v.Export) n.Set("export", false);
            n.SetIf("value", v.Value).SetIf("steps", v.Steps).SetIf("bucket", v.Bucket).SetIf("x", v.X).SetIf("y", v.Y);
            if (v.Periods.Count > 0)
            {
                var arr = JsonNode.Arr();
                foreach (var p in v.Periods) arr.Add(JsonNode.Of(p));
                n.Set("periods", arr);
            }
            if (v.Bins > 0) n.Set("bins", v.Bins);
            if (v.Top > 0) n.Set("top", v.Top);
            if (v.Within > 0) n.Set("within", v.Within);
            n.SetIf("clock", v.Clock);

            if (v.Computed.Count > 0)
            {
                var arr = JsonNode.Arr();
                foreach (var c in v.Computed) arr.Add(JsonNode.Obj().Set("name", c.Name ?? string.Empty).Set("expr", c.Expr ?? string.Empty));
                n.Set("computed", arr);
            }

            if (v.Outcomes.Count > 0)
            {
                var arr = JsonNode.Arr();
                foreach (var o in v.Outcomes)
                    arr.Add(JsonNode.Obj().Set("value", o.Value ?? string.Empty).Set("tone", o.BadTone ?? ViewTypes.Name(o.Tone)).SetIf("label", o.Label));
                n.Set("outcomes", arr);
            }

            return n;
        }

        public static JsonNode WriteBinding(ViewBinding b, bool withView)
        {
            var n = JsonNode.Obj();
            if (withView) n.Set("view", b.View ?? string.Empty);
            n.Set("role", b.BadRole ?? ViewTypes.Name(b.Role)).SetIf("subject", b.Subject).SetIf("measure", b.Measure).SetIf("value", b.Value);
            if (b.BadTone != null) n.Set("tone", b.BadTone);
            else if (b.Tone != Tone.Neutral) n.Set("tone", ViewTypes.Name(b.Tone));
            return n;
        }

        private static JsonNode WriteProfile(ExportProfile p)
        {
            var n = JsonNode.Obj().Set("id", p.Id ?? string.Empty).SetIf("title", p.Title).SetIf("views", p.Views).SetIf("formats", p.Formats);
            if (p.Raw) n.Set("raw", true);
            if (p.Anonymize) n.Set("anonymize", true);
            if (p.LastDays > 0) n.Set("lastDays", p.LastDays);
            n.SetIf("from", p.From).SetIf("to", p.To).SetIf("tags", p.Tags).SetIf("builds", p.Builds).SetIf("platforms", p.Platforms).SetIf("timezone", p.TimeZone);
            return n;
        }
    }
}
