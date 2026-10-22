using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Локализация AppCore: таблица строк из слоёв, текущий язык, окончания, история патчей.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Строка, начинающаяся с <c>^</c>, — ключ: <c>Loc.Get("^MENU_PLAY")</c> → «Играть». Любая
    /// другая строка возвращается как есть, поэтому звать можно на любом тексте.
    /// </para>
    /// <para>
    /// Таблица собирается из слоёв по порядку: встроенный <c>appcore</c>, базовая таблица
    /// проекта <c>base</c>, дальше слои, добавленные через <see cref="AddLayer"/>, в порядке
    /// добавления. Каждый слой дополняет и заменяет значения предыдущих; приоритетов нет —
    /// порядок решает тот, кто слои добавляет (загрузчик модов проекта). Id слоя задаёт
    /// вызывающий, он обязан быть уникальным: по нему видно в истории, кто что изменил.
    /// </para>
    /// <para>
    /// Пока локализация выключена в <see cref="LocSettings"/>, ничего не грузится и не
    /// патчится: <see cref="Get(string)"/> возвращает строку как есть, <see cref="AddLayer"/> — false.
    /// </para>
    /// </remarks>
    public static partial class Loc
    {
        public const char Prefix = '^';

        /// <summary>Id встроенного слоя AppCore.</summary>
        public const string AppCoreLayerId = "appcore";

        /// <summary>Id базовой таблицы проекта.</summary>
        public const string BaseLayerId = "base";

        private sealed class Layer
        {
            public string Id;
            public LocSource Source;
            public object Owner;
            public LocTable Table;
            public string Path;
            public int Rejected;
        }

        private static readonly List<Layer> External = new();
        private static Layer _appCoreLayer;
        private static Layer _baseLayer;

        // ключ → слот (ru, ru.few) → текст
        private static readonly Dictionary<string, Dictionary<string, string>> Values = new(StringComparer.Ordinal);

        // ключ → слот → слои, писавшие значение, по порядку (последний — победитель)
        private static readonly Dictionary<string, Dictionary<string, List<string>>> Writers = new(StringComparer.Ordinal);

        // ключ → шаги истории (только при включённой трассировке)
        private static readonly Dictionary<string, List<LocStep>> Trace = new(StringComparer.Ordinal);

        private static readonly List<LocLanguage> LanguageList = new();
        private static readonly HashSet<string> ReportedMissing = new(StringComparer.Ordinal);
        private static readonly HashSet<string> RequestedMissing = new(StringComparer.Ordinal);
        private static readonly List<string> RuntimeProtected = new();

        private static bool _initialized;

        // Включена ли локализация — кэш на горячий путь Get: настройки читаются только при
        // инициализации и при их изменении, а не на каждую строку.
        private static bool _active;

        private static bool _traceActive;
        private static string _language;
        private static string _primary;
        private static int _batchDepth;
        private static bool _batchDirty;

        /// <summary>
        /// Таблица пересобрана или сменился язык — тексты пора обновить. <see cref="LocBinder"/>
        /// подписан сам; подписываться вручную нужно только на строки, собранные в коде.
        /// </summary>
        public static event Action Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            External.Clear();
            _appCoreLayer = null;
            _baseLayer = null;
            Values.Clear();
            Writers.Clear();
            Trace.Clear();
            LanguageList.Clear();
            ReportedMissing.Clear();
            RequestedMissing.Clear();
            RuntimeProtected.Clear();
            _initialized = false;
            _active = false;
            _enabledOverride = null;
            ClearResolved();
            ResetPipeline();
            _traceActive = false;
            _language = null;
            _primary = null;
            _batchDepth = 0;
            _batchDirty = false;
            Changed = null;
        }

        // ================================================================ состояние

        private static LocSettings Settings => LocSettings.Current;

        /// <summary>
        /// Включена ли локализация: галочка в настройках, с поправкой на <see cref="LocSettings.DisableInBatchMode"/>
        /// и <see cref="OverrideEnabled"/>.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                if (_enabledOverride.HasValue) return _enabledOverride.Value;

                var settings = Settings;
                if (settings == null || !settings.Enabled) return false;
                return !(settings.DisableInBatchMode && Application.isBatchMode);
            }
        }

        private static bool? _enabledOverride;

        /// <summary>
        /// Включить или выключить локализацию на этот запуск — до первой инициализации (выделенный сервер,
        /// тесты). Выключено — таблицы не разбираются, слои и проходы не принимаются, <c>Get("^X")</c> → «^X».
        /// </summary>
        public static void OverrideEnabled(bool enabled)
        {
            if (_initialized)
            {
                Debug.LogWarning("[Loc] OverrideEnabled после инициализации не действует — зовите его до первого обращения к Loc.");
                return;
            }

            _enabledOverride = enabled;
        }

        /// <summary>Пишется ли история ключей в этом запуске.</summary>
        public static bool TraceActive
        {
            get
            {
                EnsureInitialized();
                return _traceActive;
            }
        }

        /// <summary>Строка — ключ (начинается с <c>^</c>).</summary>
        public static bool IsKey(string text) => !string.IsNullOrEmpty(text) && text[0] == Prefix;

        /// <summary>Ключ без <c>^</c>. Строка без префикса возвращается как есть.</summary>
        public static string KeyOf(string text) => IsKey(text) ? text.Substring(1) : text;

        // ================================================================ языки

        /// <summary>Код текущего языка. Пусто — локализация выключена или таблица пуста.</summary>
        public static string Language
        {
            get
            {
                if (!EnsureInitialized()) return string.Empty;
                return _language ?? string.Empty;
            }
        }

        /// <summary>Основной язык: из него берётся непереведённое.</summary>
        public static string PrimaryLanguage
        {
            get
            {
                if (!EnsureInitialized()) return string.Empty;
                return _primary ?? string.Empty;
            }
        }

        /// <summary>Языки в порядке объявления (база, затем языки, добавленные слоями).</summary>
        public static IReadOnlyList<LocLanguage> Languages
        {
            get
            {
                EnsureInitialized();
                return LanguageList;
            }
        }

        public static LocLanguage GetLanguage(string code)
        {
            EnsureInitialized();
            return FindLanguage(code);
        }

        /// <summary>Переключает язык (и запоминает, если так настроено). False — такого языка нет.</summary>
        public static bool SetLanguage(string code)
        {
            if (!EnsureInitialized()) return false;

            var language = FindLanguage(code);
            if (language == null)
            {
                Debug.LogWarning($"[Loc] Языка «{code}» нет в таблице.");
                return false;
            }

            var settings = Settings;
            if (settings.RememberChoice) PlayerPrefs.SetString(settings.PrefsKey, language.Code);

            if (language.Code == _language) return true;

            _language = language.Code;
            ReportedMissing.Clear();
            ClearResolved();
            RaiseChanged();
            return true;
        }

        // ================================================================ чтение

        /// <summary>Перевод ключа на заданном языке (без кэша и без проходов — для инструментов).</summary>
        public static string Get(string text, string language)
        {
            if (!IsKey(text) || !EnsureInitialized()) return text;
            return ResolveOrMissing(text.Substring(1), language, null);
        }

        /// <summary>
        /// Культура текущего языка: числа и даты в <c>{0:N1}</c> форматируются по языку игры, а не
        /// по настройкам Windows. Неизвестный код — инвариантная культура.
        /// </summary>
        public static CultureInfo Culture
        {
            get
            {
                if (_culture != null) return _culture;
                var code = EnsureInitialized() ? _language : null;
                try { _culture = string.IsNullOrEmpty(code) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(code); }
                catch (CultureNotFoundException) { _culture = CultureInfo.InvariantCulture; }
                return _culture;
            }
        }

        /// <summary>Есть ли ключ (с <c>^</c> или без) хоть на каком-то языке.</summary>
        public static bool Has(string key)
        {
            if (string.IsNullOrEmpty(key) || !EnsureInitialized()) return false;
            return Values.ContainsKey(KeyOf(key));
        }

        /// <summary>Значение ключа с учётом фолбэков. False — нет нигде (заглушку не подставляет).</summary>
        public static bool TryGet(string key, out string value, string language = null, string form = null)
        {
            value = null;
            if (string.IsNullOrEmpty(key) || !EnsureInitialized()) return false;

            key = KeyOf(key);
            language ??= _language;
            value = Resolve(key, language, form, out _) ?? TryResolvers(key, language, out _);
            return value != null;
        }

        /// <summary>Значение ровно в этом слоте, без фолбэков. Для инструментов.</summary>
        public static string GetExact(string key, string language, string form = null)
        {
            if (!EnsureInitialized()) return null;
            return Values.TryGetValue(KeyOf(key), out var slots) && slots.TryGetValue(LocSlots.Make(language, form), out var text) ? text : null;
        }

        /// <summary>Все ключи итоговой таблицы.</summary>
        public static IEnumerable<string> Keys
        {
            get
            {
                EnsureInitialized();
                return Values.Keys;
            }
        }

        /// <summary>Ключи без значения на языке (без учёта фолбэков).</summary>
        public static List<string> Untranslated(string language)
        {
            var list = new List<string>();
            if (!EnsureInitialized()) return list;

            foreach (var pair in Values)
                if (!pair.Value.ContainsKey(language)) list.Add(pair.Key);

            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>Ключи, которые просили, а их нет ни на одном языке.</summary>
        public static IReadOnlyCollection<string> RequestedButMissing => RequestedMissing;

        // ================================================================ слои

        /// <summary>
        /// Добавляет слой поверх уже добавленных. Слой дополняет и заменяет значения; удалить
        /// ключ (<c>~</c>) может только не-мод, либо мод при <see cref="LocSettings.ModRemovals"/>.
        /// </summary>
        /// <param name="id">Уникальный id слоя (задаёт вызывающий). <c>appcore</c> и <c>base</c> заняты.</param>
        /// <param name="table">Таблица слоя. Хранится как есть — не меняйте её после добавления.</param>
        /// <param name="source">Кто добавляет: от этого зависят ограничения.</param>
        /// <param name="owner">Владелец — чтобы снять все его слои одним <see cref="RemoveLayers"/>.</param>
        /// <returns>False — локализация выключена, id занят или пуст, таблицы нет.</returns>
        public static bool AddLayer(string id, LocTable table, LocSource source = LocSource.Mod, object owner = null)
            => AddLayerInternal(id, table, source, owner, null);

        /// <summary>Добавляет слой из текста <c>.loc</c>. Текст с ошибками не принимается целиком.</summary>
        public static bool AddLayerFromText(string id, string text, LocSource source = LocSource.Mod, object owner = null)
        {
            if (!Enabled) return false;

            var parsed = LocFormat.Parse(text);
            if (!parsed.Ok)
            {
                Debug.LogError($"[Loc] Слой «{id}» не принят — ошибки в тексте:\n{parsed.ErrorText()}");
                return false;
            }

            return AddLayerInternal(id, parsed.Table, source, owner, null);
        }

        /// <summary>
        /// Добавляет слой из файла <c>.loc</c>. Путь запоминается: <see cref="Reload"/> перечитает файл.
        /// </summary>
        public static bool AddLayerFromFile(string id, string path, LocSource source = LocSource.Mod, object owner = null)
        {
            if (!Enabled) return false;

            var table = ReadFile(id, path);
            return table != null && AddLayerInternal(id, table, source, owner, path);
        }

        /// <summary>Снимает слой. Встроенные слои не снимаются.</summary>
        public static bool RemoveLayer(string id)
        {
            if (!_initialized || string.IsNullOrEmpty(id)) return false;

            var index = External.FindIndex(l => l.Id == id);
            if (index < 0) return false;

            External.RemoveAt(index);
            Invalidate();
            return true;
        }

        /// <summary>Снимает все слои владельца. Возвращает, сколько снято.</summary>
        public static int RemoveLayers(object owner)
        {
            if (!_initialized || owner == null) return 0;

            var removed = External.RemoveAll(l => ReferenceEquals(l.Owner, owner));
            if (removed > 0) Invalidate();
            return removed;
        }

        /// <summary>
        /// Пачка изменений: слои внутри <c>using (Loc.Batch())</c> применяются одной пересборкой
        /// и одним событием <see cref="Changed"/> на выходе.
        /// </summary>
        public static IDisposable Batch()
        {
            _batchDepth++;
            return new BatchScope();
        }

        private sealed class BatchScope : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;

                if (--_batchDepth > 0 || !_batchDirty) return;

                _batchDirty = false;
                Rebuild();
                RaiseChanged();
            }
        }

        /// <summary>Слои по порядку применения, включая встроенные.</summary>
        public static IReadOnlyList<LocLayerInfo> Layers
        {
            get
            {
                var list = new List<LocLayerInfo>();
                if (!EnsureInitialized()) return list;

                foreach (var layer in AllLayers())
                    list.Add(new LocLayerInfo(layer.Id, layer.Source, layer.Table.Count, layer.Path, layer.Rejected));
                return list;
            }
        }

        /// <summary>Таблица слоя как её добавили. Только для чтения.</summary>
        public static LocTable GetLayerTable(string id)
        {
            if (!EnsureInitialized()) return null;

            foreach (var layer in AllLayers())
                if (layer.Id == id) return layer.Table;
            return null;
        }

        /// <summary>Значения, которые писали несколько слоёв. Пусто, если трассировка выключена.</summary>
        public static List<LocOverride> Overrides()
        {
            var list = new List<LocOverride>();
            if (!EnsureInitialized()) return list;

            foreach (var key in Writers)
            foreach (var slot in key.Value)
                if (slot.Value.Count > 1)
                    list.Add(new LocOverride(key.Key, slot.Key, slot.Value.ToArray()));

            list.Sort((a, b) =>
            {
                var byKey = string.CompareOrdinal(a.Key, b.Key);
                return byKey != 0 ? byKey : string.CompareOrdinal(a.Slot, b.Slot);
            });
            return list;
        }

        /// <summary>Какой слой поставил показанное значение. <c>null</c> — значения в этом слоте нет или трассировка выключена.</summary>
        public static string WhereFrom(string key, string language = null, string form = null)
        {
            if (!EnsureInitialized()) return null;

            key = KeyOf(key);
            if (Writers.TryGetValue(key, out var slots) &&
                slots.TryGetValue(LocSlots.Make(language ?? _language, form), out var layers) && layers.Count > 0)
                return layers[layers.Count - 1];

            return ResolvedBy.TryGetValue(key, out var resolver) ? ResolverPrefix + resolver : null;
        }

        /// <summary>
        /// История ключа: какой слой что поставил, заменил, удалил, что отклонено и почему,
        /// и откуда в итоге берётся показанное. Пишется только при включённой трассировке
        /// (<see cref="LocSettings.Trace"/>) — иначе в истории только итог.
        /// </summary>
        /// <param name="language">Язык; <c>null</c> — все языки, итог — по текущему.</param>
        public static List<LocStep> History(string key, string language = null)
        {
            var list = new List<LocStep>();
            if (!EnsureInitialized() || string.IsNullOrEmpty(key)) return list;

            key = KeyOf(key);

            if (Trace.TryGetValue(key, out var steps))
            {
                foreach (var step in steps)
                    if (language == null || step.Language == null || LocSlots.Same(step.Language, language))
                        list.Add(step);
            }

            var target = language ?? _language;
            if (string.IsNullOrEmpty(target)) return list;

            var value = Resolve(key, target, null, out var usedLanguage);
            if (value == null && ResolvedBy.TryGetValue(key, out var resolver))
                list.Add(new LocStep { LayerId = resolver, Kind = LocStepKind.Resolved, Language = target, Value = TryResolvers(key, target, out _) });
            else if (value == null)
                list.Add(new LocStep { Kind = LocStepKind.Missing, Language = target });
            else if (!LocSlots.Same(usedLanguage, target))
                list.Add(new LocStep { Kind = LocStepKind.Fallback, Language = target, Value = value, Reason = usedLanguage });

            return list;
        }

        /// <summary>
        /// Защищает ключи от модов. Шаблон с <c>*</c> в конце — префикс. Действует со следующей
        /// пересборки (уже применённые слои пересобираются сразу).
        /// </summary>
        public static void Protect(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || RuntimeProtected.Contains(pattern)) return;

            RuntimeProtected.Add(pattern);
            if (_initialized) Invalidate();
        }

        /// <summary>Перечитывает базовую таблицу и слои из файлов, пересобирает и обновляет тексты.</summary>
        public static void Reload()
        {
            if (!_initialized)
            {
                if (EnsureInitialized()) RaiseChanged();
                return;
            }

            if (!Enabled) return;

            _baseLayer = LoadBase();

            foreach (var layer in External)
            {
                if (string.IsNullOrEmpty(layer.Path)) continue;

                var table = ReadFile(layer.Id, layer.Path);
                if (table != null) layer.Table = table;
            }

            Rebuild();
            RaiseChanged();
        }

        /// <summary>Настройки изменились (инспектор в Play Mode).</summary>
        internal static void SettingsChanged()
        {
            if (!_initialized) return;

            _active = Enabled;
            if (!_active)
            {
                ClearResolved();
                RaiseChanged();
                return;
            }

            Rebuild();
            RaiseChanged();
        }

        // ================================================================ внутреннее: сборка

        /// <summary>Поднимает таблицу при первом обращении. False — локализация выключена.</summary>
        private static bool EnsureInitialized()
        {
            if (_initialized) return _active;
            if (!Enabled) return false;

            _initialized = true;
            _active = true;
            _appCoreLayer = new Layer { Id = AppCoreLayerId, Source = LocSource.AppCore, Table = LocBuiltin.CreateTable() };
            _baseLayer = LoadBase();

            Rebuild();
            return true;
        }

        private static Layer LoadBase()
        {
            var settings = Settings;
            var table = new LocTable();
            var asset = settings != null ? settings.BaseTable : null;

            if (asset == null)
            {
                Debug.LogWarning("[Loc] Базовая таблица не задана в LocalizationSettings — работают только встроенные строки AppCore.");
            }
            else
            {
                var parsed = LocFormat.Parse(asset.text);
                table = parsed.Table;

                // Своя таблица с ошибками всё равно грузится: лучше половина текстов, чем ни одного.
                if (!parsed.Ok) Debug.LogError($"[Loc] Ошибки в базовой таблице:\n{parsed.ErrorText(asset.name)}");
            }

            return new Layer { Id = BaseLayerId, Source = LocSource.Base, Table = table };
        }

        private static LocTable ReadFile(string id, string path)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Loc] Слой «{id}»: не читается файл {path} — {e.Message}");
                return null;
            }

            var parsed = LocFormat.Parse(text);
            if (parsed.Ok) return parsed.Table;

            Debug.LogError($"[Loc] Слой «{id}» не принят — ошибки в файле:\n{parsed.ErrorText(Path.GetFileName(path))}");
            return null;
        }

        private static bool AddLayerInternal(string id, LocTable table, LocSource source, object owner, string path)
        {
            if (!EnsureInitialized()) return false;

            if (table == null)
            {
                Debug.LogError($"[Loc] Слой «{id}»: таблицы нет.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                Debug.LogError("[Loc] У слоя должен быть id — по нему видно в истории, кто что изменил.");
                return false;
            }

            if (id == AppCoreLayerId || id == BaseLayerId)
            {
                Debug.LogError($"[Loc] Id «{id}» занят встроенным слоем.");
                return false;
            }

            if (External.Exists(l => l.Id == id))
            {
                Debug.LogError($"[Loc] Слой «{id}» уже добавлен: id слоёв должны быть уникальными.");
                return false;
            }

            if (source == LocSource.AppCore || source == LocSource.Base)
            {
                Debug.LogError($"[Loc] Слой «{id}»: источник {source} зарезервирован, используйте Project или Mod.");
                return false;
            }

            External.Add(new Layer { Id = id, Source = source, Owner = owner, Table = table, Path = path });
            Invalidate();
            return true;
        }

        private static void Invalidate()
        {
            if (_batchDepth > 0)
            {
                _batchDirty = true;
                return;
            }

            Rebuild();
            RaiseChanged();
        }

        private static IEnumerable<Layer> AllLayers()
        {
            if (_appCoreLayer != null) yield return _appCoreLayer;
            if (_baseLayer != null) yield return _baseLayer;
            foreach (var layer in External) yield return layer;
        }

        private static void Rebuild()
        {
            var settings = Settings;
            _traceActive = settings != null && settings.TraceActive;

            Values.Clear();
            Writers.Clear();
            Trace.Clear();
            LanguageList.Clear();
            ReportedMissing.Clear();
            RequestedMissing.Clear();
            ClearResolved();

            foreach (var layer in AllLayers())
            {
                layer.Rejected = 0;
                ApplyLanguages(layer);
                ApplyEntries(layer, settings);
            }

            // Основной язык: из настроек, иначе первый объявленный.
            var primary = settings != null ? FindLanguage(settings.PrimaryLanguage) : null;
            if (primary == null && LanguageList.Count > 0) primary = LanguageList[0];
            _primary = primary?.Code;

            if (FindLanguage(_language) == null) _language = PickStartLanguage(settings);
        }

        private static void ApplyLanguages(Layer layer)
        {
            foreach (var header in layer.Table.Languages)
            {
                var existing = FindLanguage(header.Code);
                if (existing == null)
                {
                    LanguageList.Add(header.Clone());
                    continue;
                }

                // Поздний слой уточняет заголовок: имя, правило окончаний, фолбэк.
                if (!string.IsNullOrEmpty(header.Name)) existing.Name = header.Name;
                if (!string.IsNullOrEmpty(header.Plural)) existing.Plural = header.Plural;
                if (!string.IsNullOrEmpty(header.Fallback)) existing.Fallback = header.Fallback;
            }
        }

        private static void ApplyEntries(Layer layer, LocSettings settings)
        {
            var isMod = layer.Source == LocSource.Mod;
            var modRemovals = settings != null && settings.ModRemovals;

            foreach (var entry in layer.Table.Entries)
            {
                var key = entry.Key;

                if (isMod && IsProtected(key, settings))
                {
                    Reject(layer, key, null, null, "ключ защищён от модов");
                    continue;
                }

                if (entry.RemoveKey)
                {
                    if (isMod && !modRemovals)
                    {
                        Reject(layer, key, null, null, "модам нельзя удалять ключи (LocSettings → Моды → Mod Removals)");
                        continue;
                    }

                    RemoveKey(layer, key);
                    continue;
                }

                foreach (var value in entry.Values)
                {
                    if (value.IsRemove)
                    {
                        if (isMod && !modRemovals)
                        {
                            Reject(layer, key, value.Language, value.Form, "модам нельзя удалять значения (LocSettings → Моды → Mod Removals)");
                            continue;
                        }

                        RemoveValue(layer, key, value.Language, value.Form);
                        continue;
                    }

                    var text = value.Text;
                    if (isMod && !PassRichText(ref text, settings, out var reason))
                    {
                        Reject(layer, key, value.Language, value.Form, reason);
                        continue;
                    }

                    SetValue(layer, key, value.Language, value.Form, text);
                }
            }
        }

        private static void SetValue(Layer layer, string key, string language, string form, string text)
        {
            if (!Values.TryGetValue(key, out var slots))
            {
                slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Values.Add(key, slots);
            }

            var slot = LocSlots.Make(language, form);
            slots.TryGetValue(slot, out var previous);
            slots[slot] = text;

            // Кто писал значение — для WhereFrom и Overrides. Только при трассировке: в релизе это
            // удвоило бы память таблицы ради отладочного инструмента.
            if (!_traceActive) return;

            if (!Writers.TryGetValue(key, out var writerSlots))
            {
                writerSlots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                Writers.Add(key, writerSlots);
            }

            if (!writerSlots.TryGetValue(slot, out var layers))
            {
                layers = new List<string>(1);
                writerSlots.Add(slot, layers);
            }

            layers.Add(layer.Id);

            AddStep(key, new LocStep
                {
                    LayerId = layer.Id, Source = layer.Source, Language = language, Form = form, Value = text, Previous = previous,
                    Kind = previous == null ? LocStepKind.Set : LocStepKind.Replace,
                });
        }

        private static void RemoveValue(Layer layer, string key, string language, string form)
        {
            if (!Values.TryGetValue(key, out var slots)) return;

            var slot = LocSlots.Make(language, form);
            if (!slots.TryGetValue(slot, out var previous)) return;

            slots.Remove(slot);
            if (slots.Count == 0) Values.Remove(key);

            if (Writers.TryGetValue(key, out var writerSlots)) writerSlots.Remove(slot);

            if (_traceActive)
                AddStep(key, new LocStep { LayerId = layer.Id, Source = layer.Source, Kind = LocStepKind.Remove, Language = language, Form = form, Previous = previous });
        }

        private static void RemoveKey(Layer layer, string key)
        {
            if (!Values.TryGetValue(key, out var slots)) return;

            if (_traceActive)
            {
                foreach (var pair in slots)
                {
                    SplitSlot(pair.Key, out var language, out var form);
                    AddStep(key, new LocStep { LayerId = layer.Id, Source = layer.Source, Kind = LocStepKind.Remove, Language = language, Form = form, Previous = pair.Value });
                }
            }

            Values.Remove(key);
            Writers.Remove(key);
        }

        private static void Reject(Layer layer, string key, string language, string form, string reason)
        {
            layer.Rejected++;
            if (!_traceActive) return;

            AddStep(key, new LocStep { LayerId = layer.Id, Source = layer.Source, Kind = LocStepKind.Rejected, Language = language, Form = form, Reason = reason });
        }

        private static void AddStep(string key, LocStep step)
        {
            if (!Trace.TryGetValue(key, out var steps))
            {
                steps = new List<LocStep>(2);
                Trace.Add(key, steps);
            }

            steps.Add(step);
        }

        private static void SplitSlot(string slot, out string language, out string form)
        {
            var dot = slot.IndexOf('.');
            language = dot > 0 ? slot.Substring(0, dot) : slot;
            form = dot > 0 ? slot.Substring(dot + 1) : null;
        }

        private static bool IsProtected(string key, LocSettings settings)
        {
            if (settings != null)
                foreach (var pattern in settings.ProtectedKeys)
                    if (Matches(key, pattern)) return true;

            foreach (var pattern in RuntimeProtected)
                if (Matches(key, pattern)) return true;

            return false;
        }

        private static bool Matches(string key, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern[pattern.Length - 1] == '*') return key.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal);
            return string.Equals(key, pattern, StringComparison.Ordinal);
        }

        // Теги rich text. Токены вида <x:id> — не разметка (их разбирает процессор проекта), пропускаются.
        private static readonly Regex TagPattern = new(@"<(/?)([A-Za-z][A-Za-z0-9\-]*)([^<>]*)>", RegexOptions.Compiled);

        private static readonly HashSet<string> SafeTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "b", "i", "u", "s", "color", "size", "sup", "sub", "mark", "br", "alpha", "noparse",
            "lowercase", "uppercase", "smallcaps", "nobr",
        };

        private static bool PassRichText(ref string text, LocSettings settings, out string reason) =>
            PassRichText(ref text, settings != null ? settings.ModRichText : LocModRichText.Validate, out reason);

        private static bool PassRichText(ref string text, LocModRichText mode, out string reason)
        {
            reason = null;
            if (mode == LocModRichText.Allow || text.IndexOf('<') < 0) return true;

            if (mode == LocModRichText.Strip)
            {
                text = TagPattern.Replace(text, m => IsToken(m) ? m.Value : string.Empty);
                return true;
            }

            foreach (Match match in TagPattern.Matches(text))
            {
                if (IsToken(match) || SafeTags.Contains(match.Groups[2].Value)) continue;

                reason = $"тег <{match.Groups[2].Value}> не разрешён модам (LocSettings → Моды → Mod Rich Text)";
                return false;
            }

            return true;
        }

        private static bool IsToken(Match match) => match.Groups[1].Length == 0 && match.Groups[3].Value.StartsWith(":");

        // ================================================================ внутреннее: чтение

        private static LocLanguage FindLanguage(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            for (var i = 0; i < LanguageList.Count; i++)
                if (LocSlots.Same(LanguageList[i].Code, code)) return LanguageList[i];
            return null;
        }

        private static string PickStartLanguage(LocSettings settings)
        {
            if (LanguageList.Count == 0) return null;

            if (settings != null && settings.RememberChoice)
            {
                var saved = FindLanguage(PlayerPrefs.GetString(settings.PrefsKey, string.Empty));
                if (saved != null) return saved.Code;
            }

            var mode = settings != null ? settings.StartLanguage : LocStartLanguage.System;
            var picked = mode switch
            {
                LocStartLanguage.System => FindLanguage(SystemLanguageCode()),
                LocStartLanguage.Fixed => FindLanguage(settings.FixedLanguage),
                _ => null,
            };

            return picked?.Code ?? _primary;
        }

        /// <summary>Код языка системы (ISO 639-1).</summary>
        public static string SystemLanguageCode() => Application.systemLanguage switch
        {
            SystemLanguage.Russian => "ru",
            SystemLanguage.Ukrainian => "uk",
            SystemLanguage.Belarusian => "be",
            SystemLanguage.English => "en",
            SystemLanguage.German => "de",
            SystemLanguage.French => "fr",
            SystemLanguage.Spanish => "es",
            SystemLanguage.Italian => "it",
            SystemLanguage.Portuguese => "pt",
            SystemLanguage.Polish => "pl",
            SystemLanguage.Czech => "cs",
            SystemLanguage.Slovak => "sk",
            SystemLanguage.Turkish => "tr",
            SystemLanguage.Dutch => "nl",
            SystemLanguage.Swedish => "sv",
            SystemLanguage.Finnish => "fi",
            SystemLanguage.Norwegian => "no",
            SystemLanguage.Danish => "da",
            SystemLanguage.Japanese => "ja",
            SystemLanguage.Korean => "ko",
            SystemLanguage.Chinese => "zh",
            SystemLanguage.ChineseSimplified => "zh",
            SystemLanguage.ChineseTraditional => "zh-TW",
            SystemLanguage.Vietnamese => "vi",
            SystemLanguage.Thai => "th",
            SystemLanguage.Indonesian => "id",
            _ => "en",
        };

        private static string ResolveOrMissing(string key, string language, string form)
        {
            var value = Resolve(key, language, form, out _) ?? TryResolvers(key, language, out _);
            if (value != null) return value;

            return Missing(key);
        }

        private static string Missing(string key)
        {
            var settings = Settings;
            RequestedMissing.Add(key);

            if (settings == null || settings.WarnMissing)
                if (ReportedMissing.Add(key))
                    Debug.LogWarning(string.IsNullOrEmpty(key) ? "[Loc] Пустой ключ: один знак '^' без имени." : $"[Loc] Нет ключа «{key}».");

            return settings != null ? settings.MissingText : "???";
        }

        /// <summary>
        /// Значение по цепочке: язык → его fallback (и дальше по цепочке) → основной язык →
        /// английский. На каждом языке — сначала форма, потом основное значение.
        /// </summary>
        private static string Resolve(string key, string language, string form, out string usedLanguage)
        {
            usedLanguage = null;
            if (string.IsNullOrEmpty(key) || !Values.TryGetValue(key, out var slots)) return null;

            var visited = 0;
            var current = language;

            while (!string.IsNullOrEmpty(current) && visited++ < 8)
            {
                if (TryLanguage(slots, current, form, out var text))
                {
                    usedLanguage = current;
                    return text;
                }

                var next = FindLanguage(current)?.Fallback;
                if (string.IsNullOrEmpty(next) || LocSlots.Same(next, current)) break;
                current = next;
            }

            if (!string.IsNullOrEmpty(_primary) && TryLanguage(slots, _primary, form, out var primary))
            {
                usedLanguage = _primary;
                return primary;
            }

            if (TryLanguage(slots, "en", form, out var english))
            {
                usedLanguage = "en";
                return english;
            }

            return null;
        }

        private static bool TryLanguage(Dictionary<string, string> slots, string language, string form, out string text)
        {
            if (!string.IsNullOrEmpty(form) && slots.TryGetValue(language + "." + form, out text)) return true;
            return slots.TryGetValue(language, out text);
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;

            // Один упавший подписчик не должен оставить остальные тексты на старом языке.
            foreach (var listener in handler.GetInvocationList())
            {
                try { ((Action)listener)(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
