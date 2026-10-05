using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Описание кнопки. Кнопка — это строка команды плюс оформление: нажатие выполняет
    /// <see cref="Line"/> тем же путём, что и ввод в консоли. Своей логики у кнопки нет.
    /// </summary>
    public sealed class ActionSpec
    {
        /// <summary>Стабильный id — по нему запоминается «закреплено». null — берётся заголовок.</summary>
        public readonly string Id;

        public readonly string Title;

        /// <summary>Строка команд («give gold 100», «god on; heal»). Пусто — кнопка открывает форму команды <see cref="Command"/>.</summary>
        public readonly string Line;

        /// <summary>Команда, чья форма аргументов открывается по нажатию (вместо готовой строки).</summary>
        public readonly string Command;

        /// <summary>Теги через запятую.</summary>
        public readonly string Tags;

        /// <summary>Подсказка при наведении.</summary>
        public readonly string Tooltip;

        /// <summary>Требовать второе нажатие (опасные действия: очистить сейв, убить всех).</summary>
        public readonly bool Confirm;

        public ActionSpec(string title, string line = null, string tags = null, string command = null,
                          string tooltip = null, bool confirm = false, string id = null)
        {
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("У кнопки нет заголовка.", nameof(title));
            if (string.IsNullOrWhiteSpace(line) && string.IsNullOrWhiteSpace(command))
                throw new ArgumentException($"Кнопка «{title}»: нужна строка команды или имя команды для формы.");

            Title = title;
            Line = line;
            Command = command;
            Tags = tags;
            Tooltip = tooltip;
            Confirm = confirm;
            Id = string.IsNullOrEmpty(id) ? title : id;
        }
    }

    /// <summary>Хэндл кнопки: слот + версия.</summary>
    public readonly struct ActionHandle : IEquatable<ActionHandle>
    {
        internal readonly int Slot;
        internal readonly int Version;

        internal ActionHandle(int slot, int version)
        {
            Slot = slot;
            Version = version;
        }

        public bool IsAssigned => Version != 0;

        public bool Equals(ActionHandle other) => Slot == other.Slot && Version == other.Version;
        public override bool Equals(object obj) => obj is ActionHandle other && Equals(other);
        public override int GetHashCode() => (Slot * 397) ^ Version;
    }

    /// <summary>
    /// Таблица тегов: имя → номер бита. Набор тегов кнопки — маска <c>ulong</c>, фильтр по тегам —
    /// одна операция AND. Тегов больше 64 не бывает на практике; лишние честно игнорируются с предупреждением.
    /// </summary>
    internal sealed class TagTable
    {
        public const int MaxTags = 64;

        private readonly Dictionary<string, int> _bits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _names = new List<string>();
        private bool _overflowReported;

        public int Count => _names.Count;

        public int Version { get; private set; }

        public string NameOf(int bit) => _names[bit];

        /// <summary>Маска тегов из строки «a, b, c». Новые теги получают биты.</summary>
        public ulong MaskOf(string tags)
        {
            if (string.IsNullOrWhiteSpace(tags)) return 0;

            ulong mask = 0;
            int i = 0;

            while (i < tags.Length)
            {
                while (i < tags.Length && (tags[i] == ',' || char.IsWhiteSpace(tags[i]))) i++;
                int start = i;
                while (i < tags.Length && tags[i] != ',') i++;
                int end = i;
                while (end > start && char.IsWhiteSpace(tags[end - 1])) end--;
                if (end <= start) continue;

                string tag = tags.Substring(start, end - start);
                if (!_bits.TryGetValue(tag, out int bit))
                {
                    if (_names.Count >= MaxTags)
                    {
                        if (!_overflowReported)
                        {
                            _overflowReported = true;
                            AppDeck.Print($"[AppDeck] Больше {MaxTags} тегов кнопок — «{tag}» и дальнейшие не учитываются в фильтре.", DeckLogType.Warning);
                        }

                        continue;
                    }

                    bit = _names.Count;
                    _bits.Add(tag, bit);
                    _names.Add(tag);
                    Version++;
                }

                mask |= 1UL << bit;
            }

            return mask;
        }
    }

    /// <summary>
    /// Реестр кнопок: слоты с версиями в параллельных массивах. Заголовок в нижнем регистре и маска
    /// тегов считаются при регистрации — поиск и фильтр во вкладке не создают строк.
    /// </summary>
    internal sealed class ActionRegistry
    {
        private ActionSpec[] _specs = new ActionSpec[32];
        private string[] _search = new string[32];
        private ulong[] _tags = new ulong[32];
        private object[] _owners = new object[32];
        private int[] _versions = new int[32];

        private int[] _free = new int[16];
        private int _freeCount;
        private int _highWater;

        public TagTable TagTable { get; } = new TagTable();

        public int Version { get; private set; }

        public int HighWater => _highWater;

        public ActionHandle Add(ActionSpec spec, object owner)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            int slot = _freeCount > 0 ? _free[--_freeCount] : _highWater++;
            EnsureCapacity(slot + 1);

            _specs[slot] = spec;
            _search[slot] = SearchKey(spec.Title, spec.Line ?? spec.Command, spec.Tags);
            _tags[slot] = TagTable.MaskOf(spec.Tags);
            _owners[slot] = owner;

            int version = _versions[slot] + 1;
            if (version <= 0) version = 1;
            _versions[slot] = version;

            Version++;
            return new ActionHandle(slot, version);
        }

        public bool Remove(ActionHandle handle)
        {
            if (!IsAlive(handle)) return false;

            Release(handle.Slot);
            return true;
        }

        public int RemoveAll(object owner)
        {
            if (owner == null) return 0;

            var removed = 0;
            for (var i = 0; i < _highWater; i++)
            {
                if (_specs[i] == null || !ReferenceEquals(_owners[i], owner)) continue;

                Release(i);
                removed++;
            }

            return removed;
        }

        public bool IsAlive(ActionHandle handle) =>
            handle.Version != 0 && handle.Slot >= 0 && handle.Slot < _highWater &&
            _versions[handle.Slot] == handle.Version && _specs[handle.Slot] != null;

        public ActionSpec Spec(int slot) => slot >= 0 && slot < _highWater ? _specs[slot] : null;

        public string SearchOf(int slot) => _search[slot];

        public ulong TagsOf(int slot) => _tags[slot];

        private void Release(int slot)
        {
            _specs[slot] = null;
            _search[slot] = null;
            _owners[slot] = null;
            _tags[slot] = 0;

            if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
            _free[_freeCount++] = slot;
            Version++;
        }

        private void EnsureCapacity(int size)
        {
            if (size <= _specs.Length) return;

            int n = Math.Max(size, _specs.Length * 2);
            Array.Resize(ref _specs, n);
            Array.Resize(ref _search, n);
            Array.Resize(ref _tags, n);
            Array.Resize(ref _owners, n);
            Array.Resize(ref _versions, n);
        }

        /// <summary>Строка для поиска: заголовок, команда и теги в нижнем регистре — один раз при регистрации.</summary>
        internal static string SearchKey(string title, string line, string tags) =>
            string.Concat(title, "\n", line ?? string.Empty, "\n", tags ?? string.Empty).ToLowerInvariant();
    }
}
