using Exerussus.Payloads;

namespace Exerussus.AppCore.Signals
{
    public class PayloadBuilder
    {
        private Payload _payload;
        
        public Payload CreatePayload()
        {
            if (_payload == Payload.None || !_payload.IsValid())
            {
                _payload = Payload.Create();
            }
            
            return  _payload;
        }
        
        /// <summary>
        /// Закрывает скоуп текущей кнопки и отдаёт пейлоад вызывающему.
        /// </summary>
        /// <remarks>
        /// Поле обязательно обнуляется: билдер один на весь проход по кнопкам, и если
        /// оставить ссылку, следующая кнопка получит из <see cref="CreatePayload"/> тот же
        /// самый пейлоад и перезапишет уже проставленную цель навигации.
        /// </remarks>
        internal Payload End()
        {
            var payload = _payload;
            _payload = Payload.None;

            // Кнопку мог не тронуть ни один билдер — тогда пейлоада просто нет.
            // Проверка та же, что в CreatePayload, чтобы не звать методы на несозданном хендле.
            if (payload == Payload.None || !payload.IsValid()) return Payload.None;

            if (payload.IsEmpty())
            {
                payload.Dispose();
                return Payload.None;
            }

            return payload;
        }
    }
}
