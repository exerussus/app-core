using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>Скрытый объект: OnApplicationPause (Android уходит в фон — последний шанс сбросить пакет).</summary>
    [AddComponentMenu("")]
    internal sealed class AnalyticsHost : MonoBehaviour
    {
        private void OnApplicationPause(bool paused) => AppAnalytics.OnApplicationPause(paused);
    }
}
