using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Analytics.Reports
{
    /// <summary>Событие из пакета, привязанное к сессии и (если было) прогону.</summary>
    public sealed class ReportEvent
    {
        public ReportSession Session;
        public ReportRun Run;
        public long Seq;
        /// <summary>Секунды от начала прогона (если событие в прогоне) или сессии.</summary>
        public double T;
        /// <summary>Игровое время или NaN.</summary>
        public double G = double.NaN;
        public string Key;
        public ReportEventInfo Info;
        public readonly List<KeyValuePair<string, JsonValue>> Fields = new List<KeyValuePair<string, JsonValue>>();
        /// <summary>Свойства из заголовка пакета (<c>p.*</c>, AppAnalytics.SetProperty); общий словарь пакета.</summary>
        public Dictionary<string, string> Props = ReportSession.NoProps;

        /// <summary>Ось таймлайна прогона: игровое время, если есть, иначе реальное.</summary>
        public double Axis => double.IsNaN(G) ? T : G;

        /// <summary>Секунды от начала сессии.</summary>
        public double SessionTime => Run != null && !double.IsNaN(Run.Start) ? Run.Start + T : T;

        /// <summary>Момент события (UTC) или null, если начало сессии неизвестно.</summary>
        public DateTime? Utc => ReportSession.SafeAdd(Session?.StartTime, SessionTime);

        /// <summary>Абсолютное время в секундах (для порядка между сессиями игрока); без даты — время в сессии.</summary>
        public double Absolute => Session?.StartTime != null ? (Session.StartTime.Value - DateTime.UnixEpoch).TotalSeconds + SessionTime : SessionTime;

        public string Prop(string name)
        {
            if (Props != null && Props.TryGetValue(name, out var v)) return v;
            return Session != null && Session.Props.TryGetValue(name, out v) ? v : null;
        }

        public JsonValue Field(string name)
        {
            if (string.IsNullOrEmpty(name)) return JsonValue.Null;
            foreach (var f in Fields)
                if (f.Key == name) return f.Value;
            return JsonValue.Null;
        }
    }

    public sealed class ReportSession
    {
        public static readonly Dictionary<string, string> NoProps = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Дата + секунды без исключения: вне диапазона DateTime — null.</summary>
        public static DateTime? SafeAdd(DateTime? start, double seconds)
        {
            if (start == null || double.IsNaN(seconds) || double.IsInfinity(seconds)) return start;
            var max = (DateTime.MaxValue - start.Value).TotalSeconds;
            var min = (DateTime.MinValue - start.Value).TotalSeconds;
            if (seconds >= max || seconds <= min) return null;
            return start.Value.AddSeconds(seconds);
        }

        public string Id;
        public string Profile;
        public string StartUtc;
        public DateTime? StartTime;
        public string Platform;
        public string Build;
        public string Unity;
        public readonly HashSet<string> Schemas = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<ReportRun> Runs = new List<ReportRun>();
        public readonly List<ReportEvent> Events = new List<ReportEvent>();
        public readonly HashSet<long> Seqs = new HashSet<long>();
        public double Duration;
        public bool Ended;
        public long Dropped;
        /// <summary>Пропуски в номерах событий (потерянные пакеты).</summary>
        public long Missing;
        /// <summary>Свойства сессии: последнее значение каждого <c>p.*</c> из заголовков.</summary>
        public readonly Dictionary<string, string> Props = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class ReportRun
    {
        public string Id;
        public ReportSession Session;
        public string Tag;
        public string Result;
        public bool Finished;
        /// <summary>Реальные секунды прогона (app.run.end.duration или последнее событие).</summary>
        public double Duration;
        /// <summary>Игровое время конца прогона (последнее g) или NaN.</summary>
        public double GameTime = double.NaN;
        /// <summary>Начало в секундах сессии (header.runStart) или NaN.</summary>
        public double Start = double.NaN;
        /// <summary>Сколько событий с окраской «успех» / «провал» в привязках (по всем представлениям).</summary>
        public int Positive;
        public int Negative;
        public readonly List<ReportEvent> Events = new List<ReportEvent>();

        public string Profile => Session?.Profile;
        public string TagOrDash => string.IsNullOrEmpty(Tag) ? "—" : Tag;

        public DateTime? StartTime
        {
            get
            {
                if (Session?.StartTime == null) return null;
                return ReportSession.SafeAdd(Session.StartTime, double.IsNaN(Start) ? 0 : Start);
            }
        }

        /// <summary>Конец прогона в реальных секундах.</summary>
        public double RealEnd
        {
            get
            {
                var end = Duration;
                foreach (var e in Events)
                    if (e.T > end) end = e.T;
                return end;
            }
        }

        /// <summary>Конец оси таймлайна: последнее игровое время, если прогон его пишет, иначе реальное.</summary>
        public double AxisEnd
        {
            get
            {
                var end = 0.0;
                var game = false;
                foreach (var e in Events)
                    if (!double.IsNaN(e.G))
                    {
                        if (!game || e.G > end) end = e.G;
                        game = true;
                    }

                if (game) return Math.Max(0, end);
                foreach (var e in Events)
                    if (e.T > end) end = e.T;
                return end;
            }
        }
    }

    public sealed class ReportProfile
    {
        public string Id;
        public int Sessions;
        public int Runs;
        public double Time;
        public int Negative;
        public DateTime? First;
        public DateTime? Last;
        public string Platform;
        public string Build;
        public readonly SortedDictionary<string, int> Results = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Props = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Интервал представления «Интервал» по субъекту внутри прогона или сессии.</summary>
    public sealed class ReportInterval
    {
        public ReportSession Session;
        public ReportRun Run;
        /// <summary>Id представления.</summary>
        public string Name;
        /// <summary>Событие начала (для измерений и фильтра).</summary>
        public ReportEvent BeginEvent;
        /// <summary>Событие конца или null (закрыт концом прогона/сессии, повторным началом).</summary>
        public ReportEvent EndEvent;
        public string Subject;
        /// <summary>По оси прогона (g, иначе t).</summary>
        public double Start;
        public double End;
        /// <summary>Закрыт событием конца (иначе — концом прогона/сессии).</summary>
        public bool Closed;
        /// <summary>Значение меры события конца или NaN.</summary>
        public double Measure = double.NaN;

        public double Duration => End - Start;
    }

    /// <summary>Таблица отчёта (и CSV): колонки и строки значений.</summary>
    public sealed class ReportTable
    {
        public string Id;
        public string Title;
        /// <summary>Машинные имена колонок (CSV, JSON).</summary>
        public readonly List<string> Columns = new List<string>();
        /// <summary>Подписи для людей (report.html); пусто — как Columns.</summary>
        public readonly List<string> Labels = new List<string>();
        public readonly List<JsonValue[]> Rows = new List<JsonValue[]>();

        public void Add(string column, string label)
        {
            Columns.Add(column);
            Labels.Add(label ?? column);
        }
    }

    /// <summary>Прогоны одного тега (уровня, режима…).</summary>
    public sealed class ReportTag
    {
        public string Tag;
        public readonly List<ReportRun> Runs = new List<ReportRun>();
        public readonly SortedDictionary<string, int> Results = new SortedDictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Представления, посчитанные по прогонам этого тега.</summary>
        public readonly List<ViewResult> Views = new List<ViewResult>();
    }

    public sealed class ReportOptions
    {
        /// <summary>Заменить профили на хэш (p_ + 12 знаков SHA-256).</summary>
        public bool Anonymize;
        public string Title;
        public ReportFilter Filter = new ReportFilter();
        /// <summary>Часовой пояс для «по дням»; null — из файла представлений.</summary>
        public string TimeZone;
        /// <summary>Только эти представления (id); пусто — все.</summary>
        public readonly HashSet<string> Views = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Перекрыть группировку представлений на этот вызов (CLI).</summary>
        public readonly List<string> GroupBy = new List<string>();
        /// <summary>Перекрыть агрегаты на этот вызов (CLI).</summary>
        public readonly List<string> Aggs = new List<string>();
        /// <summary>Перекрыть сравнение на этот вызов (CLI).</summary>
        public string Compare;
        /// <summary>Дополнительный фильтр событий на этот вызов (CLI), склеивается с where представления через &amp;&amp;.</summary>
        public string Where;
        /// <summary>Не считать представления (только сессии, прогоны, сырые данные).</summary>
        public bool SkipViews;
    }

    /// <summary>
    /// Фильтр сборки: что войдёт в отчёт и экспорт. Пустой набор — без ограничения. Сессия отбирается по дате
    /// начала (UTC), билду, платформе и профилю; прогон — ещё и по тегу.
    /// </summary>
    public sealed class ReportFilter
    {
        public DateTime? From;
        public DateTime? To;
        public readonly HashSet<string> Builds = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> Platforms = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> Tags = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> Profiles = new HashSet<string>(StringComparer.Ordinal);

        public bool IsEmpty => From == null && To == null && Builds.Count == 0 && Platforms.Count == 0 && Tags.Count == 0 && Profiles.Count == 0;

        public bool Accepts(ReportSession s)
        {
            if (From != null && (s.StartTime == null || s.StartTime < From)) return false;
            if (To != null && (s.StartTime == null || s.StartTime > To)) return false;
            if (Builds.Count > 0 && !Builds.Contains(s.Build ?? string.Empty)) return false;
            if (Platforms.Count > 0 && !Platforms.Contains(s.Platform ?? string.Empty)) return false;
            if (Profiles.Count > 0 && !Profiles.Contains(s.Profile ?? string.Empty)) return false;
            return true;
        }

        public bool Accepts(ReportRun r) => Tags.Count == 0 || Tags.Contains(r.TagOrDash);

        public string Describe()
        {
            var parts = new List<string>();
            if (From != null || To != null)
                parts.Add((From?.ToString("yyyy-MM-dd") ?? "…") + " – " + (To?.ToString("yyyy-MM-dd") ?? "…"));
            if (Builds.Count > 0) parts.Add("билды: " + string.Join(", ", Builds));
            if (Platforms.Count > 0) parts.Add("платформы: " + string.Join(", ", Platforms));
            if (Tags.Count > 0) parts.Add("теги: " + string.Join(", ", Tags));
            if (Profiles.Count > 0) parts.Add("профили: " + Profiles.Count);
            return parts.Count == 0 ? "все данные" : string.Join(" · ", parts);
        }
    }

    /// <summary>Собранный отчёт: всё, из чего строятся report.html и экспорт.</summary>
    public sealed class ReportData
    {
        public ReportCatalog Catalog;
        /// <summary>Представления, участвующие в отчёте (встроенные + проекта, с перекрытиями CLI).</summary>
        public readonly List<ReportView> Views = new List<ReportView>();
        /// <summary>Представления по всем данным.</summary>
        public readonly List<ViewResult> ViewResults = new List<ViewResult>();
        /// <summary>Таймлайны последних прогонов: дорожки представлений.</summary>
        public readonly List<RunTimeline> Timelines = new List<RunTimeline>();
        public ReportTime Time = ReportTime.Utc;
        /// <summary>Первое появление игрока во всех прочитанных данных (до фильтра) — для когорт удержания.</summary>
        public readonly Dictionary<string, DateTime> FirstSeen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        /// <summary>Данные прошлого периода той же длины (для «сравнить с прошлым периодом»); null — нет.</summary>
        public ReportData Previous;
        /// <summary>Период отчёта (UTC): начало первой и конец последней сессии.</summary>
        public DateTime? PeriodFrom;
        public DateTime? PeriodTo;
        public ReportOptions Options;
        public DateTime Generated;
        public readonly List<string> Sources = new List<string>();
        public int Files;
        public int Packets;
        public int DuplicatePackets;
        public int BrokenLines;
        public long EventCount;
        public readonly List<string> RawLines = new List<string>();
        public readonly List<ReportSession> Sessions = new List<ReportSession>();
        public readonly List<ReportRun> Runs = new List<ReportRun>();
        public readonly List<ReportProfile> Profiles = new List<ReportProfile>();
        public readonly List<ReportInterval> Intervals = new List<ReportInterval>();
        public readonly List<ReportTag> Tags = new List<ReportTag>();
        public readonly SortedSet<string> Schemas = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedSet<string> Builds = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedSet<string> Platforms = new SortedSet<string>(StringComparer.Ordinal);
        /// <summary>Все имена полей событий по порядку: сначала из каталога, потом встреченные в данных.</summary>
        public readonly List<string> FieldNames = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public IEnumerable<ReportEvent> AllEvents()
        {
            foreach (var s in Sessions)
                foreach (var e in s.Events)
                    yield return e;
        }
    }
}
