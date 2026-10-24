using System;
using System.Globalization;

namespace Exerussus.AppCore.Analytics.Reports
{
    /// <summary>Часовой пояс отчёта: в нём считаются дни, недели, часы суток и когорты удержания.</summary>
    public sealed class ReportTime
    {
        public static readonly ReportTime Utc = new ReportTime("UTC", TimeSpan.Zero, null);

        public readonly string Name;
        private readonly TimeSpan? _fixed;
        private readonly TimeZoneInfo _zone;

        private ReportTime(string name, TimeSpan? fixedOffset, TimeZoneInfo zone)
        {
            Name = name;
            _fixed = fixedOffset;
            _zone = zone;
        }

        /// <summary>
        /// «UTC», смещение «+03:00» / «-05:30» или id пояса системы («Europe/Moscow», «Russian Standard Time»).
        /// Не распознан — UTC и предупреждение.
        /// </summary>
        public static ReportTime Parse(string s, out string warning)
        {
            warning = null;
            if (string.IsNullOrWhiteSpace(s)) return Utc;
            s = s.Trim();
            if (s.Equals("UTC", StringComparison.OrdinalIgnoreCase) || s.Equals("Z", StringComparison.OrdinalIgnoreCase)) return Utc;

            if (s[0] == '+' || s[0] == '-')
            {
                var body = s.Substring(1);
                if (TimeSpan.TryParseExact(body, new[] { @"hh\:mm", @"h\:mm", @"hh", "%h", @"hhmm" }, CultureInfo.InvariantCulture, out var span) && span.TotalHours <= 14)
                    return new ReportTime("UTC" + s, s[0] == '-' ? -span : span, null);
            }

            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(s);
                return new ReportTime(s, null, zone);
            }
            catch (Exception)
            {
                warning = $"Часовой пояс «{s}» не найден в системе — дни считаются в UTC (можно указать смещение, например +03:00).";
                return Utc;
            }
        }

        public DateTime ToLocal(DateTime utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            if (_zone != null)
            {
                try
                {
                    return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, _zone), DateTimeKind.Unspecified);
                }
                catch (Exception)
                {
                    return utc;
                }
            }

            return DateTime.SpecifyKind(utc + (_fixed ?? TimeSpan.Zero), DateTimeKind.Unspecified);
        }

        public DateTime LocalDate(DateTime utc) => ToLocal(utc).Date;

        public string Day(DateTime utc) => DayKey(LocalDate(utc));

        public string Week(DateTime utc) => DayKey(WeekStart(LocalDate(utc)));

        public string Hour(DateTime utc) => ToLocal(utc).Hour.ToString("00", CultureInfo.InvariantCulture);

        public string HourBucket(DateTime utc)
        {
            var l = ToLocal(utc);
            return l.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture) + ":00";
        }

        private static readonly string[] WeekdayNames = { "1 Пн", "2 Вт", "3 Ср", "4 Чт", "5 Пт", "6 Сб", "7 Вс" };

        public string Weekday(DateTime utc)
        {
            var d = (int)ToLocal(utc).DayOfWeek; // 0 = воскресенье
            return WeekdayNames[(d + 6) % 7];
        }

        public static DateTime WeekStart(DateTime date)
        {
            var d = ((int)date.DayOfWeek + 6) % 7;
            return date.AddDays(-d).Date;
        }

        public static string DayKey(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public static bool TryDay(string key, out DateTime date) =>
            DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
