using System;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>Роль события в отчёте аналитики.</summary>
    public enum AnalyticsReportRole : byte
    {
        /// <summary>Без роли: событие есть в таблице событий и экспорте.</summary>
        [InspectorName("Нет")] None = 0,

        /// <summary>Точка: счётчик, метка на таймлайне; с мерой — медиана и p90.</summary>
        [InspectorName("Метка")] Mark = 1,

        /// <summary>Начало интервала с именем <see cref="AnalyticsReportInfo.interval"/>.</summary>
        [InspectorName("Начало интервала")] Begin = 2,

        /// <summary>Конец интервала: закрывает последнее открытое начало того же имени и субъекта.</summary>
        [InspectorName("Конец интервала")] End = 3,

        /// <summary>Состояние: значение поля <see cref="AnalyticsReportInfo.value"/> держится до следующего такого события.</summary>
        [InspectorName("Состояние")] State = 4,
    }

    /// <summary>Окраска метки: нейтральная, удача (шаг выполнен), неудача (ошибка).</summary>
    public enum AnalyticsReportTone : byte
    {
        [InspectorName("Нейтрально")] Neutral = 0,
        [InspectorName("Удача")] Positive = 1,
        [InspectorName("Неудача")] Negative = 2,
    }

    /// <summary>
    /// Как отчёт читает событие. Поля — имена полей этого же события: <see cref="subject"/> — строковое поле
    /// для группировки (module, pipeline), <see cref="measure"/> — числовое для агрегатов (duration, turnTime),
    /// <see cref="value"/> — поле значения состояния.
    /// </summary>
    [Serializable]
    public struct AnalyticsReportInfo
    {
        public AnalyticsReportRole role;

        [Tooltip("Имя интервала для начала и конца: focus, turn, blocked. Пара сводится по имени и субъекту.")]
        public string interval;

        [Tooltip("Строковое поле, по которому группировать: module, pipeline. Пусто — без группировки.")]
        public string subject;

        [Tooltip("Числовое поле для агрегатов: duration, turnTime. Пусто — только счётчик.")]
        public string measure;

        [Tooltip("Поле значения для роли «Состояние»: paused, count.")]
        public string value;

        public AnalyticsReportTone tone;

        public bool IsEmpty => role == AnalyticsReportRole.None;
    }
}
