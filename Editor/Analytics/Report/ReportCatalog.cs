using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Analytics.Reports
{
    /// <summary>Роль события в отчёте (копия AnalyticsReportRole: сборщик не зависит от Unity).</summary>
    public enum ReportRole : byte
    {
        None = 0,
        Mark = 1,
        Begin = 2,
        End = 3,
        State = 4,
    }

    public enum ReportTone : byte
    {
        Neutral = 0,
        Positive = 1,
        Negative = 2,
    }

    public enum ReportFieldType : byte
    {
        Int = 0,
        Float = 1,
        Bool = 2,
        String = 3,
    }

    public sealed class ReportField
    {
        public string Name;
        public ReportFieldType Type;
        public string Note;
    }

    /// <summary>Событие каталога глазами отчёта: ключ, поля и роль.</summary>
    public sealed class ReportEventInfo
    {
        public string Key;
        public string Title;
        public ReportRole Role;
        public string Interval;
        public string Subject;
        public string Measure;
        public string Value;
        public ReportTone Tone;
        public readonly List<ReportField> Fields = new List<ReportField>();

        public bool HasRole => Role != ReportRole.None;
    }

    /// <summary>
    /// Каталог для сборки отчёта. Редактор заполняет его из AnalyticsSettings; события, которых нет в каталоге
    /// (старые билды, системные <c>app.*</c>), попадают в отчёт без роли.
    /// </summary>
    public sealed class ReportCatalog
    {
        public string Title = "Аналитика";
        public string Schema = string.Empty;
        public readonly List<ReportEventInfo> Events = new List<ReportEventInfo>();

        private Dictionary<string, ReportEventInfo> _byKey;

        public ReportEventInfo Find(string key)
        {
            if (key == null) return null;
            if (_byKey == null)
            {
                _byKey = new Dictionary<string, ReportEventInfo>(StringComparer.Ordinal);
                foreach (var e in Events)
                    if (!string.IsNullOrEmpty(e.Key)) _byKey[e.Key] = e;
            }

            return _byKey.TryGetValue(key, out var info) ? info : null;
        }

        /// <summary>Имена полей-субъектов, которые встречаются в ролях каталога (module, pipeline…), по порядку появления.</summary>
        public List<string> SubjectFields()
        {
            var result = new List<string>();
            foreach (var e in Events)
                if (e.HasRole && !string.IsNullOrEmpty(e.Subject) && !result.Contains(e.Subject)) result.Add(e.Subject);
            return result;
        }

        /// <summary>Имена интервалов (focus, turn…), по порядку появления начала.</summary>
        public List<string> Intervals()
        {
            var result = new List<string>();
            foreach (var e in Events)
                if (e.Role == ReportRole.Begin && !string.IsNullOrEmpty(e.Interval) && !result.Contains(e.Interval)) result.Add(e.Interval);
            return result;
        }
    }
}
