using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Таблица строк: заголовки языков и ключи со значениями. Это и содержимое файла <c>.loc</c>,
    /// и слой, который отдают в <see cref="Loc.AddLayer"/>.
    /// </summary>
    /// <remarks>
    /// В базовой таблице все значения — тексты. В патче значение может быть удалением (<c>~</c>),
    /// а запись — удалением ключа целиком. Порядок записей — порядок файла; писатель
    /// сортирует ключи сам.
    /// </remarks>
    public sealed class LocTable
    {
        private readonly List<LocLanguage> _languages = new();
        private readonly List<LocEntry> _entries = new();
        private readonly Dictionary<string, LocEntry> _index = new(StringComparer.Ordinal);

        /// <summary>Комментарии в начале файла, до первого ключа.</summary>
        public readonly List<string> HeaderComments = new();

        /// <summary>Комментарии после последнего ключа.</summary>
        public readonly List<string> TrailingComments = new();

        public IReadOnlyList<LocLanguage> Languages => _languages;
        public IReadOnlyList<LocEntry> Entries => _entries;
        public int Count => _entries.Count;

        // ---------- языки ----------

        public LocLanguage GetLanguage(string code)
        {
            for (var i = 0; i < _languages.Count; i++)
                if (LocSlots.Same(_languages[i].Code, code)) return _languages[i];
            return null;
        }

        /// <summary>Добавляет язык или обновляет поля существующего. Пустые поля не затирают заданные.</summary>
        public LocLanguage AddLanguage(string code, string name = null, string plural = null, string fallback = null)
        {
            if (!LocSlots.IsValidLanguage(code)) throw new ArgumentException($"Недопустимый код языка «{code}».", nameof(code));

            var language = GetLanguage(code);
            if (language == null)
            {
                language = new LocLanguage(code, name, plural, fallback);
                _languages.Add(language);
                return language;
            }

            if (!string.IsNullOrEmpty(name)) language.Name = name;
            if (!string.IsNullOrEmpty(plural)) language.Plural = plural;
            if (!string.IsNullOrEmpty(fallback)) language.Fallback = fallback;
            return language;
        }

        /// <summary>Убирает язык вместе со всеми его значениями.</summary>
        public bool RemoveLanguage(string code)
        {
            var language = GetLanguage(code);
            if (language == null) return false;

            _languages.Remove(language);
            for (var i = 0; i < _entries.Count; i++)
                _entries[i].Values.RemoveAll(v => LocSlots.Same(v.Language, code));
            return true;
        }

        /// <summary>Переставляет язык. Первый язык таблицы — основной, если в настройках не задан другой.</summary>
        public void MoveLanguage(string code, int index)
        {
            var language = GetLanguage(code);
            if (language == null) return;

            _languages.Remove(language);
            _languages.Insert(Math.Clamp(index, 0, _languages.Count), language);
        }

        // ---------- ключи ----------

        public bool Contains(string key) => key != null && _index.ContainsKey(key);

        public LocEntry Get(string key) => key != null && _index.TryGetValue(key, out var entry) ? entry : null;

        public LocEntry GetOrAdd(string key)
        {
            var entry = Get(key);
            if (entry != null) return entry;

            if (!LocSlots.IsValidKey(key)) throw new ArgumentException($"Недопустимый ключ «{key}».", nameof(key));

            entry = new LocEntry { Key = key };
            _entries.Add(entry);
            _index.Add(key, entry);
            return entry;
        }

        /// <summary>Текст ключа на языке и в форме. <c>null</c> — нет.</summary>
        public string GetText(string key, string language, string form = null) => Get(key)?.Find(language, form)?.Text;

        /// <summary>Ставит текст. Пустой или <c>null</c> текст — значение снимается (а не превращается в удаление).</summary>
        public void Set(string key, string language, string text, string form = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                Clear(key, language, form);
                return;
            }

            var entry = GetOrAdd(key);
            entry.RemoveKey = false;

            var value = entry.Find(language, form);
            if (value == null)
            {
                value = new LocValue { Language = language, Form = string.IsNullOrEmpty(form) ? null : form };
                entry.Values.Add(value);
            }

            value.Text = text;
        }

        /// <summary>Снимает значение с записи (в файле его просто не будет).</summary>
        public bool Clear(string key, string language, string form = null)
        {
            var entry = Get(key);
            var value = entry?.Find(language, form);
            return value != null && entry.Values.Remove(value);
        }

        /// <summary>Патч: удалить значение на языке (<c>ru: ~</c>).</summary>
        public void MarkRemoved(string key, string language, string form = null)
        {
            var entry = GetOrAdd(key);
            entry.RemoveKey = false;

            var value = entry.Find(language, form);
            if (value == null)
            {
                value = new LocValue { Language = language, Form = string.IsNullOrEmpty(form) ? null : form };
                entry.Values.Add(value);
            }

            value.Text = null;
        }

        /// <summary>Патч: удалить ключ целиком (<c>KEY ~</c>).</summary>
        public void MarkRemoved(string key)
        {
            var entry = GetOrAdd(key);
            entry.RemoveKey = true;
            entry.Values.Clear();
        }

        /// <summary>Убирает запись из таблицы (правка файла, не патч).</summary>
        public bool Remove(string key)
        {
            var entry = Get(key);
            if (entry == null) return false;

            _entries.Remove(entry);
            _index.Remove(key);
            return true;
        }

        /// <summary>Переименовывает ключ. False — нового имени нельзя (занято или недопустимо).</summary>
        public bool Rename(string key, string newKey)
        {
            var entry = Get(key);
            if (entry == null || !LocSlots.IsValidKey(newKey) || _index.ContainsKey(newKey)) return false;

            _index.Remove(key);
            entry.Key = newKey;
            _index.Add(newKey, entry);
            return true;
        }

        /// <summary>Добавляет готовую запись. Разбор файла кладёт их сюда; дубль — false.</summary>
        internal bool AddEntry(LocEntry entry)
        {
            if (!_index.TryAdd(entry.Key, entry)) return false;
            _entries.Add(entry);
            return true;
        }

        internal void AddLanguageHeader(LocLanguage language) => _languages.Add(language);

        /// <summary>Сортирует ключи по алфавиту (порядковое сравнение).</summary>
        public void Sort() => _entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        public LocTable Clone()
        {
            var copy = new LocTable();
            for (var i = 0; i < _languages.Count; i++) copy._languages.Add(_languages[i].Clone());
            for (var i = 0; i < _entries.Count; i++) copy.AddEntry(_entries[i].Clone());
            copy.HeaderComments.AddRange(HeaderComments);
            copy.TrailingComments.AddRange(TrailingComments);
            return copy;
        }
    }
}
