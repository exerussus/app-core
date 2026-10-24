namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Запись одного события: <c>AppAnalytics.Begin(id)</c> → поля → <see cref="End"/>. Живёт только на стеке.
    /// Сгенерированные <c>AppEvents.….Raise(...)</c> делают ровно это; руками нужно только для ручек по строке.
    /// Неактивный писатель (сбор выключен, событие выключено) ничего не делает.
    /// </summary>
    public readonly ref struct AnalyticsEventWriter
    {
        private readonly bool _active;

        internal AnalyticsEventWriter(bool active) => _active = active;

        /// <summary>Событие пишется. false — можно не вычислять поля.</summary>
        public bool Active => _active;

        public AnalyticsEventWriter Field(string name, long value)
        {
            if (_active) AppAnalytics.WriteField(name, value);
            return this;
        }

        public AnalyticsEventWriter Field(string name, int value)
        {
            if (_active) AppAnalytics.WriteField(name, value);
            return this;
        }

        public AnalyticsEventWriter Field(string name, double value)
        {
            if (_active) AppAnalytics.WriteField(name, value);
            return this;
        }

        public AnalyticsEventWriter Field(string name, float value)
        {
            if (_active) AppAnalytics.WriteField(name, value);
            return this;
        }

        public AnalyticsEventWriter Field(string name, bool value)
        {
            if (_active) AppAnalytics.WriteField(name, value);
            return this;
        }

        public AnalyticsEventWriter Field(string name, string value)
        {
            if (_active) AppAnalytics.WriteField(name, value);
            return this;
        }

        /// <summary>Закончить событие. Без End событие закроется само при следующем Begin.</summary>
        public void End()
        {
            if (_active) AppAnalytics.EndEvent();
        }
    }
}
