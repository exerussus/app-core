using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Deck
{
    /// <summary>Вид метрики.</summary>
    public enum MetricKind : byte
    {
        /// <summary>Число, которое читается функцией при показе (FPS, память, число жителей).</summary>
        Gauge = 0,

        /// <summary>Строка, которая читается функцией при показе (текущая страница, сцена).</summary>
        Text = 1,

        /// <summary>Число, которое пишет сам код (<see cref="AppDeck.Count"/> / <see cref="AppDeck.SetMetric"/>). Показывается значение и скорость в секунду.</summary>
        Counter = 2,
    }

    /// <summary>Хэндл метрики: слот + версия. Запись по хэндлу — индексация массива, без поиска по имени.</summary>
    public readonly struct MetricHandle : IEquatable<MetricHandle>
    {
        internal readonly int Slot;
        internal readonly int Version;

        internal MetricHandle(int slot, int version)
        {
            Slot = slot;
            Version = version;
        }

        public bool IsAssigned => Version != 0;

        public bool Equals(MetricHandle other) => Slot == other.Slot && Version == other.Version;
        public override bool Equals(object obj) => obj is MetricHandle other && Equals(other);
        public override int GetHashCode() => (Slot * 397) ^ Version;
    }

    /// <summary>
    /// Реестр метрик в параллельных массивах.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Метрики — «тянущие»: функция чтения зовётся только когда значение кому-то показывают
    /// (видимая строка вкладки, закреплённая в мини-HUD) и не чаще заданной частоты. Пока вкладка
    /// закрыта и метрика не закреплена, она не стоит ничего.
    /// </para>
    /// <para>
    /// Счётчики пишутся кодом через хэндл: проверка версии и сложение в массиве. История треков —
    /// кольцо <c>float</c> на метрику, выделяется один раз при регистрации.
    /// </para>
    /// </remarks>
    internal sealed class MetricRegistry
    {
        private string[] _names = new string[32];
        private string[] _groups = new string[32];
        private string[] _units = new string[32];
        private string[] _formats = new string[32];
        private string[] _search = new string[32];
        private MetricKind[] _kinds = new MetricKind[32];
        private Func<double>[] _readers = new Func<double>[32];
        private Func<string>[] _textReaders = new Func<string>[32];
        private double[] _values = new double[32];
        private double[] _previous = new double[32];
        private double[] _rates = new double[32];
        private string[] _texts = new string[32];
        private float[][] _history = new float[32][];
        private int[] _historyHead = new int[32];
        private int[] _historyCount = new int[32];
        private bool[] _alwaysTrack = new bool[32];
        private double[] _sampledAt = new double[32];
        private ulong[] _groupMasks = new ulong[32];
        private object[] _owners = new object[32];
        private int[] _versions = new int[32];

        private int[] _free = new int[16];
        private int _freeCount;
        private int _highWater;

        private readonly Dictionary<string, int> _byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int Version { get; private set; }

        public int HighWater => _highWater;

        public TagTable Groups { get; } = new TagTable();

        public MetricHandle Add(string name, MetricKind kind, Func<double> reader, Func<string> textReader, string group,
                                string unit, string format, int trackLength, bool alwaysTrack, object owner)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("У метрики нет имени.", nameof(name));

            if (_byName.TryGetValue(name, out int existing)) Release(existing);

            int slot = _freeCount > 0 ? _free[--_freeCount] : _highWater++;
            EnsureCapacity(slot + 1);

            _names[slot] = name;
            _groups[slot] = string.IsNullOrWhiteSpace(group) ? "Other" : group;
            _units[slot] = unit;
            _formats[slot] = string.IsNullOrEmpty(format) ? "F1" : format;
            _search[slot] = string.Concat(name, "\n", _groups[slot]).ToLowerInvariant();
            _kinds[slot] = kind;
            _readers[slot] = reader;
            _textReaders[slot] = textReader;
            _values[slot] = 0;
            _previous[slot] = 0;
            _rates[slot] = 0;
            _texts[slot] = null;
            _history[slot] = trackLength > 0 ? new float[trackLength] : null;
            _historyHead[slot] = 0;
            _historyCount[slot] = 0;
            _alwaysTrack[slot] = alwaysTrack && trackLength > 0;
            _sampledAt[slot] = -1.0;
            _owners[slot] = owner;

            // бит группы — один раз при регистрации: фильтр по группам не разбирает строки
            _groupMasks[slot] = Groups.MaskOf(_groups[slot]);
            _byName[name] = slot;

            int version = _versions[slot] + 1;
            if (version <= 0) version = 1;
            _versions[slot] = version;

            Version++;
            return new MetricHandle(slot, version);
        }

        public bool IsAlive(MetricHandle h) =>
            h.Version != 0 && h.Slot >= 0 && h.Slot < _highWater && _versions[h.Slot] == h.Version && _names[h.Slot] != null;

        public bool Remove(MetricHandle h)
        {
            if (!IsAlive(h)) return false;

            Release(h.Slot);
            return true;
        }

        public int RemoveAll(object owner)
        {
            if (owner == null) return 0;

            var removed = 0;
            for (var i = 0; i < _highWater; i++)
            {
                if (_names[i] == null || !ReferenceEquals(_owners[i], owner)) continue;

                Release(i);
                removed++;
            }

            return removed;
        }

        private void Release(int slot)
        {
            _byName.Remove(_names[slot]);
            _names[slot] = null;
            _readers[slot] = null;
            _textReaders[slot] = null;
            _texts[slot] = null;
            _history[slot] = null;
            _owners[slot] = null;

            if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
            _free[_freeCount++] = slot;
            Version++;
        }

        private void EnsureCapacity(int size)
        {
            if (size <= _names.Length) return;

            int n = Math.Max(size, _names.Length * 2);
            Array.Resize(ref _names, n);
            Array.Resize(ref _groups, n);
            Array.Resize(ref _units, n);
            Array.Resize(ref _formats, n);
            Array.Resize(ref _search, n);
            Array.Resize(ref _kinds, n);
            Array.Resize(ref _readers, n);
            Array.Resize(ref _textReaders, n);
            Array.Resize(ref _values, n);
            Array.Resize(ref _previous, n);
            Array.Resize(ref _rates, n);
            Array.Resize(ref _texts, n);
            Array.Resize(ref _history, n);
            Array.Resize(ref _historyHead, n);
            Array.Resize(ref _historyCount, n);
            Array.Resize(ref _alwaysTrack, n);
            Array.Resize(ref _sampledAt, n);
            Array.Resize(ref _groupMasks, n);
            Array.Resize(ref _owners, n);
            Array.Resize(ref _versions, n);
        }

        // ---------------------------------------------------------------- запись (счётчики)

        public void Add(MetricHandle h, double delta)
        {
            if (IsAlive(h)) _values[h.Slot] += delta;
        }

        public void Set(MetricHandle h, double value)
        {
            if (IsAlive(h)) _values[h.Slot] = value;
        }

        // ---------------------------------------------------------------- чтение

        public string NameOf(int slot) => _names[slot];
        public string GroupOf(int slot) => _groups[slot];
        public string UnitOf(int slot) => _units[slot];
        public string FormatOf(int slot) => _formats[slot];
        public string SearchOf(int slot) => _search[slot];
        public ulong GroupMaskOf(int slot) => _groupMasks[slot];
        public MetricKind KindOf(int slot) => _kinds[slot];
        public double ValueOf(int slot) => _values[slot];
        public double RateOf(int slot) => _rates[slot];
        public string TextOf(int slot) => _texts[slot];
        public bool IsTracked(int slot) => _history[slot] != null;
        public bool AlwaysTrack(int slot) => _alwaysTrack[slot];
        public int Find(string name) => name != null && _byName.TryGetValue(name, out int slot) ? slot : -1;

        public bool IsAliveSlot(int slot) => slot >= 0 && slot < _highWater && _names[slot] != null;

        /// <summary>
        /// Опросить метрику, если с прошлого опроса прошло не меньше <paramref name="interval"/>.
        /// Возвращает true, если значение обновилось. Исключение читателя — в консоль, метрика не роняет кадр.
        /// </summary>
        /// <remarks>
        /// Часы — одни на всех (realtime с запуска): метрику опрашивают вкладка, мини-HUD и фоновые треки,
        /// и у каждого свой ритм, но «когда опрошена» и скорость счётчика считаются по общему времени.
        /// </remarks>
        public bool Sample(int slot, float interval)
        {
            if (_names[slot] == null) return false;

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_sampledAt[slot] >= 0.0 && now - _sampledAt[slot] < interval) return false;

            double dt = _sampledAt[slot] >= 0.0 ? now - _sampledAt[slot] : 0.0;
            _sampledAt[slot] = now;

            try
            {
                switch (_kinds[slot])
                {
                    case MetricKind.Gauge:
                        if (_readers[slot] != null) _values[slot] = _readers[slot]();
                        break;

                    case MetricKind.Text:
                        if (_textReaders[slot] != null) _texts[slot] = _textReaders[slot]();
                        return true;

                    case MetricKind.Counter:
                        _rates[slot] = dt > 0.0 ? (_values[slot] - _previous[slot]) / dt : 0;
                        _previous[slot] = _values[slot];
                        break;
                }
            }
            catch (Exception e)
            {
                // один раз и отключаем читателя: метрика, падающая 8 раз в секунду, засыпала бы консоль
                AppDeck.Print($"Метрика «{_names[slot]}» упала и отключена: {e.Message}", DeckLogType.Error, e.ToString());
                _readers[slot] = null;
                _textReaders[slot] = null;
                return false;
            }

            float[] history = _history[slot];
            if (history != null)
            {
                double point = _kinds[slot] == MetricKind.Counter ? _rates[slot] : _values[slot];
                history[_historyHead[slot]] = (float)point;
                _historyHead[slot] = (_historyHead[slot] + 1) % history.Length;
                if (_historyCount[slot] < history.Length) _historyCount[slot]++;
            }

            return true;
        }

        /// <summary>История трека: массив-кольцо, индекс самой старой точки и число точек.</summary>
        public float[] HistoryOf(int slot, out int oldest, out int count)
        {
            float[] history = _history[slot];
            count = history != null ? _historyCount[slot] : 0;
            oldest = history != null ? (_historyHead[slot] - count + history.Length) % history.Length : 0;
            return history;
        }
    }
}
