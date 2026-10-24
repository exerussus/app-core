using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>Скрытый объект: только чтобы получить OnApplicationPause (Android уходит в фон — последний шанс).</summary>
    [AddComponentMenu("")]
    internal sealed class AnalyticsHost : MonoBehaviour
    {
        private void OnApplicationPause(bool paused) => AppAnalytics.OnApplicationPause(paused);
    }
}
