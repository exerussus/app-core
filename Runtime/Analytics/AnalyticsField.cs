using System;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>Поле события: имя (идентификатор C#, он же ключ в JSON) и тип.</summary>
    [Serializable]
    public struct AnalyticsField
    {
        [Tooltip("Имя поля: латиница, цифры, «_». Становится именем параметра Raise и ключом в JSON.")]
        public string name;

        public AnalyticsFieldType type;

        [Tooltip("Что лежит в поле — для каталога и аналитика.")]
        public string note;
    }
}
