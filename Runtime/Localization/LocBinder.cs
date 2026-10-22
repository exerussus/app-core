using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Вторая ступень после перевода: проект дорабатывает строку — подставляет живые значения,
    /// раскрашивает куски. Работа разделена на дорогую и дешёвую часть:
    /// <see cref="Prepare"/> зовётся раз на перевод (разобрать строку, подписаться на данные),
    /// <see cref="Build"/> — на каждое изменение данных (склеить готовые куски).
    /// </summary>
    public interface ILocTextProcessor
    {
        /// <summary>
        /// Нужна ли привязка строке без ключа. Например, в ней есть токены значений, которые
        /// должны обновляться: такую строку биндер держит, хотя переводить в ней нечего.
        /// </summary>
        bool Wants(string template);

        /// <summary>
        /// Разбирает переведённую строку. Подписки на данные — через <see cref="LocBinding.Track"/>,
        /// изменение — <see cref="LocBinding.Invalidate"/> (только если видимый текст и правда
        /// изменился: значение может меняться каждый кадр, а показ — раз в секунду).
        /// <c>null</c> — дорабатывать нечего, показывается сама строка.
        /// </summary>
        object Prepare(string text, LocBinding binding);

        /// <summary>Собирает строку из разобранного состояния. Зовётся часто — без разбора и без лишних аллокаций.</summary>
        string Build(object state, LocBinding binding);
    }

    /// <summary>
    /// Элемент, который сам собирает часть своих текстов (составной компонент: строки по состоянию,
    /// форматирование). <see cref="LocBinder.BindTree"/> запоминает его и на смене языка зовёт
    /// <see cref="Relocalize"/> — после пересборки привязанных строк. Обход дерева внутрь него
    /// при этом продолжается: ключи в его вёрстке переводятся как обычно.
    /// </summary>
    public interface ILocalizedElement
    {
        void Relocalize();
    }

    /// <summary>Привязка одного элемента: шаблон, аргументы, состояние процессора, подписки.</summary>
    public sealed class LocBinding
    {
        private readonly LocBinder _owner;
        private readonly List<IDisposable> _tracked = new();

        private Func<string, string> _decorate;
        private string _decorateIn;
        private string _decorateOut;

        private object[] _args;
        private (string name, object value)[] _named;
        private bool _plural;
        private double _count;

        private object _state;
        private string _source;
        private string _written;
        private bool _foreignReported;

        internal bool Released;
        internal bool Sleeping;
        internal bool Dirty;
        internal int SleptAtFrame;

        /// <summary>Элемент, которому пишется текст.</summary>
        public TextElement Element { get; }

        /// <summary>Исходная строка (ключ или текст с токенами) — источник каждой сборки.</summary>
        public string Template { get; }

        /// <summary>Ключ без <c>^</c>; <c>null</c> — шаблон не ключ.</summary>
        internal string Key { get; }

        /// <summary>Идёт разбор (<see cref="ILocTextProcessor.Prepare"/>): подписки можно ставить.</summary>
        public bool IsTracking { get; private set; }

        /// <summary>Контекст для логов (владелец биндера).</summary>
        public Object Context => _owner.Context;

        internal LocBinding(LocBinder owner, TextElement element, string template)
        {
            _owner = owner;
            Element = element;
            Template = template;
            Key = Loc.IsKey(template) ? template.Substring(1) : null;
        }

        /// <summary>Подписка процессора. Снимается на смене языка, при сне и при отвязке элемента.</summary>
        public void Track(IDisposable subscription)
        {
            if (subscription == null) return;

            if (Released || !IsTracking)
            {
                subscription.Dispose();
                return;
            }

            _tracked.Add(subscription);
        }

        /// <summary>
        /// Данные изменились — пересобрать текст. Сборка откладывается и склеивается: одна на биндер
        /// за кадр, сколько бы значений ни поменялось. Скрытый (приостановленный) биндер копит.
        /// </summary>
        public void Invalidate()
        {
            if (Released || Sleeping || Dirty) return;

            Dirty = true;
            _owner.EnqueueDirty(this);
        }

        // ---------- настройка ----------

        internal bool SameAs(Func<string, string> decorate, object[] args, (string name, object value)[] named, bool plural, double count) =>
            Equals(_decorate, decorate) && SameExceptDecorate(args, named, plural, count);

        /// <summary>Те же шаблон и аргументы, другое оформление: подписки не трогаем.</summary>
        internal bool SameExceptDecorate(object[] args, (string name, object value)[] named, bool plural, double count) =>
            _plural == plural && (!plural || _count.Equals(count)) && SameArgs(_args, args) && SameNamed(_named, named);

        /// <summary>
        /// Один именованный аргумент того же значения — без упаковки: повторный <c>Set</c> с тем же числом
        /// каждый кадр ничего не стоит.
        /// </summary>
        internal bool SameSingle<T0>(Func<string, string> decorate, (string name, T0 value) a0, bool plural, double count) =>
            _decorate == null && decorate == null && _args == null && _plural == plural && (!plural || _count.Equals(count)) &&
            _named != null && _named.Length == 1 && Same(_named[0], a0);

        internal bool SameDouble<T0, T1>((string name, T0 value) a0, (string name, T1 value) a1) =>
            _decorate == null && _args == null && !_plural && _named != null && _named.Length == 2 &&
            Same(_named[0], a0) && Same(_named[1], a1);

        internal bool SameTriple<T0, T1, T2>((string name, T0 value) a0, (string name, T1 value) a1, (string name, T2 value) a2) =>
            _decorate == null && _args == null && !_plural && _named != null && _named.Length == 3 &&
            Same(_named[0], a0) && Same(_named[1], a1) && Same(_named[2], a2);

        private static bool Same<T>((string name, object value) stored, (string name, T value) given) =>
            string.Equals(stored.name, given.name, StringComparison.Ordinal) &&
            stored.value is T previous && EqualityComparer<T>.Default.Equals(previous, given.value);

        internal void Configure(Func<string, string> decorate, object[] args, (string name, object value)[] named, bool plural, double count)
        {
            _decorate = decorate;
            _decorateIn = null;
            _decorateOut = null;
            _args = args != null && args.Length > 0 ? args : null;
            _named = named != null && named.Length > 0 ? named : null;
            _plural = plural;
            _count = count;
        }

        internal void SetDecorate(Func<string, string> decorate)
        {
            _decorate = decorate;
            _decorateIn = null;
            _decorateOut = null;
        }

        private static bool SameNamed((string name, object value)[] a, (string name, object value)[] b)
        {
            var la = a?.Length ?? 0;
            var lb = b?.Length ?? 0;
            if (la != lb) return false;
            for (var i = 0; i < la; i++)
                if (!string.Equals(a[i].name, b[i].name, StringComparison.Ordinal) || !Equals(a[i].value, b[i].value)) return false;
            return true;
        }

        private static bool SameArgs(object[] a, object[] b)
        {
            var la = a?.Length ?? 0;
            var lb = b?.Length ?? 0;
            if (la != lb) return false;
            for (var i = 0; i < la; i++)
                if (!Equals(a[i], b[i])) return false;
            return true;
        }

        // ---------- сборка ----------

        /// <summary>Полная сборка: перевод, аргументы, разбор процессором, подписки заново.</summary>
        internal void Refresh()
        {
            if (Released) return;

            DisposeTracked();
            Sleeping = false;
            Dirty = false;

            _source = Resolve();
            _state = null;

            var processor = _owner.Processor;
            if (processor != null)
            {
                IsTracking = true;
                try { _state = processor.Prepare(_source, this); }
                catch (Exception e) { Debug.LogException(e, _owner.Context); }
                finally { IsTracking = false; }
            }

            Write();
        }

        /// <summary>Частичная сборка: только склейка из готового разбора. Перевод не перечитывается.</summary>
        internal void Rebuild()
        {
            Dirty = false;
            if (Released || Sleeping) return;
            Write();
        }

        // Перевод → проходы Pattern → аргументы → проходы Text (с владельцем биндера).
        private string Resolve() => Loc.ComposeBinding(Template, _args, _named, _plural, _count, _owner.Context);

        private void Write()
        {
            var text = _source;

            if (_state != null)
            {
                try { text = _owner.Processor.Build(_state, this); }
                catch (Exception e) { Debug.LogException(e, _owner.Context); }
            }

            if (_decorate != null)
            {
                // Оформление дорогое (регулярки), а вход часто тот же — кэш последней пары.
                if (!ReferenceEquals(text, _decorateIn) && text != _decorateIn)
                {
                    _decorateIn = text;
                    try { _decorateOut = _decorate(text); }
                    catch (Exception e)
                    {
                        Debug.LogException(e, _owner.Context);
                        _decorateOut = text;
                    }
                }

                text = _decorateOut;
            }

            if (Debug.isDebugBuild && _written != null && !_foreignReported && Element.text != _written)
            {
                // Кто-то пишет в элемент мимо биндера: следующая сборка его перетрёт.
                _foreignReported = true;
                Debug.LogWarning($"[Loc] Текст элемента «{Element.name}» (шаблон «{Template}») изменён в обход биндера — " +
                                 "ставьте его через Set/SetRaw, иначе смена языка вернёт шаблон.", _owner.Context);
            }

            if (text == _written) return;

            _written = text;
            Element.text = text;
        }

        // ---------- жизненный цикл элемента ----------

        internal void Attach()
        {
            Element.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            Element.RegisterCallback<AttachToPanelEvent>(OnAttach);
        }

        /// <summary>
        /// Элемент ушёл с панели: засыпаем — подписки снимаются, шаблон остаётся. Вернётся
        /// (переприцепили, включили панель) — проснётся полной сборкой.
        /// </summary>
        private void OnDetach(DetachFromPanelEvent evt) => Sleep();

        internal void Sleep()
        {
            if (Released) return;

            Sleeping = true;
            SleptAtFrame = Time.frameCount;
            DisposeTracked();
        }

        private void OnAttach(AttachToPanelEvent evt)
        {
            if (Released || !Sleeping) return;
            Refresh();
        }

        internal void Release()
        {
            if (Released) return;
            Released = true;

            Element.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
            Element.UnregisterCallback<AttachToPanelEvent>(OnAttach);
            DisposeTracked();
            _state = null;
        }

        private void DisposeTracked()
        {
            for (var i = 0; i < _tracked.Count; i++)
            {
                try { _tracked[i].Dispose(); }
                catch (Exception e) { Debug.LogException(e, _owner.Context); }
            }

            _tracked.Clear();
        }
    }

    /// <summary>
    /// Держатель переведённых текстов одного дерева: обходит вёрстку, переводит ключи <c>^</c>,
    /// помнит шаблоны и пересобирает тексты на смене языка и при изменении данных.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Показанный текст никогда не служит исходником: у каждого элемента хранится шаблон, и любая
    /// пересборка идёт от него. Иначе смена языка не нашла бы ключ — он уже заменён текстом.
    /// </para>
    /// <para>
    /// Стоимость: перевод и разбор строки — раз на язык; изменение данных — одна склейка готовых
    /// кусков, одна запись в элемент и только если видимый текст изменился. Изменения собираются
    /// в одну очередь на биндер и обрабатываются раз за кадр; <see cref="Suspend"/> (скрытый
    /// монитор) копит их до <see cref="Resume"/>.
    /// </para>
    /// <para>
    /// Страницам, попапам, фрагментам и скринам AppCore заводит биндер сам (у контроллера — свойство
    /// <c>Text</c>). Свой биндер нужен коду вне AppCore — мониторам, world-space панелям; его обязательно
    /// освобождают (<see cref="Dispose"/>), иначе подписка на <see cref="Loc.Changed"/> держит дерево.
    /// </para>
    /// <para>Только главный поток.</para>
    /// </remarks>
    public sealed class LocBinder : IDisposable
    {
        // Спящие (оторванные от панели) привязки старше этого — мусор: элемент выбросили, не отвязав.
        private const int SleepPruneFrames = 300;

        private readonly Dictionary<TextElement, LocBinding> _bindings = new();
        private readonly List<LocBinding> _buffer = new();
        private readonly List<LocBinding> _dirty = new();
        private readonly List<LocBinding> _flushing = new();
        private readonly HashSet<ILocalizedElement> _composites = new();
        private readonly List<ILocalizedElement> _compositeBuffer = new();
        private readonly bool _hasContext;

        private IVisualElementScheduledItem _flush;
        private VisualElement _flushHost;
        private bool _subscribed;
        private bool _disposed;
        private bool _suspended;

        /// <summary>Пустышка для выключенной локализации и вью без дерева: ставит текст как есть.</summary>
        public static LocBinder Passthrough { get; } = new(null, null, false);

        /// <summary>Контекст для логов и проверки «владелец уничтожен».</summary>
        public Object Context { get; }

        /// <summary>Доработка строк после перевода. Может быть <c>null</c>.</summary>
        public ILocTextProcessor Processor { get; }

        /// <summary>Сколько элементов на руках — в отладке течь видно отсюда.</summary>
        public int BoundCount => _bindings.Count;

        /// <summary>Приостановлен: изменения данных копятся, текст не пересобирается.</summary>
        public bool Suspended => _suspended;

        /// <summary>
        /// Тексты пересобраны на новом языке. Подписываются те, кто собирает строки в коде
        /// и держит их сам (у контроллеров — <c>OnLanguageChanged</c>).
        /// </summary>
        public event Action LanguageChanged;

        public LocBinder(Object context = null, ILocTextProcessor processor = null) : this(context, processor, true) { }

        private LocBinder(Object context, ILocTextProcessor processor, bool live)
        {
            Context = context;
            Processor = processor;
            _hasContext = !ReferenceEquals(context, null);

            if (!live) _disposed = true;
        }

        private void EnsureSubscribed()
        {
            if (_subscribed || _disposed) return;
            _subscribed = true;
            Loc.Changed += OnChanged;
            Loc.KeysRefreshed += OnKeysRefreshed;
        }

        // ---------- разметка ----------

        /// <summary>
        /// Обходит поддерево и берёт в работу каждый текстовый элемент с ключом (или со строкой,
        /// которую хочет процессор). Идемпотентен: связанные элементы пересобираются, новые
        /// привязываются.
        /// </summary>
        public void BindTree(VisualElement root)
        {
            if (root == null || _disposed) return;

            var enabled = Loc.Enabled;
            if (!enabled && Processor == null) return;

            EnsureSubscribed();
            PruneSleeping();
            Walk(root, enabled);
        }

        // Обход — по hierarchy, а не по contentContainer: надписи внутри компонентов
        // (у которых свой контейнер содержимого) тоже живут в обычных Label.
        private void Walk(VisualElement element, bool enabled)
        {
            if (element is ILocalizedElement composite) _composites.Add(composite);

            // Текст, который набирает пользователь, не шаблон: у поля ввода переводим только подпись.
            if (element is TextField field)
            {
                if (field.labelElement != null) Walk(field.labelElement, enabled);
                return;
            }

            if (element is TextElement text)
            {
                if (_bindings.TryGetValue(text, out var bound)) bound.Refresh();
                else if (NeedsBinding(text.text, enabled)) Bind(text, text.text, null, null, null, false, 0);
            }

            var children = element.hierarchy;
            for (var i = 0; i < children.childCount; i++) Walk(children[i], enabled);
        }

        /// <summary>
        /// Ставит текст элементу, созданному в коде. Запоминается шаблон, а не результат, поэтому
        /// строка переживает смену языка. Повторный вызов с тем же шаблоном ничего не стоит.
        /// </summary>
        public void Set(TextElement element, string template) => SetCore(element, template, null, null, null, false, 0);

        /// <summary>
        /// Ставит текст с пост-обработкой. <paramref name="decorate"/> хранится с шаблоном и
        /// прогоняется на сборке (с кэшем по входу) — иначе смена языка стёрла бы оформление.
        /// </summary>
        public void Set(TextElement element, string template, Func<string, string> decorate) =>
            SetCore(element, template, decorate, null, null, false, 0);

        /// <summary>
        /// Ставит текст с аргументами: <c>Set(label, L.MSG_SEND_PROGRESS, recorded, total)</c>.
        /// Аргументы подставляются до живых значений — фигурные скобки в значениях игры не мешают.
        /// </summary>
        public void Set(TextElement element, string template, params object[] args) =>
            SetCore(element, template, null, args, null, false, 0);

        /// <summary>
        /// Ставит текст с именованным аргументом: <c>Set(label, "^POINTS_LEFT", ("count", left))</c>.
        /// Тот же шаблон и то же значение — ничего не стоит (без упаковки), можно звать каждый кадр.
        /// </summary>
        public void Set<T0>(TextElement element, string template, (string name, T0 value) a0)
        {
            if (Unchanged(element, template, out var existing) && existing.SameSingle(null, a0, false, 0)) return;
            SetCore(element, template, null, null, new (string, object)[] { (a0.name, a0.value) }, false, 0);
        }

        public void Set<T0, T1>(TextElement element, string template, (string name, T0 value) a0, (string name, T1 value) a1)
        {
            if (Unchanged(element, template, out var existing) && existing.SameDouble(a0, a1)) return;
            SetCore(element, template, null, null, new (string, object)[] { (a0.name, a0.value), (a1.name, a1.value) }, false, 0);
        }

        public void Set<T0, T1, T2>(TextElement element, string template, (string name, T0 value) a0, (string name, T1 value) a1, (string name, T2 value) a2)
        {
            if (Unchanged(element, template, out var existing) && existing.SameTriple(a0, a1, a2)) return;
            SetCore(element, template, null, null,
                    new (string, object)[] { (a0.name, a0.value), (a1.name, a1.value), (a2.name, a2.value) }, false, 0);
        }

        /// <summary>Ставит текст с именованными аргументами (четыре и больше).</summary>
        public void Set(TextElement element, string template, params (string name, object value)[] named) =>
            SetCore(element, template, null, null, named, false, 0);

        /// <summary>Ставит текст с окончанием по числу: число — <c>{0}</c>.</summary>
        public void SetPlural(TextElement element, string template, double count) =>
            SetCore(element, template, null, null, null, true, count);

        /// <summary>Ставит текст с окончанием по числу (см. <see cref="Loc.Plural(string,double,object[])"/>).</summary>
        public void SetPlural(TextElement element, string template, double count, params object[] args) =>
            SetCore(element, template, null, args, null, true, count);

        /// <summary>Окончание по числу (<c>{0}</c>) и именованные аргументы.</summary>
        public void SetPlural(TextElement element, string template, double count, params (string name, object value)[] named) =>
            SetCore(element, template, null, null, named, true, count);

        /// <summary>Окончание по числу и один именованный аргумент — без упаковки при том же значении.</summary>
        public void SetPlural<T0>(TextElement element, string template, double count, (string name, T0 value) a0)
        {
            if (Unchanged(element, template, out var existing) && existing.SameSingle(null, a0, true, count)) return;
            SetCore(element, template, null, null, new (string, object)[] { (a0.name, a0.value) }, true, count);
        }

        private bool Unchanged(TextElement element, string template, out LocBinding existing)
        {
            existing = null;
            return element != null && !_disposed && _bindings.TryGetValue(element, out existing) && !existing.Released &&
                   existing.Template == template;
        }

        /// <summary>Ставит строку как есть и снимает привязку: ключ в ней не переводится.</summary>
        public void SetRaw(TextElement element, string text)
        {
            if (element == null) return;
            if (!_disposed) ReleaseElement(element);
            element.text = text;
        }

        private void SetCore(TextElement element, string template, Func<string, string> decorate, object[] args,
                             (string name, object value)[] named, bool plural, double count)
        {
            if (element == null) return;

            if (!_disposed && _bindings.TryGetValue(element, out var existing) && !existing.Released && existing.Template == template)
            {
                if (existing.SameAs(decorate, args, named, plural, count)) return;

                if (existing.SameExceptDecorate(args, named, plural, count))
                {
                    existing.SetDecorate(decorate);
                    existing.Rebuild();
                    return;
                }

                existing.Configure(decorate, args, named, plural, count);
                existing.Refresh();
                return;
            }

            if (!_disposed) ReleaseElement(element);

            var enabled = Loc.Enabled;
            if (_disposed || !NeedsBinding(template, enabled))
            {
                // Держать нечего: ни ключа, ни того, что хочет процессор.
                var text = Loc.ComposeBinding(template, args, named, plural, count, Context);
                element.text = decorate != null ? decorate(text) : text;
                return;
            }

            EnsureSubscribed();
            Bind(element, template, decorate, args, named, plural, count);
        }

        /// <summary>Пост-обработка «подставить аргументы». Устарело: передавайте аргументы в <c>Set</c>.</summary>
        public static Func<string, string> Args(string key, params object[] args) => pattern => Loc.FormatPattern(key, pattern, args);

        /// <summary>Снимает привязки с элемента и поддерева.</summary>
        public void Release(VisualElement element)
        {
            if (element == null || _disposed) return;

            if (element is TextElement text) ReleaseElement(text);
            if (element is ILocalizedElement composite) _composites.Remove(composite);

            var children = element.hierarchy;
            for (var i = 0; i < children.childCount; i++) Release(children[i]);
        }

        /// <summary>Снимает все привязки. Подписка на смену языка остаётся.</summary>
        public void Clear()
        {
            foreach (var binding in _bindings.Values) binding.Release();
            _bindings.Clear();
            _composites.Clear();
            _dirty.Clear();
        }

        /// <summary>
        /// Приостановить пересборку по данным (монитор скрыт или вдали). Изменения копятся и
        /// применяются одной сборкой на <see cref="Resume"/>. Смена языка обрабатывается всегда.
        /// </summary>
        public void Suspend() => _suspended = true;

        public void Resume()
        {
            if (!_suspended) return;
            _suspended = false;
            if (_dirty.Count > 0) ScheduleFlush(null);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_subscribed)
            {
                Loc.Changed -= OnChanged;
                Loc.KeysRefreshed -= OnKeysRefreshed;
            }

            _subscribed = false;

            _flush?.Pause();
            _flush = null;
            _flushHost = null;

            Clear();
            LanguageChanged = null;
        }

        // ---------- внутреннее ----------

        private bool NeedsBinding(string template, bool enabled)
        {
            if (string.IsNullOrEmpty(template)) return false;
            if (enabled && Loc.IsKey(template)) return true;
            return Processor != null && Processor.Wants(template);
        }

        private void Bind(TextElement element, string template, Func<string, string> decorate, object[] args,
                          (string name, object value)[] named, bool plural, double count)
        {
            var binding = new LocBinding(this, element, template);
            binding.Configure(decorate, args, named, plural, count);
            _bindings.Add(element, binding);
            binding.Attach();
            binding.Refresh();

            // Привязали элемент, ещё не стоящий на панели: текст поставлен, а подписки ждут прицепления.
            if (element.panel == null) binding.Sleep();
        }

        internal void ReleaseElement(TextElement element)
        {
            if (!_bindings.TryGetValue(element, out var binding)) return;

            binding.Release();
            _bindings.Remove(element);
        }

        internal void EnqueueDirty(LocBinding binding)
        {
            _dirty.Add(binding);
            if (!_suspended) ScheduleFlush(binding.Element);
        }

        /// <summary>
        /// Одна задача планировщика на биндер. Хост — любой живой элемент биндера; оторвался —
        /// берём другой.
        /// </summary>
        private void ScheduleFlush(VisualElement candidate)
        {
            if (_flush != null && _flushHost != null && _flushHost.panel != null)
            {
                if (!_flush.isActive) _flush.ExecuteLater(0);
                return;
            }

            var host = candidate != null && candidate.panel != null ? candidate : FindLiveHost();
            if (host == null) return;   // всё оторвано — соберутся при пробуждении

            _flush?.Pause();
            _flushHost = host;
            _flush = host.schedule.Execute(Flush);
        }

        private VisualElement FindLiveHost()
        {
            for (var i = 0; i < _dirty.Count; i++)
                if (_dirty[i].Element.panel != null) return _dirty[i].Element;
            return null;
        }

        private void Flush()
        {
            if (_disposed || _suspended) return;

            // Снимок: сборка может поставить элемент в очередь снова (значение поменялось при чтении).
            _flushing.Clear();
            _flushing.AddRange(_dirty);
            _dirty.Clear();

            for (var i = 0; i < _flushing.Count; i++) _flushing[i].Rebuild();
            _flushing.Clear();
        }

        private void PruneSleeping()
        {
            if (_bindings.Count == 0) return;

            var frame = Time.frameCount;
            _buffer.Clear();
            foreach (var binding in _bindings.Values)
                if (binding.Sleeping && binding.Element.panel == null && frame - binding.SleptAtFrame > SleepPruneFrames)
                    _buffer.Add(binding);

            for (var i = 0; i < _buffer.Count; i++) ReleaseElement(_buffer[i].Element);
            _buffer.Clear();
        }

        /// <summary>Проход пересчитал ключи (<see cref="Loc.RefreshPass(string,string)"/>): пересобрать только их.</summary>
        private void OnKeysRefreshed(IReadOnlyCollection<string> keys)
        {
            if (_disposed || _bindings.Count == 0) return;

            if (_hasContext && Context == null)
            {
                Dispose();
                return;
            }

            var set = keys as ICollection<string>;
            _buffer.Clear();
            foreach (var binding in _bindings.Values)
            {
                if (binding.Sleeping || binding.Key == null) continue;
                if (set != null ? set.Contains(binding.Key) : Contains(keys, binding.Key)) _buffer.Add(binding);
            }

            for (var i = 0; i < _buffer.Count; i++) _buffer[i].Refresh();
            _buffer.Clear();
        }

        private static bool Contains(IReadOnlyCollection<string> keys, string key)
        {
            foreach (var k in keys)
                if (string.Equals(k, key, StringComparison.Ordinal)) return true;
            return false;
        }

        private void OnChanged()
        {
            if (_disposed) return;

            // Владелец уничтожен, а биндер не освободили — освобождаемся сами.
            if (_hasContext && Context == null)
            {
                Dispose();
                return;
            }

            PruneSleeping();

            // Снимок: пересборка может отвязать элементы (компонент перестроил надписи).
            _buffer.Clear();
            _buffer.AddRange(_bindings.Values);
            for (var i = 0; i < _buffer.Count; i++)
            {
                var binding = _buffer[i];
                if (binding.Sleeping) continue;   // проснётся полной сборкой при прицеплении
                binding.Refresh();
            }

            _buffer.Clear();

            _compositeBuffer.Clear();
            _compositeBuffer.AddRange(_composites);
            for (var i = 0; i < _compositeBuffer.Count; i++)
            {
                var composite = _compositeBuffer[i];
                if (composite is VisualElement ve && ve.panel == null)
                {
                    _composites.Remove(composite);
                    continue;
                }

                try { composite.Relocalize(); }
                catch (Exception e) { Debug.LogException(e, Context); }
            }

            _compositeBuffer.Clear();

            var handler = LanguageChanged;
            if (handler == null) return;

            foreach (var listener in handler.GetInvocationList())
            {
                try { ((Action)listener)(); }
                catch (Exception e) { Debug.LogException(e, Context); }
            }
        }
    }
}
