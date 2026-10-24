using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Analytics.Reports
{
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

        public bool IsNumber => Type == ReportFieldType.Int || Type == ReportFieldType.Float;
    }

    /// <summary>Событие каталога глазами отчёта: ключ, название, поля.</summary>
    public sealed class ReportEventInfo
    {
        public string Key;
        public string Title;
        public string When;
        public readonly List<ReportField> Fields = new List<ReportField>();

        public ReportField Field(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var f in Fields)
                if (f.Name == name) return f;
            return null;
        }
    }

    /// <summary>
    /// Каталог для сборки отчёта: события (из AnalyticsSettings) и файл представлений проекта. События, которых
    /// нет в каталоге (старые билды, системные <c>app.*</c>), попадают в отчёт; в представления — если подходят
    /// по маске или классифицированы в файле.
    /// </summary>
    public sealed class ReportCatalog
    {
        public string Title = "Аналитика";
        public string Schema = string.Empty;
        public readonly List<ReportEventInfo> Events = new List<ReportEventInfo>();
        public ReportConfig Config = new ReportConfig();

        private Dictionary<string, ReportEventInfo> _byKey;

        public ReportEventInfo Find(string key)
        {
            if (key == null) return null;
            if (_byKey == null || _byKey.Count != Events.Count)
            {
                _byKey = new Dictionary<string, ReportEventInfo>(StringComparer.Ordinal);
                foreach (var e in Events)
                    if (!string.IsNullOrEmpty(e.Key)) _byKey[e.Key] = e;
            }

            return _byKey.TryGetValue(key, out var info) ? info : null;
        }

        /// <summary>Системные события AppCore — их поля известны ядру, в каталоге проекта их нет.</summary>
        public static readonly ReportEventInfo[] SystemEvents = BuildSystem();

        private static ReportEventInfo[] BuildSystem()
        {
            ReportEventInfo E(string key, string title, params (string, ReportFieldType)[] fields)
            {
                var info = new ReportEventInfo { Key = key, Title = title };
                foreach (var (n, t) in fields) info.Fields.Add(new ReportField { Name = n, Type = t });
                return info;
            }

            return new[]
            {
                E("app.session.start", "Начало сессии"),
                E("app.session.end", "Конец сессии"),
                E("app.run.begin", "Начало прогона", ("tag", ReportFieldType.String)),
                E("app.run.end", "Конец прогона", ("result", ReportFieldType.String), ("duration", ReportFieldType.Float)),
                E("app.profile.new", "Новый профиль", ("previous", ReportFieldType.String)),
                E("app.property", "Свойство изменено", ("name", ReportFieldType.String), ("value", ReportFieldType.String)),
                E("app.analytics.on", "Сбор включён"),
                E("app.analytics.off", "Сбор выключен"),
                E("app.analytics.dropped", "Потеряны события", ("count", ReportFieldType.Int)),
            };
        }

        /// <summary>Событие каталога или системное AppCore; null — неизвестно.</summary>
        public ReportEventInfo FindAny(string key)
        {
            var info = Find(key);
            if (info != null) return info;
            foreach (var s in SystemEvents)
                if (s.Key == key) return s;
            return null;
        }
    }
}
