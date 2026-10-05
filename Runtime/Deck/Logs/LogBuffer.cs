using System;

namespace Exerussus.AppCore.Deck
{
    /// <summary>Вид записи консоли. Порядок значений = бит в маске фильтра.</summary>
    public enum DeckLogType : byte
    {
        Log = 0,
        Warning = 1,

        /// <summary>Error, Exception и Assert Unity.</summary>
        Error = 2,

        /// <summary>Эхо введённой команды («&gt; god on»).</summary>
        Input = 3,

        /// <summary>Вывод команд (<see cref="CommandContext.Print"/>).</summary>
        Output = 4,
    }

    /// <summary>
    /// Кольцо записей консоли фиксированной ёмкости (степень двойки) в параллельных массивах.
    /// Строки сообщения и стека не копируются — хранятся те ссылки, что отдал Unity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Запись адресуется порядковым номером <c>seq</c>: индекс в кольце — <c>seq &amp; mask</c>,
    /// запись жива, пока <c>seq ≥ <see cref="Oldest"/></c>. Так представление (фильтр) хранит номера
    /// и само понимает, что запись вытеснена, без уведомлений.
    /// </para>
    /// <para>
    /// Свёртка повторов: если новая запись совпадает с последней (тот же вид, текст и стек),
    /// растёт её счётчик, новая строка не появляется. Сравнение сначала по ссылке и длине —
    /// типичный спам одним и тем же литералом сравнивается за O(1).
    /// </para>
    /// </remarks>
    internal sealed class LogBuffer
    {
        public const int TypeCount = 5;

        private readonly int _mask;
        private readonly string[] _message;
        private readonly string[] _stack;
        private readonly string[] _head;
        private readonly byte[] _type;
        private readonly int[] _time;
        private readonly int[] _frame;
        private readonly int[] _repeat;
        private readonly int[] _counts = new int[TypeCount];

        private long _next;
        private long _oldest;

        public LogBuffer(int capacity)
        {
            capacity = NextPowerOfTwo(Math.Max(64, Math.Min(capacity, 1 << 20)));
            Capacity = capacity;
            _mask = capacity - 1;

            _message = new string[capacity];
            _stack = new string[capacity];
            _head = new string[capacity];
            _type = new byte[capacity];
            _time = new int[capacity];
            _frame = new int[capacity];
            _repeat = new int[capacity];
        }

        public int Capacity { get; }

        /// <summary>Номер следующей записи (= сколько записей было всего).</summary>
        public long Next => _next;

        /// <summary>Номер самой старой живой записи.</summary>
        public long Oldest => _oldest;

        public int Count => (int)(_next - Oldest);

        /// <summary>Растёт на любое изменение, включая счётчик повторов. Дешёвая проверка «надо ли перерисовать».</summary>
        public int Version { get; private set; }

        /// <summary>Сколько записей вида сейчас в кольце (с учётом повторов).</summary>
        public int CountOf(DeckLogType type) => _counts[(int)type];

        public bool Collapse { get; set; } = true;

        public void Append(DeckLogType type, string message, string stack, int secondOfDay, int frame)
        {
            message ??= string.Empty;
            if (stack != null && stack.Length == 0) stack = null;

            if (Collapse && _next > _oldest)
            {
                int last = (int)((_next - 1) & _mask);
                if (_type[last] == (byte)type && Same(_message[last], message) && Same(_stack[last], stack))
                {
                    _repeat[last]++;
                    _time[last] = secondOfDay;
                    _frame[last] = frame;
                    _counts[(int)type]++;
                    Version++;
                    return;
                }
            }

            int i = (int)(_next & _mask);

            // вытесняем самую старую: её вклад в счётчики уходит вместе с ней
            if (_next - _oldest >= Capacity)
            {
                _counts[_type[i]] -= _repeat[i];
                _oldest++;
            }

            _message[i] = message;
            _stack[i] = stack;
            _head[i] = null;
            _type[i] = (byte)type;
            _time[i] = secondOfDay;
            _frame[i] = frame;
            _repeat[i] = 1;
            _counts[(int)type]++;

            _next++;
            Version++;
        }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++)
            {
                _message[i] = null;
                _stack[i] = null;
                _head[i] = null;
            }

            Array.Clear(_counts, 0, _counts.Length);

            // номера не сбрасываем: представления увидят, что все их записи старше Oldest
            _oldest = _next;
            Version++;
        }

        public bool IsAlive(long seq) => seq >= _oldest && seq < _next;

        public DeckLogType TypeOf(long seq) => (DeckLogType)_type[seq & _mask];

        public string MessageOf(long seq) => _message[seq & _mask];

        public string StackOf(long seq) => _stack[seq & _mask];

        public int TimeOf(long seq) => _time[seq & _mask];

        public int FrameOf(long seq) => _frame[seq & _mask];

        public int RepeatOf(long seq) => _repeat[seq & _mask];

        /// <summary>
        /// Первая строка сообщения, не длиннее <paramref name="maxLength"/>. Считается один раз при первом
        /// показе и кэшируется: однострочное короткое сообщение — та же ссылка, без аллокации.
        /// </summary>
        public string HeadOf(long seq, int maxLength)
        {
            int i = (int)(seq & _mask);
            string head = _head[i];
            if (head != null) return head;

            string message = _message[i];
            int cut = message.IndexOf('\n');
            if (cut < 0) cut = message.Length;
            if (cut > 0 && message[cut - 1] == '\r') cut--;
            if (cut > maxLength) cut = maxLength;

            head = cut == message.Length ? message : message.Substring(0, cut);
            _head[i] = head;
            return head;
        }

        private static int NextPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value) result <<= 1;
            return result;
        }

        private static bool Same(string a, string b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}
