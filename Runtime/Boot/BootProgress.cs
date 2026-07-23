namespace Exerussus.AppCore.Boot
{
    /// <summary>
    /// Снимок прогресса загрузки для UI.
    /// </summary>
    /// <remarks>
    /// Стадия отдаётся значением <see cref="BootState"/>, а не строкой: строка требовала бы
    /// ToString() на каждом переходе, то есть аллокацию на ровном месте.
    /// </remarks>
    public readonly struct BootProgress
    {
        /// <summary>Нормализованный прогресс в диапазоне 0..1.</summary>
        public readonly float Normalized;

        /// <summary>Текущая стадия загрузки.</summary>
        public readonly BootState State;

        public BootProgress(float normalized, BootState state)
        {
            Normalized = normalized;
            State = state;
        }
    }
}
