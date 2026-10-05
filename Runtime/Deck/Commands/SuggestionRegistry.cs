using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Источник подсказок для аргумента: дописать в <paramref name="results"/> до <paramref name="max"/>
    /// значений, подходящих под <paramref name="prefix"/>. Список уже очищен; префикс — кусок ввода без копии.
    /// </summary>
    public delegate void SuggestionProvider(ReadOnlySpan<char> prefix, List<string> results, int max);

    /// <summary>
    /// Именованные источники подсказок. Аргумент ссылается на источник по имени
    /// (<c>item:string@item</c>), а наполняет его тот, кто знает данные (игра, DSL-мост).
    /// </summary>
    internal sealed class SuggestionRegistry
    {
        private readonly Dictionary<string, int> _byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SuggestionProvider> _providers = new List<SuggestionProvider>();
        private readonly List<object> _owners = new List<object>();
        private readonly List<string> _names = new List<string>();

        public void Register(string name, SuggestionProvider provider, object owner)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("У источника подсказок нет имени.", nameof(name));
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            if (_byName.TryGetValue(name, out int index))
            {
                _providers[index] = provider;
                _owners[index] = owner;
                return;
            }

            _byName.Add(name, _providers.Count);
            _providers.Add(provider);
            _owners.Add(owner);
            _names.Add(name);
        }

        public bool Has(string name) => name != null && _byName.ContainsKey(name);

        public bool Unregister(string name)
        {
            if (!_byName.TryGetValue(name, out int index)) return false;

            RemoveAt(index);
            return true;
        }

        public int UnregisterAll(object owner)
        {
            if (owner == null) return 0;

            var removed = 0;
            for (int i = _providers.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_owners[i], owner)) continue;

                RemoveAt(i);
                removed++;
            }

            return removed;
        }

        private void RemoveAt(int index)
        {
            // перенос последнего на место снятого — без сдвига списков
            int last = _providers.Count - 1;
            _byName.Remove(_names[index]);

            if (index != last)
            {
                _providers[index] = _providers[last];
                _owners[index] = _owners[last];
                _names[index] = _names[last];
                _byName[_names[index]] = index;
            }

            _providers.RemoveAt(last);
            _owners.RemoveAt(last);
            _names.RemoveAt(last);
        }

        public bool Collect(string name, ReadOnlySpan<char> prefix, List<string> results, int max)
        {
            results.Clear();
            if (name == null || !_byName.TryGetValue(name, out int index)) return false;

            try
            {
                _providers[index](prefix, results, max);
            }
            catch (Exception e)
            {
                AppDeck.Print($"Источник подсказок «{name}» упал: {e.Message}", DeckLogType.Error, e.ToString());
                results.Clear();
                return false;
            }

            if (results.Count > max) results.RemoveRange(max, results.Count - max);
            return true;
        }

        /// <summary>Готовый поставщик по списку: фильтр по префиксу без учёта регистра, без аллокаций.</summary>
        public static SuggestionProvider FromList(IReadOnlyList<string> values)
        {
            return (prefix, results, max) =>
            {
                for (var i = 0; i < values.Count && results.Count < max; i++)
                {
                    string v = values[i];
                    if (v != null && v.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) results.Add(v);
                }
            };
        }
    }
}
