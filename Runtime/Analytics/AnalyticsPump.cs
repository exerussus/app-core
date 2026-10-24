using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Насос доставки: Update включён, только пока модулю есть что делать (см. AppAnalytics.UpdatePump) —
    /// в простое Unity его не вызывает.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class AnalyticsPump : MonoBehaviour
    {
        private void Update() => AppAnalytics.Tick();
    }
}
