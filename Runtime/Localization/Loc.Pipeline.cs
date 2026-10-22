using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Exerussus.AppCore.Localization
{
    // Путь строки: перевод слоя (или резолвер) → проходы Pattern (кэш) → аргументы → проходы Text.
    public static partial class Loc
    {
        /// <summary>Префикс в <see cref="WhereFrom"/> для значения, которое дал резолвер.</summary>
        public const string ResolverPrefix = "resolver:";

        // ================================================================ кэш переводов

        // Готовый перевод ключа на текущем языке: текст после проходов Pattern, разобранный шаблон
        // и список проходов Text, которым строка нужна. Сбрасывается при пересборке таблицы, смене языка,
        // изменении проходов или резолверов.
        private sealed class Slot
        {
            public string Key;
            public string Text;
            public string Language;
            public bool Missing;
            public LocTemplate Template;
            public int[] TextPasses;
        }

        // Один и тот же слот по «^KEY» и по «KEY»: Get и GetKey не склеивают строк.
        private static readonly Dictionary<string, Slot> ByPrefixed = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Slot> ByKey = new(StringComparer.Ordinal);

        // Окончания: правило языка, с которого строка реально берётся, и слоты по формам.
        private sealed class PluralEntry
        {
            public string Key;
            public string Rule;
            public readonly Slot[] Slots = new Slot[(int)PluralCategory.Other + 1];
        }

        private static readonly Dictionary<string, PluralEntry> PluralByPrefixed = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, PluralEntry> PluralByKey = new(StringComparer.Ordinal);

        // Разобранные шаблоны по тексту: одинаковый перевод под разными ключами разбирается один раз.
        private static readonly Dictionary<string, LocTemplate> Templates = new(StringComparer.Ordinal);

        // Предупреждения раз на ключ (и на токен).
        private static readonly HashSet<string> ReportedFormat = new(StringComparer.Ordinal);
        private static readonly HashSet<string> ReportedTokens = new(StringComparer.Ordinal);

        private static CultureInfo _culture;

        /// <summary>
        /// Ключи пересчитаны проходом (<see cref="RefreshPass(string,string)"/>): строки с ними пора обновить.
        /// <see cref="LocBinder"/> подписан сам; ключи — без <c>^</c>.
        /// </summary>
        public static event Action<IReadOnlyCollection<string>> KeysRefreshed;

        private static void ClearResolved()
        {
            _culture = null;
            ByPrefixed.Clear();
            ByKey.Clear();
            PluralByPrefixed.Clear();
            PluralByKey.Clear();
            Templates.Clear();
            ReportedFormat.Clear();
            ReportedTokens.Clear();
            ResolverMisses.Clear();
            ResolvedBy.Clear();
            TracedPasses.Clear();
            for (var i = 0; i < PatternPasses.Count; i++) PatternPasses[i].Index.Clear();
            for (var i = 0; i < TextPasses.Count; i++) TextPasses[i].Index.Clear();
        }

        private static void ResetPipeline()
        {
            PatternPasses.Clear();
            TextPasses.Clear();
            Resolvers.Clear();
            ReportedPassFailures.Clear();
            KeysRefreshed = null;
        }

        private static Slot SlotOf(string prefixed)
        {
            if (ByPrefixed.TryGetValue(prefixed, out var slot)) return slot;

            var key = prefixed.Substring(1);
            slot = SlotOfKey(key);
            ByPrefixed[prefixed] = slot;
            return slot;
        }

        private static Slot SlotOfKey(string key)
        {
            if (ByKey.TryGetValue(key, out var slot)) return slot;

            slot = BuildSlot(key, null, true);
            ByKey[key] = slot;
            return slot;
        }

        private static Slot BuildSlot(string key, string form, bool reportMissing)
        {
            var slot = new Slot { Key = key };
            var text = Resolve(key, _language, form, out var used) ?? TryResolvers(key, _language, out _);

            if (text == null)
            {
                slot.Missing = true;
                slot.Text = reportMissing ? Missing(key) : null;
                return slot;
            }

            slot.Text = text;
            slot.Language = used ?? _language;
            if (PatternPasses.Count > 0) ApplyPatternPasses(slot);
            if (TextPasses.Count > 0) PlanTextPasses(slot);
            return slot;
        }

        private static PluralEntry PluralOf(string prefixed)
        {
            if (PluralByPrefixed.TryGetValue(prefixed, out var entry)) return entry;

            entry = PluralOfKey(prefixed.Substring(1));
            PluralByPrefixed[prefixed] = entry;
            return entry;
        }

        private static PluralEntry PluralOfKey(string key)
        {
            if (PluralByKey.TryGetValue(key, out var entry)) return entry;

            entry = new PluralEntry { Key = key };
            Resolve(key, _language, null, out var used);
            var language = used ?? _language;
            entry.Rule = PluralRules.RuleFor(FindLanguage(language) ?? new LocLanguage(language, null));
            PluralByKey[key] = entry;
            return entry;
        }

        private static Slot PluralSlot(PluralEntry entry, double count)
        {
            var category = PluralRules.Select(entry.Rule, count);
            var slot = entry.Slots[(int)category];
            if (slot != null) return slot;

            var form = category == PluralCategory.Other ? null : LocSlots.FormOf(category);
            slot = BuildSlot(entry.Key, form, true);
            entry.Slots[(int)category] = slot;
            return slot;
        }

        private static LocTemplate TemplateOf(string pattern)
        {
            if (Templates.TryGetValue(pattern, out var template)) return template;

            template = LocTemplate.Parse(pattern);
            Templates[pattern] = template;
            return template;
        }

        // ================================================================ чтение

        /// <summary>
        /// Перевод ключа на текущем языке. Строка без <c>^</c> — как есть. Повторный вызов —
        /// поиск в словаре без аллокаций (если строку не хочет проход стадии Text).
        /// </summary>
        public static string Get(string text)
        {
            if (!IsKey(text) || !EnsureInitialized()) return text;
            var slot = SlotOf(text);
            return slot.TextPasses == null ? slot.Text : ApplyTextPasses(slot, slot.Text, null);
        }

        /// <summary>Как <c>Get("^" + key)</c>, без склейки строк: ключ без префикса, тот же кэш.</summary>
        public static string GetKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (!EnsureInitialized()) return Prefix + key;

            var slot = SlotOfKey(key);
            return slot.TextPasses == null ? slot.Text : ApplyTextPasses(slot, slot.Text, null);
        }

        /// <summary>Перевод ключа без префикса. False — нет нигде и резолвер не дал (заглушка не подставляется).</summary>
        public static bool TryGetKey(string key, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(key) || !EnsureInitialized()) return false;

            if (!ByKey.TryGetValue(key, out var slot))
            {
                slot = BuildSlot(key, null, false);
                if (slot.Missing) return false;   // промах не кэшируем: следующий Get предупредит о нём
                ByKey[key] = slot;
            }

            if (slot.Missing) return false;
            text = slot.TextPasses == null ? slot.Text : ApplyTextPasses(slot, slot.Text, null);
            return true;
        }

        /// <summary>
        /// Шаблон окончания для числа, без подстановки и без проходов Text. Правило — того языка, с которого
        /// строка на деле берётся (ключ без перевода показывается с фолбэка — и склоняется по его правилу).
        /// </summary>
        public static string PluralPattern(string text, double count)
        {
            if (!IsKey(text) || !EnsureInitialized()) return text;
            return PluralSlot(PluralOf(text), count).Text;
        }

        // ================================================================ подстановка

        /// <summary>Перевод без аргументов (то же, что <see cref="Get(string)"/>).</summary>
        public static string Format(string text) => Get(text);

        /// <summary>Перевод с одним позиционным аргументом <c>{0}</c> — без массива.</summary>
        public static string Format(string text, object arg0)
        {
            var args = new ObjectArg(arg0);
            return Compose(text, ref args);
        }

        /// <summary>Перевод с позиционными аргументами <c>{0}</c>, <c>{1}</c>…</summary>
        public static string Format(string text, params object[] args)
        {
            if (args == null || args.Length == 0) return Get(text);
            var source = new ObjectArgs(args);
            return Compose(text, ref source);
        }

        /// <summary>Перевод с именованным аргументом: <c>Format("^POINTS", ("count", 3))</c>. Без упаковки чисел.</summary>
        public static string Format<T0>(string text, (string name, T0 value) a0)
        {
            var args = new NamedArg<T0>(a0);
            return Compose(text, ref args);
        }

        public static string Format<T0, T1>(string text, (string name, T0 value) a0, (string name, T1 value) a1)
        {
            var args = new NamedArg<T0, T1>(a0, a1);
            return Compose(text, ref args);
        }

        public static string Format<T0, T1, T2>(string text, (string name, T0 value) a0, (string name, T1 value) a1, (string name, T2 value) a2)
        {
            var args = new NamedArg<T0, T1, T2>(a0, a1, a2);
            return Compose(text, ref args);
        }

        /// <summary>Перевод с именованными аргументами (четыре и больше).</summary>
        public static string Format(string text, params (string name, object value)[] args)
        {
            if (args == null || args.Length == 0) return Get(text);
            var source = new NamedArgs(args);
            return Compose(text, ref source);
        }

        /// <summary>Ключ без <c>^</c> с именованными аргументами.</summary>
        public static string FormatKey(string key) => GetKey(key);

        public static string FormatKey<T0>(string key, (string name, T0 value) a0)
        {
            var args = new NamedArg<T0>(a0);
            return ComposeKey(key, ref args);
        }

        public static string FormatKey<T0, T1>(string key, (string name, T0 value) a0, (string name, T1 value) a1)
        {
            var args = new NamedArg<T0, T1>(a0, a1);
            return ComposeKey(key, ref args);
        }

        public static string FormatKey<T0, T1, T2>(string key, (string name, T0 value) a0, (string name, T1 value) a1, (string name, T2 value) a2)
        {
            var args = new NamedArg<T0, T1, T2>(a0, a1, a2);
            return ComposeKey(key, ref args);
        }

        public static string FormatKey(string key, params (string name, object value)[] args)
        {
            if (args == null || args.Length == 0) return GetKey(key);
            var source = new NamedArgs(args);
            return ComposeKey(key, ref source);
        }

        /// <summary>Подставляет позиционные аргументы в уже переведённый шаблон (культура языка, предупреждение раз на ключ).</summary>
        public static string FormatPattern(string key, string pattern, object[] args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrEmpty(pattern)) return pattern;
            var source = new ObjectArgs(args);
            return Render(TemplateOf(pattern), ref source, key);
        }

        /// <summary>Подставляет именованные аргументы в уже переведённый шаблон.</summary>
        public static string FormatPattern(string key, string pattern, params (string name, object value)[] args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrEmpty(pattern)) return pattern;
            var source = new NamedArgs(args);
            return Render(TemplateOf(pattern), ref source, key);
        }

        /// <summary>Шаблон окончаний: число — <c>{0}</c>, остальные аргументы — <c>{1}</c> и дальше.</summary>
        public static string FormatPattern(string key, string pattern, double count, object[] args)
        {
            if (string.IsNullOrEmpty(pattern)) return pattern;
            var source = new PluralArgs<ObjectArgs>(count, new ObjectArgs(args));
            return Render(TemplateOf(pattern), ref source, key);
        }

        // ================================================================ окончания

        /// <summary>Перевод с окончанием по числу: число — <c>{0}</c>.</summary>
        public static string Plural(string text, double count)
        {
            var args = new PluralArgs<NoArgs>(count, default);
            return ComposePlural(text, ref args, count, null);
        }

        /// <summary>Окончание по целому числу без массива параметров.</summary>
        public static string Plural(string text, int count) => Plural(text, (double)count);

        /// <summary>
        /// Перевод с окончанием по числу. Форма выбирается правилом языка строки; число подставляется
        /// как <c>{0}</c>, остальные аргументы — <c>{1}</c> и дальше. Нет нужной формы — основное значение.
        /// </summary>
        public static string Plural(string text, double count, params object[] args)
        {
            var source = new PluralArgs<ObjectArgs>(count, new ObjectArgs(args));
            return ComposePlural(text, ref source, count, null);
        }

        /// <inheritdoc cref="Plural(string,double,object[])"/>
        public static string Plural(string text, int count, params object[] args) => Plural(text, (double)count, args);

        /// <summary>Окончание по числу (<c>{0}</c>) и именованные аргументы.</summary>
        public static string Plural(string text, double count, params (string name, object value)[] args)
        {
            var source = new PluralArgs<NamedArgs>(count, new NamedArgs(args));
            return ComposePlural(text, ref source, count, null);
        }

        /// <summary>Окончание по целому числу и именованные аргументы.</summary>
        public static string Plural(string text, int count, params (string name, object value)[] args) => Plural(text, (double)count, args);

        /// <summary>Окончание по ключу без <c>^</c>.</summary>
        public static string PluralKey(string key, double count)
        {
            var args = new PluralArgs<NoArgs>(count, default);
            return ComposePluralKey(key, ref args, count);
        }

        public static string PluralKey<T0>(string key, double count, (string name, T0 value) a0)
        {
            var args = new PluralArgs<NamedArg<T0>>(count, new NamedArg<T0>(a0));
            return ComposePluralKey(key, ref args, count);
        }

        public static string PluralKey<T0, T1>(string key, double count, (string name, T0 value) a0, (string name, T1 value) a1)
        {
            var args = new PluralArgs<NamedArg<T0, T1>>(count, new NamedArg<T0, T1>(a0, a1));
            return ComposePluralKey(key, ref args, count);
        }

        public static string PluralKey(string key, double count, params (string name, object value)[] args)
        {
            var source = new PluralArgs<NamedArgs>(count, new NamedArgs(args));
            return ComposePluralKey(key, ref source, count);
        }

        /// <summary>Удваивает <c>{</c> и <c>}</c>: данные игры, вставленные в шаблон, не разберутся как токены.</summary>
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text) || (text.IndexOf('{') < 0 && text.IndexOf('}') < 0)) return text;
            return text.Replace("{", "{{").Replace("}", "}}");
        }

        // ================================================================ внутреннее: сборка строки

        private static string Compose<TArgs>(string text, ref TArgs args) where TArgs : struct, ILocArgs
        {
            if (!IsKey(text) || !EnsureInitialized()) return RenderLoose(text, ref args);
            return Finish(SlotOf(text), ref args, null);
        }

        private static string ComposeKey<TArgs>(string key, ref TArgs args) where TArgs : struct, ILocArgs
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (!EnsureInitialized()) return Prefix + key;
            return Finish(SlotOfKey(key), ref args, null);
        }

        private static string ComposePlural<TArgs>(string text, ref TArgs args, double count, object owner) where TArgs : struct, ILocArgs
        {
            if (!IsKey(text) || !EnsureInitialized()) return RenderLoose(text, ref args);
            return Finish(PluralSlot(PluralOf(text), count), ref args, owner);
        }

        private static string ComposePluralKey<TArgs>(string key, ref TArgs args, double count) where TArgs : struct, ILocArgs
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (!EnsureInitialized()) return Prefix + key;
            return Finish(PluralSlot(PluralOfKey(key), count), ref args, null);
        }

        /// <summary>Сборка для <see cref="LocBinder"/>: позиционные и именованные аргументы, окончания, владелец.</summary>
        internal static string ComposeBinding(string template, object[] args, (string name, object value)[] named, bool plural, double count, object owner)
        {
            var source = new MixedArgs(args, named);
            var hasArgs = (args != null && args.Length > 0) || (named != null && named.Length > 0);

            if (plural)
            {
                var pluralArgs = new PluralArgs<MixedArgs>(count, source);
                return ComposePlural(template, ref pluralArgs, count, owner);
            }

            if (!IsKey(template) || !EnsureInitialized()) return hasArgs ? RenderLoose(template, ref source) : template;

            var slot = SlotOf(template);
            if (!hasArgs) return slot.TextPasses == null ? slot.Text : ApplyTextPasses(slot, slot.Text, owner);
            return Finish(slot, ref source, owner);
        }

        private static string Finish<TArgs>(Slot slot, ref TArgs args, object owner) where TArgs : struct, ILocArgs
        {
            if (slot.Missing) return slot.Text;

            slot.Template ??= TemplateOf(slot.Text);
            var text = slot.Template.NeedsRender ? Render(slot.Template, ref args, slot.Key) : slot.Text;
            return slot.TextPasses == null ? text : ApplyTextPasses(slot, text, owner);
        }

        // Строка без ключа (или локализация выключена): подстановка без кэша шаблона — такие строки
        // бывают собраны на лету, и кэш по ним рос бы без конца.
        private static string RenderLoose<TArgs>(string text, ref TArgs args) where TArgs : struct, ILocArgs
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;
            return Render(LocTemplate.Parse(text), ref args, null);
        }

        private static string Render<TArgs>(LocTemplate template, ref TArgs args, string key) where TArgs : struct, ILocArgs
        {
            string result;
            bool missingPositional;
            string unknownName;
            try
            {
                result = template.Render(ref args, Culture, out missingPositional, out unknownName);
            }
            catch (Exception e)
            {
                if (ReportedFormat.Add(key ?? string.Empty))
                    Debug.LogWarning($"[Loc] Перевод «{key}» не собрался: {e.Message}. Шаблон: «{template.Source}».");
                return template.Source;
            }

            if (missingPositional && ReportedFormat.Add(key ?? string.Empty))
                Debug.LogWarning($"[Loc] Перевод «{key}» не подходит под аргументы: «{template.Source}».");

            if (unknownName != null)
            {
                var settings = Settings;
                if (settings != null && settings.WarnUnknownTokens && ReportedTokens.Add((key ?? string.Empty) + "\n" + unknownName))
                    Debug.LogWarning($"[Loc] «{key}»: токен {{{unknownName}}} без аргумента — остался в строке.");
            }

            return result;
        }

        // ================================================================ проходы

        private sealed class PassReg
        {
            public string Id;
            public ILocPass Pass;
            public LocPassStage Stage;
            public object Owner;
            public LocSource Source;

            // зависимость → ключи, которым она нужна
            public readonly Dictionary<string, HashSet<string>> Index = new(StringComparer.Ordinal);
        }

        private static readonly List<PassReg> PatternPasses = new();
        private static readonly List<PassReg> TextPasses = new();
        private static readonly List<string> Dependencies = new();
        private static readonly HashSet<string> ReportedPassFailures = new(StringComparer.Ordinal);
        private static readonly HashSet<string> TracedPasses = new(StringComparer.Ordinal);

        /// <summary>
        /// Подключает проход. Порядок применения — порядок добавления (отдельно по стадиям), приоритетов нет.
        /// Строки, которые проход хочет, пересобираются.
        /// </summary>
        /// <param name="id">Уникальный id прохода (задаёт вызывающий).</param>
        /// <param name="source">Проход мода (<see cref="LocSource.Mod"/>) подчиняется тем же правилам, что слои модов.</param>
        /// <returns>False — локализация выключена, id занят или пуст, прохода нет.</returns>
        public static bool AddPass(string id, ILocPass pass, LocPassStage stage, object owner = null, LocSource source = LocSource.Project)
        {
            if (!EnsureInitialized()) return false;

            if (pass == null || string.IsNullOrWhiteSpace(id))
            {
                Debug.LogError("[Loc] У прохода должны быть id и реализация.");
                return false;
            }

            if (FindPass(id) != null)
            {
                Debug.LogError($"[Loc] Проход «{id}» уже добавлен: id проходов должны быть уникальными.");
                return false;
            }

            if (source != LocSource.Mod) source = LocSource.Project;

            var reg = new PassReg { Id = id, Pass = pass, Stage = stage, Owner = owner, Source = source };
            (stage == LocPassStage.Pattern ? PatternPasses : TextPasses).Add(reg);
            InvalidateResolved();
            return true;
        }

        /// <summary>Снимает проход.</summary>
        public static bool RemovePass(string id)
        {
            if (!_initialized || string.IsNullOrEmpty(id)) return false;

            var removed = PatternPasses.RemoveAll(p => p.Id == id) + TextPasses.RemoveAll(p => p.Id == id);
            if (removed > 0) InvalidateResolved();
            return removed > 0;
        }

        /// <summary>Снимает все проходы владельца. Возвращает, сколько снято.</summary>
        public static int RemovePasses(object owner)
        {
            if (!_initialized || owner == null) return 0;

            var removed = PatternPasses.RemoveAll(p => ReferenceEquals(p.Owner, owner)) +
                          TextPasses.RemoveAll(p => ReferenceEquals(p.Owner, owner));
            if (removed > 0) InvalidateResolved();
            return removed;
        }

        /// <summary>Состояние прохода сменилось: пересчитать всё, что он хочет.</summary>
        public static void RefreshPass(string id) => RefreshPassCore(id, null);

        /// <summary>Сменилось одно значение: пересчитать только строки с этой зависимостью.</summary>
        public static void RefreshPass(string id, string dependency) => RefreshPassCore(id, dependency ?? string.Empty);

        private static void RefreshPassCore(string id, string dependency)
        {
            if (!_initialized || !_active) return;

            var reg = FindPass(id);
            if (reg == null || reg.Index.Count == 0) return;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (dependency == null)
            {
                foreach (var set in reg.Index.Values) keys.UnionWith(set);
            }
            else
            {
                if (!reg.Index.TryGetValue(dependency, out var set)) return;
                keys.UnionWith(set);
            }

            if (keys.Count == 0) return;

            // Pattern — результат лежит в кэше перевода: выбрасываем, пересоберётся при показе.
            if (reg.Stage == LocPassStage.Pattern)
                foreach (var key in keys) ForgetKey(key);

            var handler = KeysRefreshed;
            if (handler == null) return;

            foreach (var listener in handler.GetInvocationList())
            {
                try { ((Action<IReadOnlyCollection<string>>)listener)(keys); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        private static void ForgetKey(string key)
        {
            ByKey.Remove(key);
            ByPrefixed.Remove(Prefix + key);
            PluralByKey.Remove(key);
            PluralByPrefixed.Remove(Prefix + key);
        }

        /// <summary>Подключённые проходы: сначала стадии Pattern, затем Text, по порядку применения.</summary>
        public static IReadOnlyList<LocPassInfo> Passes
        {
            get
            {
                var list = new List<LocPassInfo>();
                if (!EnsureInitialized()) return list;

                foreach (var reg in PatternPasses) list.Add(Info(reg));
                foreach (var reg in TextPasses) list.Add(Info(reg));
                return list;
            }
        }

        private static LocPassInfo Info(PassReg reg)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var set in reg.Index.Values) keys.UnionWith(set);
            return new LocPassInfo(reg.Id, reg.Stage, reg.Owner, reg.Source, reg.Pass.GetType().Assembly.GetName().Name, keys.Count);
        }

        private static PassReg FindPass(string id)
        {
            for (var i = 0; i < PatternPasses.Count; i++) if (PatternPasses[i].Id == id) return PatternPasses[i];
            for (var i = 0; i < TextPasses.Count; i++) if (TextPasses[i].Id == id) return TextPasses[i];
            return null;
        }

        /// <summary>Проходы, резолверы — изменились: кэш переводов устарел, тексты пересобираются.</summary>
        private static void InvalidateResolved()
        {
            if (_batchDepth > 0)
            {
                _batchDirty = true;
                return;
            }

            ClearResolved();
            RaiseChanged();
        }

        private static bool Wants(PassReg reg, Slot slot)
        {
            Dependencies.Clear();
            try { reg.Pass.Wants(slot.Key, slot.Text, Dependencies); }
            catch (Exception e)
            {
                ReportPassFailure(reg, slot.Key, e);
                return false;
            }

            if (Dependencies.Count == 0) return false;

            for (var i = 0; i < Dependencies.Count; i++)
            {
                var dependency = Dependencies[i] ?? string.Empty;
                if (!reg.Index.TryGetValue(dependency, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    reg.Index.Add(dependency, keys);
                }

                keys.Add(slot.Key);
            }

            return true;
        }

        private static void ApplyPatternPasses(Slot slot)
        {
            for (var i = 0; i < PatternPasses.Count; i++)
            {
                var reg = PatternPasses[i];
                if (!Wants(reg, slot)) continue;

                slot.Text = RunPass(reg, slot, slot.Text, null, LocPassStage.Pattern);
            }
        }

        private static void PlanTextPasses(Slot slot)
        {
            List<int> plan = null;
            for (var i = 0; i < TextPasses.Count; i++)
            {
                if (!Wants(TextPasses[i], slot)) continue;
                (plan ??= new List<int>(2)).Add(i);
            }

            slot.TextPasses = plan?.ToArray();
        }

        private static string ApplyTextPasses(Slot slot, string text, object owner)
        {
            var plan = slot.TextPasses;
            for (var i = 0; i < plan.Length; i++)
            {
                if (plan[i] >= TextPasses.Count) break;
                text = RunPass(TextPasses[plan[i]], slot, text, owner, LocPassStage.Text);
            }

            return text;
        }

        private static string RunPass(PassReg reg, Slot slot, string text, object owner, LocPassStage stage)
        {
            var settings = Settings;
            var isMod = reg.Source == LocSource.Mod;

            if (isMod && IsProtected(slot.Key, settings))
            {
                ReportPassRejected(reg, slot.Key, "ключ защищён от модов");
                return text;
            }

            string result;
            try { result = reg.Pass.Apply(new LocPassContext(slot.Key, slot.Language, owner, stage), text); }
            catch (Exception e)
            {
                ReportPassFailure(reg, slot.Key, e);
                return text;
            }

            if (result == null || ReferenceEquals(result, text) || result == text) return text;

            // Проход мода — те же правила разметки, что у слоёв модов: недопустимое изменение отбрасывается.
            if (isMod && !ModPassMarkupOk(result, settings, out var reason))
            {
                ReportPassRejected(reg, slot.Key, reason);
                return text;
            }

            if (_traceActive && TracedPasses.Add(reg.Id + "\n" + slot.Key + "\n" + slot.Language))
                AddStep(slot.Key, new LocStep
                {
                    LayerId = reg.Id, Source = reg.Source, Kind = LocStepKind.Pass, Language = slot.Language,
                    Previous = text, Value = result, Reason = stage == LocPassStage.Pattern ? "Pattern" : "Text",
                });

            return result;
        }

        private static bool ModPassMarkupOk(string text, LocSettings settings, out string reason)
        {
            reason = null;
            var mode = settings != null ? settings.ModRichText : LocModRichText.Validate;
            if (mode == LocModRichText.Allow) return true;

            // Strip для прохода — то же, что Validate: вырезать разметку из готовой строки нельзя, она могла
            // прийти из слоёв проекта. Изменение с запрещённым тегом отбрасывается целиком.
            var copy = text;
            return PassRichText(ref copy, LocModRichText.Validate, out reason);
        }

        private static void ReportPassFailure(PassReg reg, string key, Exception e)
        {
            if (ReportedPassFailures.Add(reg.Id + "\n" + key))
                Debug.LogError($"[Loc] Проход «{reg.Id}» упал на ключе «{key}» — строка показана без него.\n{e}");
        }

        private static void ReportPassRejected(PassReg reg, string key, string reason)
        {
            if (!ReportedPassFailures.Add(reg.Id + "\n" + key + "\nrejected")) return;

            Debug.LogWarning($"[Loc] Проход мода «{reg.Id}», ключ «{key}»: изменение отклонено — {reason}.");
            if (_traceActive)
                AddStep(key, new LocStep { LayerId = reg.Id, Source = reg.Source, Kind = LocStepKind.Rejected, Reason = reason });
        }

        // ================================================================ тесты

        /// <summary>Полный сброс состояния (тесты). Подписчики <see cref="Changed"/> тоже снимаются.</summary>
        internal static void ResetForTests() => ResetStatics();

        /// <summary>Язык без записи выбора в PlayerPrefs (тесты).</summary>
        internal static void UseLanguageForTests(string code)
        {
            if (!EnsureInitialized()) return;
            _language = FindLanguage(code)?.Code ?? code;
            ClearResolved();
            RaiseChanged();
        }

        // ================================================================ резолверы

        private sealed class ResolverReg
        {
            public string Id;
            public LocResolver Resolver;
            public object Owner;
        }

        private static readonly List<ResolverReg> Resolvers = new();

        // Промахи «ключ\nязык»: ни один резолвер не дал — не спрашиваем снова до сброса.
        private static readonly HashSet<string> ResolverMisses = new(StringComparer.Ordinal);

        // Ключ → резолвер, который дал значение (для WhereFrom и History).
        private static readonly Dictionary<string, string> ResolvedBy = new(StringComparer.Ordinal);

        /// <summary>
        /// Подключает резолвер ключей, которых нет ни в одном слое ни на одном языке цепочки фолбэков.
        /// Первый вернувший true побеждает; порядок — порядок добавления.
        /// </summary>
        public static bool AddResolver(string id, LocResolver resolver, object owner = null)
        {
            if (!EnsureInitialized()) return false;

            if (resolver == null || string.IsNullOrWhiteSpace(id))
            {
                Debug.LogError("[Loc] У резолвера должны быть id и функция.");
                return false;
            }

            if (Resolvers.Exists(r => r.Id == id))
            {
                Debug.LogError($"[Loc] Резолвер «{id}» уже добавлен: id резолверов должны быть уникальными.");
                return false;
            }

            Resolvers.Add(new ResolverReg { Id = id, Resolver = resolver, Owner = owner });
            InvalidateResolved();
            return true;
        }

        public static bool RemoveResolver(string id)
        {
            if (!_initialized || string.IsNullOrEmpty(id)) return false;

            var removed = Resolvers.RemoveAll(r => r.Id == id) > 0;
            if (removed) InvalidateResolved();
            return removed;
        }

        public static int RemoveResolvers(object owner)
        {
            if (!_initialized || owner == null) return 0;

            var removed = Resolvers.RemoveAll(r => ReferenceEquals(r.Owner, owner));
            if (removed > 0) InvalidateResolved();
            return removed;
        }

        /// <summary>
        /// Данные резолверов изменились (каталог загружен, мод включён): забыть промахи и пересобрать тексты.
        /// </summary>
        public static void RefreshResolvers()
        {
            if (!_initialized || !_active || Resolvers.Count == 0) return;
            InvalidateResolved();
        }

        /// <summary>Подключённые резолверы по порядку.</summary>
        public static IReadOnlyList<string> ResolverIds
        {
            get
            {
                var list = new List<string>(Resolvers.Count);
                foreach (var reg in Resolvers) list.Add(reg.Id);
                return list;
            }
        }

        /// <summary>Ключи, которые закрыл резолвер (среди уже показанных): ключ → id резолвера.</summary>
        public static IReadOnlyDictionary<string, string> ResolvedKeys => ResolvedBy;

        private static string TryResolvers(string key, string language, out string resolverId)
        {
            resolverId = null;
            if (Resolvers.Count == 0 || string.IsNullOrEmpty(key)) return null;

            var miss = key + "\n" + language;
            if (ResolverMisses.Contains(miss)) return null;

            for (var i = 0; i < Resolvers.Count; i++)
            {
                var reg = Resolvers[i];
                string text;
                bool found;
                try { found = reg.Resolver(key, language, out text); }
                catch (Exception e)
                {
                    if (ReportedPassFailures.Add("resolver\n" + reg.Id + "\n" + key))
                        Debug.LogError($"[Loc] Резолвер «{reg.Id}» упал на ключе «{key}».\n{e}");
                    continue;
                }

                if (!found || text == null) continue;

                resolverId = reg.Id;
                ResolvedBy[key] = reg.Id;
                return text;
            }

            ResolverMisses.Add(miss);
            return null;
        }
    }
}
