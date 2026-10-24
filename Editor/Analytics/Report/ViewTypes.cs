using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Analytics.Reports
{
    /// <summary>Справочник типов представлений: имена, роли, что требуется от привязки, описания для редактора.</summary>
    public static class ViewTypes
    {
        public sealed class Info
        {
            public ViewType Type;
            public string Name;
            public string Title;
            public string Chart;
            public string Example;
            /// <summary>Допустимые роли привязок (пусто — привязки не нужны: встроенное из сессий).</summary>
            public BindRole[] Roles;
            /// <summary>Роли, без которых представление пустое.</summary>
            public BindRole[] Required;
        }

        private static readonly Info[] All =
        {
            new Info { Type = ViewType.Counter, Name = "counter", Title = "Счётчик", Chart = "Число, изменение к прошлому периоду, по дням",
                       Example = "Сколько раз открыли магазин", Roles = new[] { BindRole.Point }, Required = new[] { BindRole.Point } },
            new Info { Type = ViewType.Interval, Name = "interval", Title = "Интервал", Chart = "Полосы на таймлайне, медиана и p90 длительности",
                       Example = "Сколько длится бой с каждым врагом", Roles = new[] { BindRole.Begin, BindRole.End }, Required = new[] { BindRole.Begin } },
            new Info { Type = ViewType.Funnel, Name = "funnel", Title = "Воронка", Chart = "Ступени с процентом отвала",
                       Example = "На каком шаге обучения бросают", Roles = new[] { BindRole.Step, BindRole.Done }, Required = new[] { BindRole.Step } },
            new Info { Type = ViewType.Breakdown, Name = "breakdown", Title = "Разбивка", Chart = "Таблица и столбцы по группам",
                       Example = "Покупки по товарам и платформам", Roles = new[] { BindRole.Point }, Required = new[] { BindRole.Point } },
            new Info { Type = ViewType.Distribution, Name = "distribution", Title = "Распределение", Chart = "Гистограмма, перцентили, «ящик с усами»",
                       Example = "Разброс времени прохождения уровня", Roles = new[] { BindRole.Point }, Required = new[] { BindRole.Point } },
            new Info { Type = ViewType.Outcome, Name = "outcome", Title = "Исход", Chart = "Доли классов значения",
                       Example = "Чем заканчиваются забеги", Roles = new[] { BindRole.Value }, Required = Array.Empty<BindRole>() },
            new Info { Type = ViewType.Lane, Name = "lane", Title = "Дорожка таймлайна", Chart = "Метки на таймлайне прогона",
                       Example = "Где в игре были ошибки игрока", Roles = new[] { BindRole.Point }, Required = new[] { BindRole.Point } },
            new Info { Type = ViewType.Share, Name = "share", Title = "Доля", Chart = "Процент и тренд",
                       Example = "Сколько процентов забегов использовали подсказку", Roles = new[] { BindRole.Point }, Required = new[] { BindRole.Point } },
            new Info { Type = ViewType.Conversion, Name = "conversion", Title = "Конверсия A → B", Chart = "Процент и время до B",
                       Example = "Из открывших магазин — сколько купили и за сколько", Roles = new[] { BindRole.A, BindRole.B }, Required = new[] { BindRole.A, BindRole.B } },
            new Info { Type = ViewType.Series, Name = "series", Title = "Временной ряд", Chart = "Линия по часам, дням или неделям",
                       Example = "Покупки по дням", Roles = new[] { BindRole.Point }, Required = new[] { BindRole.Point } },
            new Info { Type = ViewType.Activity, Name = "activity", Title = "Активность", Chart = "DAU / WAU / MAU, сессий на игрока, длина сессии",
                       Example = "Жива ли аудитория", Roles = Array.Empty<BindRole>(), Required = Array.Empty<BindRole>() },
            new Info { Type = ViewType.Retention, Name = "retention", Title = "Удержание", Chart = "Когорты D1 / D3 / D7 / D30",
                       Example = "Возвращаются ли игроки", Roles = Array.Empty<BindRole>(), Required = Array.Empty<BindRole>() },
            new Info { Type = ViewType.Progress, Name = "progress", Title = "Прогресс", Chart = "Максимальный шаг на игрока, где застревают",
                       Example = "До какого уровня доходят", Roles = new[] { BindRole.Step }, Required = new[] { BindRole.Step } },
            new Info { Type = ViewType.Heatmap, Name = "heatmap", Title = "Тепловая карта", Chart = "Две оси, цвет — значение",
                       Example = "Уровень × оружие: где дольше всего", Roles = new[] { BindRole.Point }, Required = new[] { BindRole.Point } },
            new Info { Type = ViewType.Correlation, Name = "correlation", Title = "Корреляция", Chart = "Точки по двум числам, коэффициент r",
                       Example = "Связано ли время уровня с числом смертей", Roles = new[] { BindRole.X, BindRole.Y }, Required = new[] { BindRole.X, BindRole.Y } },
            new Info { Type = ViewType.Transitions, Name = "transitions", Title = "Переходы", Chart = "Матрица «что идёт после чего»",
                       Example = "Куда игрок идёт после меню", Roles = new[] { BindRole.Step }, Required = new[] { BindRole.Step } },
            new Info { Type = ViewType.State, Name = "state", Title = "Состояние", Chart = "Время в каждом значении, последнее значение",
                       Example = "Сколько времени игра была на паузе", Roles = new[] { BindRole.Value }, Required = new[] { BindRole.Value } },
            new Info { Type = ViewType.Flow, Name = "flow", Title = "Поток ресурса", Chart = "Приход и расход по источникам, баланс во времени",
                       Example = "Откуда берутся и куда уходят монеты", Roles = new[] { BindRole.Point }, Required = new[] { BindRole.Point } },
        };

        public static IReadOnlyList<Info> List => All;

        public static Info Of(ViewType t) => All[(int)t];

        public static string Name(ViewType t) => All[(int)t].Name;

        public static ViewType ParseType(string s, out bool ok)
        {
            foreach (var i in All)
                if (string.Equals(i.Name, s, StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    return i.Type;
                }

            ok = false;
            return ViewType.Counter;
        }

        private static readonly string[] RoleNames = { "point", "begin", "end", "step", "done", "value", "a", "b", "x", "y" };
        private static readonly string[] RoleTitles = { "Факт", "Начало", "Конец", "Шаг", "Шаг пройден", "Значение", "A (первое)", "B (цель)", "Ось X", "Ось Y" };

        public static string Name(BindRole r) => RoleNames[(int)r];
        public static string Title(BindRole r) => RoleTitles[(int)r];

        public static BindRole ParseRole(string s, out bool ok)
        {
            for (var i = 0; i < RoleNames.Length; i++)
                if (string.Equals(RoleNames[i], s, StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    return (BindRole)i;
                }

            ok = false;
            return BindRole.Point;
        }

        private static readonly string[] ToneNames = { "neutral", "positive", "negative", "gray" };
        private static readonly string[] ToneTitles = { "Нейтрально", "Успех", "Провал", "Служебное" };

        public static string Name(Tone t) => ToneNames[(int)t];
        public static string Title(Tone t) => ToneTitles[(int)t];

        /// <summary>Окраска распознана (пусто — нейтрально, тоже верно).</summary>
        public static bool IsTone(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            foreach (var t in ToneNames)
                if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static Tone ParseTone(string s)
        {
            for (var i = 0; i < ToneNames.Length; i++)
                if (string.Equals(ToneNames[i], s, StringComparison.OrdinalIgnoreCase)) return (Tone)i;
            return Tone.Neutral;
        }

        private static readonly string[] UnitNames = { "events", "runs", "sessions", "profiles" };
        private static readonly string[] UnitTitles = { "События", "Прогоны", "Сессии", "Игроки" };

        private static readonly string[] UnitGenitive = { "событий", "прогонов", "сессий", "игроков" };

        public static string Name(CountUnit u) => UnitNames[(int)u];
        /// <summary>«прогонов», «игроков» — для подписей «доля прогонов…».</summary>
        public static string Genitive(CountUnit u) => UnitGenitive[(int)u];
        public static string Title(CountUnit u) => UnitTitles[(int)u];

        public static bool TryParseUnit(string s, out CountUnit unit)
        {
            for (var i = 0; i < UnitNames.Length; i++)
                if (string.Equals(UnitNames[i], s, StringComparison.OrdinalIgnoreCase))
                {
                    unit = (CountUnit)i;
                    return true;
                }

            unit = CountUnit.Events;
            return false;
        }

        public static bool Allows(ViewType t, BindRole r) => Array.IndexOf(Of(t).Roles, r) >= 0;

        public static BindRole DefaultRole(ViewType t)
        {
            var roles = Of(t).Roles;
            return roles.Length > 0 ? roles[0] : BindRole.Point;
        }

        public static CountUnit DefaultUnit(ViewType t, string value = null)
        {
            switch (t)
            {
                case ViewType.Funnel:
                case ViewType.Share:
                case ViewType.Conversion:
                case ViewType.Correlation:
                case ViewType.Transitions:
                case ViewType.State:
                    return CountUnit.Runs;
                case ViewType.Outcome:
                    return !string.IsNullOrEmpty(value) && value.StartsWith("@", StringComparison.Ordinal) ? CountUnit.Runs : CountUnit.Events;
                case ViewType.Activity:
                case ViewType.Retention:
                case ViewType.Progress:
                    return CountUnit.Profiles;
                default:
                    return CountUnit.Events;
            }
        }

        public static bool DefaultOnTag(ViewType t) => t != ViewType.Activity && t != ViewType.Retention && t != ViewType.Lane;

        public static bool DefaultOnRun(ViewType t) => t == ViewType.Lane || t == ViewType.Interval || t == ViewType.State;

        /// <summary>Нужна ли привязке мера (числовое поле).</summary>
        public static bool NeedsMeasure(ViewType t, BindRole r) => (t == ViewType.Distribution || t == ViewType.Flow) && r == BindRole.Point;

        /// <summary>Нужен ли привязке субъект (строковое поле).</summary>
        public static bool NeedsSubject(ViewType t, BindRole r) =>
            r == BindRole.Step || r == BindRole.Done;

        /// <summary>Нужно ли привязке поле значения.</summary>
        public static bool NeedsValue(ViewType t, BindRole r) => r == BindRole.Value;

        // ============================================================ измерения

        /// <summary>Встроенные измерения для группировки, сравнения и выражений.</summary>
        public static readonly string[] Dimensions =
        {
            "@tag", "@result", "@build", "@platform", "@profile", "@session", "@run", "@day", "@week", "@hour", "@weekday", "@event",
        };

        public static string DimensionTitle(string dim)
        {
            switch (dim)
            {
                case "@tag": return "Тег прогона";
                case "@result": return "Итог прогона";
                case "@build": return "Билд";
                case "@platform": return "Платформа";
                case "@profile": return "Игрок";
                case "@session": return "Сессия";
                case "@run": return "Прогон";
                case "@day": return "День";
                case "@week": return "Неделя";
                case "@hour": return "Час суток";
                case "@weekday": return "День недели";
                case "@event": return "Ключ события";
                case "subject": return "Субъект привязки";
                case "value": return "Значение привязки";
                case "measure": return "Мера привязки";
                default:
                    if (dim != null && dim.StartsWith("@prop.", StringComparison.Ordinal)) return "Свойство «" + dim.Substring(6) + "»";
                    return dim;
            }
        }

        /// <summary>Измерение — уровня прогона/сессии/игрока (одно значение на единицу), а не события.</summary>
        public static bool IsUnitLevel(string dim, CountUnit unit)
        {
            if (string.IsNullOrEmpty(dim) || dim[0] != '@') return false;
            switch (dim)
            {
                case "@profile": return true;
                case "@build":
                case "@platform":
                case "@session":
                    return unit != CountUnit.Profiles;
                case "@tag":
                case "@result":
                case "@run":
                    return unit == CountUnit.Runs;
                default:
                    return dim.StartsWith("@prop.", StringComparison.Ordinal) && unit != CountUnit.Events;
            }
        }

        // ============================================================ встроенные

        public const string BuiltinActivity = "app_activity";
        public const string BuiltinRetention = "app_retention";
        public const string BuiltinRuns = "app_runs";

        /// <summary>
        /// Встроенные представления AppCore — только по тому, что AppCore пишет сам (сессии, прогоны). Проект
        /// перекрывает их представлением с тем же id (например, добавляет классы своих итогов в app_runs)
        /// или скрывает (<c>hidden</c>).
        /// </summary>
        public static List<ReportView> Builtins()
        {
            var activity = new ReportView { Id = BuiltinActivity, Title = "Активность", Type = ViewType.Activity, Builtin = true, Pin = true, Order = -30 };
            var retention = new ReportView { Id = BuiltinRetention, Title = "Удержание", Type = ViewType.Retention, Builtin = true, Pin = true, Order = -20 };
            retention.Periods.AddRange(new[] { 1, 3, 7, 14, 30 });
            var runs = BuiltinRunsView();
            return new List<ReportView> { activity, retention, runs };
        }

        /// <summary>Итоги прогонов: значение — <c>@result</c> (из app.run.end, незавершённые — unfinished).</summary>
        public static ReportView BuiltinRunsView()
        {
            var runs = new ReportView { Id = BuiltinRuns, Title = "Итоги прогонов", Type = ViewType.Outcome, Builtin = true, Pin = true, Order = -10, Value = "@result" };
            AddCoreOutcomes(runs);
            return runs;
        }

        /// <summary>Итоги, которые пишет сам AppCore: «не завершён» и «прерван новым прогоном» — служебные.</summary>
        public static void AddCoreOutcomes(ReportView view)
        {
            void Add(string value, string label)
            {
                foreach (var o in view.Outcomes)
                    if (o.Value == value) return;
                view.Outcomes.Add(new OutcomeClass { Value = value, Tone = Tone.Gray, Label = label });
            }

            Add(ReportBuilder.Unfinished, "не завершён");
            Add(ReportBuilder.Interrupted, "прерван новым прогоном");
            Add(ReportBuilder.NoResult, "без итога");
        }
    }
}
