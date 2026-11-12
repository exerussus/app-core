using System.Text;
using UnityEngine;

namespace Exerussus.AppCore.Deck
{
    /// <summary>Текст лога для копирования: шапка с окружением, строки с временем и типом, стеки ошибок.</summary>
    internal static class LogExport
    {
        /// <summary>Шапка: приложение, версия, платформа, Unity, число записей.</summary>
        public static void AppendHeader(StringBuilder sb, int count)
        {
            sb.Append("AppDeck log — ").Append(Application.productName)
              .Append(' ').Append(Application.version)
              .Append(" | ").Append(Application.platform)
              .Append(" | Unity ").Append(Application.unityVersion)
              .Append(" | записей: ").Append(count)
              .Append('\n').Append('\n');
        }

        /// <summary>
        /// Одна запись: «[чч:мм:сс] Тип  сообщение ×N». Стек — с отступом, если <paramref name="withStack"/>
        /// и он есть.
        /// </summary>
        public static void AppendEntry(StringBuilder sb, LogBuffer log, long seq, bool withStack)
        {
            AppendTime(sb, log.TimeOf(seq));
            sb.Append(' ').Append(TypeTitle(log.TypeOf(seq))).Append("  ").Append(log.MessageOf(seq));

            int repeat = log.RepeatOf(seq);
            if (repeat > 1) sb.Append("  ×").Append(repeat);
            sb.Append('\n');

            if (!withStack) return;

            string stack = log.StackOf(seq);
            if (string.IsNullOrEmpty(stack)) return;

            // стек с отступом — в баг-репорте видно, где кончается запись
            int start = 0;
            while (start < stack.Length)
            {
                int end = stack.IndexOf('\n', start);
                if (end < 0) end = stack.Length;
                int length = end - start;
                if (length > 0 && stack[start + length - 1] == '\r') length--;
                if (length > 0) sb.Append("    ").Append(stack, start, length).Append('\n');
                start = end + 1;
            }
        }

        /// <summary>Стек нужен для предупреждений и ошибок; у обычных логов он шумит.</summary>
        public static bool WantsStack(DeckLogType type) => type == DeckLogType.Warning || type == DeckLogType.Error;

        /// <summary>Входит ли запись в выгрузку «только ошибки».</summary>
        public static bool IsProblem(DeckLogType type) => type == DeckLogType.Warning || type == DeckLogType.Error;

        /// <summary>Весь буфер (или только предупреждения и ошибки) с шапкой. Число записей — в <paramref name="count"/>.</summary>
        public static string All(LogBuffer log, bool problemsOnly, out int count)
        {
            count = 0;
            if (log == null) return string.Empty;

            for (long seq = log.Oldest; seq < log.Next; seq++)
            {
                if (!problemsOnly || IsProblem(log.TypeOf(seq))) count++;
            }

            var sb = new StringBuilder(128 + count * 96);
            AppendHeader(sb, count);

            for (long seq = log.Oldest; seq < log.Next; seq++)
            {
                DeckLogType type = log.TypeOf(seq);
                if (problemsOnly && !IsProblem(type)) continue;
                AppendEntry(sb, log, seq, WantsStack(type));
            }

            return sb.ToString();
        }

        private static string TypeTitle(DeckLogType type) => type switch
        {
            DeckLogType.Log => "Log  ",
            DeckLogType.Warning => "Warn ",
            DeckLogType.Error => "Error",
            DeckLogType.Input => "Cmd >",
            _ => "Cmd <",
        };

        private static void AppendTime(StringBuilder sb, int secondOfDay)
        {
            if (secondOfDay < 0) secondOfDay = 0;
            int h = secondOfDay / 3600 % 24;
            int m = secondOfDay / 60 % 60;
            int s = secondOfDay % 60;

            sb.Append('[')
              .Append((char)('0' + h / 10)).Append((char)('0' + h % 10)).Append(':')
              .Append((char)('0' + m / 10)).Append((char)('0' + m % 10)).Append(':')
              .Append((char)('0' + s / 10)).Append((char)('0' + s % 10))
              .Append(']');
        }
    }
}
