using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>Тип поля события. Определяет тип параметра в сгенерированном <c>Raise</c> и запись в JSON.</summary>
    public enum AnalyticsFieldType : byte
    {
        [InspectorName("Целое (long)")] Int = 0,
        [InspectorName("Дробное (double)")] Float = 1,
        [InspectorName("Логическое (bool)")] Bool = 2,
        [InspectorName("Строка / id (string)")] String = 3,
    }
}
