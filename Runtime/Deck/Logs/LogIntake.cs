using System;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Приёмник логов из любого потока. Unity зовёт <c>logMessageReceivedThreaded</c> откуда угодно,
    /// поэтому запись идёт в заранее выделенное кольцо под коротким локом (только копия ссылок),
    /// а главный поток раз в кадр переливает накопленное в <see cref="LogBuffer"/> одной пачкой.
    /// </summary>
    /// <remarks>
    /// Переполнение (лавина логов из фоновых потоков быстрее кадра) не блокирует писателя и не
    /// растит память: старое непрочитанное затирается, а число потерянных честно попадает в консоль.
    /// </remarks>
    internal sealed class LogIntake
    {
        private readonly object _lock = new object();
        private readonly string[] _message;
        private readonly string[] _stack;
        private readonly byte[] _type;
        private readonly int[] _time;
        private readonly int _capacity;

        // копия пачки для переливки вне лока: писатели из фоновых потоков не ждут, пока пачка ляжет в буфер
        private readonly string[] _drainMessage;
        private readonly string[] _drainStack;
        private readonly byte[] _drainType;
        private readonly int[] _drainTime;

        // смещение часового пояса — один раз: DateTime.Now на каждый лог пересчитывал бы пояс
        private readonly long _utcOffsetTicks;

        private int _head;
        private int _count;
        private int _dropped;

        public LogIntake(int capacity)
        {
            _capacity = Math.Max(64, capacity);
            _message = new string[_capacity];
            _stack = new string[_capacity];
            _type = new byte[_capacity];
            _time = new int[_capacity];

            _drainMessage = new string[_capacity];
            _drainStack = new string[_capacity];
            _drainType = new byte[_capacity];
            _drainTime = new int[_capacity];

            _utcOffsetTicks = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).Ticks;
        }

        /// <summary>Местное время суток в секундах: UtcNow + запомненное смещение.</summary>
        private int SecondOfDay() =>
            (int)((DateTime.UtcNow.Ticks + _utcOffsetTicks) / TimeSpan.TicksPerSecond % 86400);

        /// <summary>Есть ли что переливать. Чтение без лока — допустимая гонка: в худшем случае заберём в следующем кадре.</summary>
        public bool HasPending => _count > 0 || _dropped > 0;

        public void Push(DeckLogType type, string message, string stack)
        {
            // время суток в секундах — сразу, в момент лога, а не при переливке
            int second = SecondOfDay();

            lock (_lock)
            {
                int i;
                if (_count == _capacity)
                {
                    // полное кольцо: затираем самую старую непрочитанную
                    i = _head;
                    _head = (_head + 1) % _capacity;
                    _dropped++;
                }
                else
                {
                    i = (_head + _count) % _capacity;
                    _count++;
                }

                _message[i] = message;
                _stack[i] = stack;
                _type[i] = (byte)type;
                _time[i] = second;
            }
        }

        /// <summary>Перелить всё накопленное в кольцо консоли. Только главный поток.</summary>
        public void Drain(LogBuffer target, int frame)
        {
            if (!HasPending) return;

            int dropped;
            int count;

            // под локом — только перенос ссылок в копию
            lock (_lock)
            {
                count = _count;
                for (var n = 0; n < count; n++)
                {
                    int i = (_head + n) % _capacity;
                    _drainMessage[n] = _message[i];
                    _drainStack[n] = _stack[i];
                    _drainType[n] = _type[i];
                    _drainTime[n] = _time[i];
                    _message[i] = null;
                    _stack[i] = null;
                }

                _head = 0;
                _count = 0;
                dropped = _dropped;
                _dropped = 0;
            }

            for (var n = 0; n < count; n++)
            {
                target?.Append((DeckLogType)_drainType[n], _drainMessage[n], _drainStack[n], _drainTime[n], frame);
                _drainMessage[n] = null;
                _drainStack[n] = null;
            }

            if (dropped > 0 && target != null)
                target.Append(DeckLogType.Warning, $"[AppDeck] Потеряно логов при переполнении приёмника: {dropped}.", null,
                              SecondOfDay(), frame);
        }
    }
}
