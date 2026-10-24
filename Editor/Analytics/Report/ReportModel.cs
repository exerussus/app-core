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

        /// <summary>Ось таймлайна прогона: игровое время, если есть, иначе реальное.</summary>
        public double Axis => double.IsNaN(G) ? T : G;

        /// <summary>Секунды от начала сессии.</summary>
        public double SessionTime => Run != null && !double.IsNaN(Run.Start) ? Run.Start + T : T;

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
        public double Away;
        public bool Ended;
        public long Dropped;
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
        public double Away;
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
                return Session.StartTime.Value.AddSeconds(double.IsNaN(Start) ? 0 : Start);
            }
        }

        /// <summary>Конец оси таймлайна.</summary>
        public double AxisEnd
        {
            get
            {
                var end = 0.0;
                foreach (var e in Events)
                    if (e.Axis > end) end = e.Axis;
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
        public double Away;
        public int Negative;
        public DateTime? First;
        public DateTime? Last;
        public string Platform;
        public string Build;
        public readonly SortedDictionary<string, int> Results = new SortedDictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Интервал (focus, turn…) по субъекту внутри прогона или сессии.</summary>
    public sealed class ReportInterval
    {
        public ReportSession Session;
        public ReportRun Run;
        public string Name;
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

    /// <summary>Воронка: субъекты интервала по порядку первого начала, сколько прогонов дошло и закрыло.</summary>
    public sealed class ReportFunnel
    {
        public string Interval;
        public string SubjectField;
        public int Runs;
        public readonly List<ReportFunnelStep> Steps = new List<ReportFunnelStep>();
    }

    public sealed class ReportFunnelStep
    {
        public string Subject;
        public int Reached;
        public int Closed;
        public double MedianDuration = double.NaN;
    }

    /// <summary>Прогоны одного тега (сценария, уровня…) и всё, что считается по ним.</summary>
    public sealed class ReportTag
    {
        public string Tag;
        public readonly List<ReportRun> Runs = new List<ReportRun>();
        public readonly SortedDictionary<string, int> Results = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly List<ReportTable> Tables = new List<ReportTable>();
        public readonly List<ReportFunnel> Funnels = new List<ReportFunnel>();
    }

    public sealed class ReportOptions
    {
        /// <summary>Заменить профили на хэш (p_ + 12 знаков SHA-256).</summary>
        public bool Anonymize;
        public string Title;
        public ReportFilter Filter = new ReportFilter();
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
