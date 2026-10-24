using System;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Событие каталога. Ключ — путь по точкам (<c>Game.Module.Focus</c>): он пишется в данные строкой,
    /// поэтому переименование ключа — это новое событие для аналитики. Номер (<c>AppEvents.….Id</c>) —
    /// только индекс текущего билда.
    /// </summary>
    [Serializable]
    public struct AnalyticsEventEntry
    {
        [Tooltip("Путь по точкам: Game.Module.Focus. До 5 уровней.")]
        public string key;

        [Tooltip("Название для людей.")]
        public string title;

        [Tooltip("Когда отправляется — для каталога и аналитика.")]
        [TextArea(1, 3)] public string when;

        [Tooltip("Выключено — Raise выходит сразу, событие не пишется.")]
        public bool disabled;

        public AnalyticsField[] fields;
    }
}
