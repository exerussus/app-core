using System;
using System.Globalization;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    /// <summary>
    /// Аналитика приложения: профиль игрока, сессия запуска, прогоны (партии) и события по ключам каталога
    /// (<see cref="AnalyticsSettings"/>).
    /// </summary>
    /// <remarks>
    /// <para>Вызов: <c>AppEvents.Level.Enemy.Killed.Raise(enemyId)</c> — сгенерированный типизированный метод,
    /// или <c>Begin(IdOf("Level.Enemy.Killed")).Field(...).End()</c> для ключа, взятого по строке.</para>
    /// <para>События пишутся в открытый пакет — JSONL в буфере фиксированного размера, выделенном один раз при
    /// старте: запись события не аллоцирует и не растит память. Пакет закрывается по числу событий, размеру,
    /// возрасту, на границе профиля/сессии/прогона и на <see cref="Flush"/>; закрытый — в очередь
    /// (на диск, если есть <see cref="IAnalyticsStore"/>), оттуда адаптеру (<see cref="IAnalyticsAdapter"/>),
    /// удаляется после <see cref="Complete"/>.</para>
    /// <para>Лёгкий режим (по умолчанию в WebGL): без диска, без экспорта в файлы, маленькая очередь в памяти;
    /// пока проект не назначил адаптер — события не пишутся вовсе.</para>
    /// <para>Ошибка внутри модуля не уходит в игру: модуль пишет её в лог и выключается до конца запуска.</para>
    /// <para>Только главный поток. Выключено (модуль или сбор игроком) — <see cref="Begin"/> выходит сразу.</para>
    /// </remarks>
    public static partial class AppAnalytics
    {
        public const string ProfilePrefix = "player_";
        private const string PrefsCollect = "AppAnalytics.Collect";
        private const string PrefsProfile = "AppAnalytics.Profile";

        /// <summary>Запас буфера сверх размера пакета: заголовок и одно событие, начатое у самой границы.</summary>
        private const int ReserveChars = 8192;

        // ---------------------------------------------------------------- состояние

        private static AnalyticsSettings _settings;
        private static bool _booted;
        private static bool _collecting;
        private static bool _lean;
        private static bool _faulted;
        private static bool _flushing;

        private static string[] _keys = Array.Empty<string>();
        private static bool[] _eventOn = Array.Empty<bool>();
        private static string _schema = string.Empty;

        private static string _profileId = string.Empty;
        private static string _sessionId = string.Empty;
        private static string _sessionStartUtc = string.Empty;
        private static double _sessionStart;
        private static string _runId;
        private static double _runStart;
        private static Func<double> _gameClock;

        private static long _seq;
        private static int _packetIndex;

        // открытый пакет: фиксированный буфер, не растёт
        private static char[] _chars = Array.Empty<char>();
        private static int _len;
        private static int _softLimit;
        private static int _eventStart;
        private static bool _overflow;

        private static string _bufferPacketId;
        private static int _bufferEvents;
        private static double _bufferOpenedAt;
        private static bool _inEvent;
        private static int _dropped;

        // ---------------------------------------------------------------- публичное API

        /// <summary>Модуль поднят (ассет настроек есть и включён).</summary>
        public static bool IsAvailable => _booted;

        /// <summary>События сейчас пишутся (модуль поднят, игрок не выключил сбор, есть куда доставлять).</summary>
        public static bool IsCollecting => CanWrite;

        /// <summary>Лёгкий режим: без диска и экспорта, маленькая очередь в памяти (см. настройки).</summary>
        public static bool IsLean => _lean;

        /// <summary>Id профиля игрока: <c>player_&lt;guid&gt;</c>. Живёт между запусками, меняется <see cref="NewProfile"/>.</summary>
        public static string ProfileId => _profileId;

        /// <summary>Id запуска приложения: новый при каждом старте.</summary>
        public static string SessionId => _sessionId;

        /// <summary>Id текущего прогона (партии) или <c>null</c>.</summary>
        public static string RunId => ReferenceEquals(_runId, SilentRun) ? null : _runId;

        /// <summary>Прогон, начатый, когда события не пишутся: без id и без своих пакетов.</summary>
        private static readonly string SilentRun = new string('-', 1);

        /// <summary>Прогон с id (пишется): события — по его часам и в его пакетах.</summary>
        private static bool InRun => _runId != null && !ReferenceEquals(_runId, SilentRun);

        /// <summary>Выбор игрока: сбор включён (хранится в PlayerPrefs). Пишется ли сейчас — <see cref="IsCollecting"/>.</summary>
        public static bool CollectEnabled => _pendingCollect ?? _collecting;

        /// <summary>Сменился профиль.</summary>
        public static event Action ProfileChanged;

        /// <summary>Сбор включён/выключен.</summary>
        public static event Action CollectingChanged;

        /// <summary>
        /// Включить или выключить сбор (выбор игрока, хранится в PlayerPrefs). Тот же выбор — ничего не делает
        /// (можно звать каждый кадр). До старта модуля выбор запоминается и записывается при старте; выключенный
        /// модуль PlayerPrefs не трогает. Исключений в вызывающий код не выпускает.
        /// </summary>
        public static void SetCollecting(bool on)
        {
            if (_faulted) return;
            if (!_booted)
            {
                _pendingCollect = on;
                return;
            }

            if (_collecting == on) return;

            try
            {
                if (!on)
                {
                    WriteSystem("app.analytics.off");
                    Seal();
                    if (!_booted) return; // отказ посреди вызова — дальше ничего
                    _collecting = false;
                    var settings = _settings;
                    if (settings != null && settings.DiscardQueueOnDisable) ClearQueue();
                }
                else
                {
                    _collecting = true;
                    WriteSystem("app.analytics.on");
                }

                if (!_booted) return;
                PrefsSetInt(PrefsCollect, on ? 1 : 0);
                UpdatePump();
            }
            catch (Exception e)
            {
                Fault(e);
                return;
            }

            Notify(CollectingChanged);
        }

        /// <summary>
        /// Новый профиль (<c>player_&lt;guid&gt;</c>): текущий пакет закрывается, дальше события идут под новым id.
        /// Модуль не запущен — ничего не делает и возвращает текущий id (или пустую строку).
        /// </summary>
        public static string NewProfile()
        {
            if (!_booted) return _profileId ?? string.Empty;
            ChangeProfile(ProfilePrefix + Guid.NewGuid().ToString("N"));
            return _profileId;
        }

        /// <summary>Максимальная длина id профиля, заданного проектом.</summary>
        public const int MaxProfileId = 64;

        /// <summary>
        /// Свой id профиля — например, id пользователя из авторизации: не теряется вместе с PlayerPrefs (IndexedDB
        /// в WebView), один на устройствах игрока. Тот же id — ничего не делает. До старта модуля — применяется при
        /// старте. До 64 печатных символов без кавычек и «\»; неверный id отклоняется (предупреждение в лог).
        /// </summary>
        public static void SetProfileId(string id)
        {
            if (_faulted) return;
            if (!ValidProfileId(id))
            {
                Debug.LogWarning($"[AppAnalytics] Id профиля отклонён: 1–{MaxProfileId} печатных символов без кавычек и «\\».");
                return;
            }

            if (!_booted)
            {
                _pendingProfile = id;
                return;
            }

            if (string.Equals(id, _profileId, StringComparison.Ordinal)) return;
            ChangeProfile(id);
        }

        private static void ChangeProfile(string id)
        {
            var previous = _profileId;
            try
            {
                Seal();
                _profileId = id;
                PrefsSetString(PrefsProfile, id);

                if (BeginSystem("app.profile.new"))
                {
                    WriteField("previous", previous);
                    EndEvent();
                }

                Flush(AnalyticsFlushReason.ProfileChanged);
            }
            catch (Exception e)
            {
                Fault(e);
                return;
            }

            Notify(ProfileChanged);
        }

        private static bool ValidProfileId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxProfileId) return false;
            foreach (var c in id)
                if (c < 0x20 || c == '"' || c == '\\' || c == 0x7f) return false;
            return true;
        }

        /// <summary>Часы игрового времени (стоят на паузе). Пишутся в события полем <c>g</c>. <c>null</c> — не писать.</summary>
        public static void SetGameClock(Func<double> clock) => _gameClock = clock;

        /// <summary>
        /// Начать прогон (партию). Закрывает текущий пакет: пакет не смешивает прогоны.
        /// <paramref name="tag"/> — что запускается (например, id уровня).
        /// </summary>
        public static void BeginRun(string tag = null)
        {
            if (!_booted) return;
            if (_runId != null) EndRun("interrupted");

            if (!CanWrite)
            {
                // не пишется (сбор выключен, лёгкий режим без адаптера): ни id, ни пакета — прогон только отмечен
                _runId = SilentRun;
                _runStart = Now();
                return;
            }

            Seal();
            _runId = Guid.NewGuid().ToString("N");
            _runStart = Now();

            if (!BeginSystem("app.run.begin")) return;
            WriteField("tag", tag);
            EndEvent();
        }

        /// <summary>Закончить прогон: событие итога, пакет закрывается и уходит адаптеру.</summary>
        public static void EndRun(string result = null)
        {
            if (!_booted || _runId == null) return;
            if (ReferenceEquals(_runId, SilentRun))
            {
                _runId = null;
                return;
            }

            if (BeginSystem("app.run.end"))
            {
                WriteField("result", result);
                WriteField("duration", Now() - _runStart);
                EndEvent();
            }

            _runId = null;
            Flush(AnalyticsFlushReason.RunEnd);
        }

        // ---------------------------------------------------------------- свойства

        /// <summary>Сколько свойств может быть одновременно.</summary>
        public const int MaxProperties = 16;
        public const int MaxPropertyName = 32;
        public const int MaxPropertyValue = 128;
        /// <summary>Все свойства в заголовке вместе (имена и значения): заголовок не должен съедать пакет.</summary>
        public const int MaxPropertiesChars = 1024;

        private static string[] _propNames = Array.Empty<string>();
        private static string[] _propKeys = Array.Empty<string>();
        private static string[] _propValues = Array.Empty<string>();
        private static int _propCount;
        private static bool _propWarned;

        /// <summary>
        /// Свойство игрока или сессии для сегментов отчёта (<c>@prop.имя</c>): A/B-группа, версия прихода, режим.
        /// Пишется в заголовок каждого следующего пакета (поле <c>p.имя</c>) и событием <c>app.property</c>.
        /// Повтор того же значения ничего не делает и не аллоцирует; новое значение пишется событием и попадает
        /// в заголовки следующих пакетов (текущий пакет не закрывается). Можно звать до старта модуля.
        /// <c>null</c> — убрать свойство. До 16 свойств, имя до 32 символов (латиница, цифры, «_», «-», «.»),
        /// значение до 128 печатных символов; все свойства вместе — не больше <see cref="MaxPropertiesChars"/>.
        /// Передавайте закэшированные строки: модуль не копирует и не форматирует значение.
        /// </summary>
        public static void SetProperty(string name, string value)
        {
            if (_faulted) return;

            try
            {
                SetPropertyCore(name, value);
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>Логическое свойство: «true» / «false» (строки-константы, без аллокаций).</summary>
        public static void SetProperty(string name, bool value) => SetProperty(name, value ? "true" : "false");

        /// <summary>Текущее значение свойства или <c>null</c>.</summary>
        public static string GetProperty(string name)
        {
            var i = PropIndex(name);
            return i >= 0 ? _propValues[i] : null;
        }

        private static void SetPropertyCore(string name, string value)
        {
            if (!ValidPropName(name))
            {
                WarnProp($"имя свойства «{name}»: 1–{MaxPropertyName} символов, латиница, цифры, «_», «-», «.»");
                return;
            }

            if (value != null && !ValidPropValue(value))
            {
                WarnProp($"значение свойства «{name}»: до {MaxPropertyValue} символов без управляющих — не записано");
                return;
            }

            var i = PropIndex(name);
            if (i >= 0 && string.Equals(_propValues[i], value, StringComparison.Ordinal)) return;

            if (value != null)
            {
                // бюджет заголовка: все свойства вместе, с кавычками и запятыми
                var total = 0;
                for (var k = 0; k < _propCount; k++)
                    if (k != i) total += _propKeys[k].Length + _propValues[k].Length + 6;
                total += name.Length + 2 + value.Length + 6;
                if (total > MaxPropertiesChars)
                {
                    WarnProp($"свойства вместе длиннее {MaxPropertiesChars} символов — «{name}» не записано");
                    return;
                }
            }
            if (i < 0)
            {
                if (value == null) return;
                if (_propCount >= MaxProperties)
                {
                    WarnProp($"свойств больше {MaxProperties} — «{name}» не записано");
                    return;
                }

                if (_propNames.Length == 0)
                {
                    // один раз за запуск и только если проект пользуется свойствами
                    _propNames = new string[MaxProperties];
                    _propKeys = new string[MaxProperties];
                    _propValues = new string[MaxProperties];
                }

                i = _propCount++;
                _propNames[i] = name;
                _propKeys[i] = "p." + name;
            }

            if (value == null)
            {
                // убрать: сдвинуть хвост
                for (var k = i; k < _propCount - 1; k++)
                {
                    _propNames[k] = _propNames[k + 1];
                    _propKeys[k] = _propKeys[k + 1];
                    _propValues[k] = _propValues[k + 1];
                }

                _propCount--;
                _propNames[_propCount] = null;
                _propKeys[_propCount] = null;
                _propValues[_propCount] = null;
            }
            else
            {
                _propValues[i] = value;
            }

            // событие несёт изменение внутри текущего пакета; заголовки следующих пакетов — уже с новым значением
            if (BeginSystem("app.property"))
            {
                WriteField("name", name);
                WriteField("value", value);
                EndEvent();
            }
        }

        private static int PropIndex(string name)
        {
            if (name == null) return -1;
            for (var i = 0; i < _propCount; i++)
                if (string.Equals(_propNames[i], name, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static bool ValidPropValue(string value)
        {
            if (value.Length > MaxPropertyValue) return false;
            foreach (var c in value)
                if (c < 0x20 || c == '"' || c == '\\') return false;
            return true;
        }

        private static bool ValidPropName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxPropertyName) return false;
            foreach (var c in name)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }

        private static void WarnProp(string text)
        {
            if (_propWarned) return;
            _propWarned = true;
            Debug.LogWarning("[AppAnalytics] " + text + " (дальше такие предупреждения не выводятся).");
        }

        private static void ClearProperties()
        {
            _propNames = Array.Empty<string>();
            _propKeys = Array.Empty<string>();
            _propValues = Array.Empty<string>();
            _propCount = 0;
            _propWarned = false;
        }

        /// <summary>Номер события по ключу (для ручек по строке), -1 — нет такого.</summary>
        public static int IdOf(string key)
        {
            for (int i = 0; i < _keys.Length; i++)
                if (string.Equals(_keys[i], key, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>
        /// Начать событие по номеру (<c>AppEvents.….Id</c>). Неактивный писатель — сбор выключен,
        /// событие выключено, номера нет или (лёгкий режим) некуда доставлять.
        /// </summary>
        public static AnalyticsEventWriter Begin(int id)
        {
            if (!CanWrite || _starting || (uint)id >= (uint)_keys.Length || !_eventOn[id]) return default;

            try
            {
                StartEvent(_keys[id]);
            }
            catch (Exception e)
            {
                Fault(e);
                return default;
            }

            return _inEvent ? new AnalyticsEventWriter(true) : default;
        }

        /// <summary>Закрыть пакет сейчас и дать адаптеру знать причину.</summary>
        public static void Flush(AnalyticsFlushReason reason = AnalyticsFlushReason.Manual)
        {
            // адаптер позвал Flush из OnFlush/Deliver — не уходить в рекурсию
            if (!_booted || _flushing) return;

            _flushing = true;
            try
            {
                Seal();
                if (!_booted) return;
                // нечего доставлять и не пишется — адаптер не трогаем (игрок отказался от сбора)
                if (_queue.Count == 0 && !CanWrite) return;

                // сначала пакеты адаптеру (на скрытии и выходе — мимо пауз и лимита в полёте), потом OnFlush:
                // адаптер, который на Pause делает sendBeacon, уже держит только что закрытый пакет
                var urgent = reason == AnalyticsFlushReason.Pause || reason == AnalyticsFlushReason.Quit;
                Pump(force: true, urgent: urgent);
                if (!_booted) return;

                var adapter = Adapter();
                if (adapter != null)
                {
                    try
                    {
                        adapter.OnFlush(reason);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                UpdatePump();
            }
            finally
            {
                _flushing = false;
            }
        }

        // ---------------------------------------------------------------- отказ

        /// <summary>
        /// Ошибка внутри модуля: в лог и выключить модуль до конца запуска — аналитика не должна ронять игру
        /// и не должна сыпать ошибками каждый кадр.
        /// </summary>
        private static void Fault(Exception e)
        {
            if (_faulted) return;
            _faulted = true;

            Debug.LogError("[AppAnalytics] Внутренняя ошибка — аналитика выключена до конца запуска.");
            Debug.LogException(e);

            // отказ мог прийти из кадра насоса или колбэка: объект и подписки не трогаются (fromFault)
            Shutdown(seal: false, fromFault: true);
        }

        private static void Notify(Action handler)
        {
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // ---------------------------------------------------------------- запись событий

        private static double Now() => Time.realtimeSinceStartupAsDouble;

        /// <summary>Писать можно: модуль поднят, игрок разрешил, а в лёгком режиме — назначен адаптер.</summary>
        private static bool CanWrite => _booted && _collecting && (!_lean || _adapter != null);

        /// <summary>Системное событие ядра (<c>app.*</c>) — пишется, если можно писать, в обход каталога.</summary>
        private static bool BeginSystem(string key)
        {
            if (!CanWrite || _starting) return false;

            try
            {
                StartEvent(key);
            }
            catch (Exception e)
            {
                Fault(e);
                return false;
            }

            return _inEvent;
        }

        private static void WriteSystem(string key)
        {
            if (BeginSystem(key)) EndEvent();
        }

        private static bool _starting;

        private static void StartEvent(string key)
        {
            // Raise из колбэка игровых часов (он вызывается внутри StartEvent) — не вкладывать событие в событие
            if (_starting) return;

            // игровые часы — код проекта: зовутся первыми, до записи чего-либо, — чтобы их вызовы в модуль
            // (Flush, смена профиля) не запечатали полузаписанное событие
            var hasGame = false;
            var game = double.NaN;
            var clock = _gameClock;
            if (clock != null)
            {
                hasGame = true;
                _starting = true;
                try
                {
                    game = clock();
                }
                catch
                {
                    game = double.NaN;
                }
                finally
                {
                    _starting = false;
                }

                if (!_booted) return; // часы выключили модуль
            }

            if (_inEvent) EndEvent();
            if (!_booted) return; // закрытие события могло запечатать пакет и выключить модуль
            var settings = _settings;
            if (settings == null) return;
            if (_chars.Length == 0) AllocateBuffer(settings); // лениво: модуль, который не пишет, не держит буфер
            if (_len >= _softLimit) Seal();
            if (!_booted) return; // Seal мог выключить модуль ошибкой
            EnsureHeader();

            _inEvent = true;
            _overflow = false;
            _eventStart = _len;
            _seq++;

            var now = Now();
            var origin = InRun ? _runStart : _sessionStart;

            Put("{\"s\":");
            PutLong(_seq);
            Put(",\"t\":");
            PutNumber(now - origin);

            if (hasGame)
            {
                Put(",\"g\":");
                PutNumber(game);
            }

            Put(",\"e\":");
            PutString(key);
        }

        internal static void WriteField(string name, long value)
        {
            if (!_inEvent) return;
            PutName(name);
            PutLong(value);
        }

        internal static void WriteField(string name, double value)
        {
            if (!_inEvent) return;
            PutName(name);
            PutNumber(value);
        }

        internal static void WriteField(string name, bool value)
        {
            if (!_inEvent) return;
            PutName(name);
            Put(value ? "true" : "false");
        }

        internal static void WriteField(string name, string value)
        {
            if (!_inEvent) return;
            PutName(name);
            PutString(value);
        }

        internal static void EndEvent()
        {
            if (!_inEvent) return;

            try
            {
                EndEventCore();
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        private static void EndEventCore()
        {
            _inEvent = false;

            Put("}\n");
            if (_overflow)
            {
                // событие не влезло (огромное строковое поле) — откатить целиком, учесть как потерю
                _len = _eventStart;
                _overflow = false;
                _dropped++;
                return;
            }

            _bufferEvents++;
            var settings = _settings;
            if (settings != null && _bufferEvents >= settings.MaxEventsPerPacket) Seal();
        }

        /// <summary>Первая строка открытого пакета — заголовок.</summary>
        private static void EnsureHeader()
        {
            if (_bufferPacketId != null) return;

            _packetIndex++;
            _bufferPacketId = _sessionId + "-" + _packetIndex.ToString("D5", CultureInfo.InvariantCulture);
            _bufferOpenedAt = Now();
            _bufferEvents = 0;
            _len = 0;
            _overflow = false;

            Put("{\"type\":\"header\",\"packet\":");
            PutString(_bufferPacketId);
            Put(",\"schema\":");
            PutString(_schema);
            Put(",\"profile\":");
            PutString(_profileId);
            Put(",\"session\":");
            PutString(_sessionId);
            Put(",\"run\":");
            PutString(InRun ? _runId : null);
            Put(",\"clock\":");
            PutString(InRun ? "run" : "session");
            Put(",\"sessionStartUtc\":");
            PutString(_sessionStartUtc);
            RefreshBuildVersion();
            Put(",\"build\":");
            PutString(_buildVersion);
            Put(",\"platform\":");
            PutString(_platform);
            Put(",\"unity\":");
            PutString(_unityVersion);
            // свойства — только если заголовок с ними помещается в половину пакета (бюджет SetProperty это гарантирует
            // при размере пакета от 4 КБ; проверка — на случай маленького пакета)
            var beforeProps = _len;
            for (var p = 0; p < _propCount; p++)
            {
                Put(',');
                PutString(_propKeys[p]);
                Put(':');
                PutString(_propValues[p]);
            }

            if (_overflow || _len > _softLimit / 2)
            {
                _len = beforeProps;
                _overflow = false;
            }
            if (InRun)
            {
                Put(",\"runStart\":");
                PutNumber(_runStart - _sessionStart);
            }
            Put(",\"seq0\":");
            PutLong(_seq + 1);
            Put("}\n");

            if (_dropped > 0)
            {
                // Видимая дыра: сколько событий потеряно (переполнение очереди или события).
                var lineStart = _len;
                _overflow = false;
                Put("{\"s\":");
                PutLong(_seq + 1);
                Put(",\"t\":0,\"e\":\"app.analytics.dropped\",\"count\":");
                PutLong(_dropped);
                Put("}\n");
                if (_overflow) _len = lineStart; // не влезло — счётчик остаётся до следующего заголовка
                else
                {
                    _seq++;
                    _dropped = 0;
                    _bufferEvents++;
                }
            }

            _overflow = false;
        }

        // заголовочные строки — один раз при старте, не на каждый пакет
        private static string _buildVersion = string.Empty;
        private static string _platform = string.Empty;
        private static string _unityVersion = string.Empty;

        private static bool _buildFinal;

        private static void CacheHeaderStrings()
        {
            _buildVersion = Application.version ?? string.Empty;
            _buildFinal = false;
            RefreshBuildVersion();
            _platform = Application.platform.ToString();
            _unityVersion = Application.unityVersion ?? string.Empty;
        }

        /// <summary>
        /// Версия сборки с хешем — из BuildInfo, который AppRunner ставит в Awake (позже старта модуля). До этого —
        /// Application.version; первый заголовок после — уже полная версия. Читается один раз.
        /// </summary>
        private static void RefreshBuildVersion()
        {
            if (_buildFinal || Exerussus.AppCore.Build.BuildInfo.Current == null) return;
            var v = Exerussus.AppCore.Build.BuildInfo.CurrentFullVersion;
            if (!string.IsNullOrEmpty(v)) _buildVersion = v;
            _buildFinal = true;
        }

        // ---------------------------------------------------------------- буфер без аллокаций

        /// <summary>Выделить буфер пакета один раз: размер пакета из настроек + запас.</summary>
        private static void AllocateBuffer(AnalyticsSettings settings)
        {
            if (settings == null) return;
            _softLimit = settings.MaxPacketChars;
            var size = _softLimit + ReserveChars;
            if (_chars.Length != size) _chars = new char[size];
            _len = 0;
        }

        private static void ReleaseBuffer()
        {
            _chars = Array.Empty<char>();
            _len = 0;
            _softLimit = 0;
        }

        private static void Put(char c)
        {
            if (_len < _chars.Length) _chars[_len++] = c;
            else _overflow = true;
        }

        private static void Put(string s)
        {
            if (s == null) return;
            if (_len + s.Length > _chars.Length)
            {
                _overflow = true;
                return;
            }

            s.CopyTo(0, _chars, _len, s.Length);
            _len += s.Length;
        }

        private static void PutName(string name)
        {
            Put(',');
            PutString(name ?? string.Empty);
            Put(':');
        }

        /// <summary>Целое без ToString (он аллоцирует строку).</summary>
        private static void PutLong(long value)
        {
            if (value == long.MinValue)
            {
                Put("-9223372036854775808");
                return;
            }

            if (value < 0)
            {
                Put('-');
                value = -value;
            }

            var digits = 1;
            for (var t = value; t >= 10; t /= 10) digits++;

            if (_len + digits > _chars.Length)
            {
                _overflow = true;
                return;
            }

            var end = _len + digits;
            for (var i = end - 1; i >= _len; i--)
            {
                _chars[i] = (char)('0' + (int)(value % 10));
                value /= 10;
            }

            _len = end;
        }

        /// <summary>Дробное с точностью до тысячных (мс для секунд), без ToString. NaN/∞ и |x| ≥ 9e12 — <c>null</c>.</summary>
        private static void PutNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                Put("null");
                return;
            }

            // за пределами — «null»: (long) от слишком большого double не определён (в WASM может упасть)
            if (!(Math.Abs(value) < 9e12))
            {
                Put("null");
                return;
            }

            var scaled = (long)Math.Round(value * 1000.0, MidpointRounding.AwayFromZero);
            if (scaled < 0)
            {
                Put('-');
                scaled = -scaled;
            }

            PutLong(scaled / 1000);

            var frac = (int)(scaled % 1000);
            if (frac == 0) return;

            Put('.');
            Put((char)('0' + frac / 100));
            frac %= 100;
            if (frac == 0) return;
            Put((char)('0' + frac / 10));
            frac %= 10;
            if (frac == 0) return;
            Put((char)('0' + frac));
        }

        private static void PutString(string value)
        {
            if (value == null)
            {
                Put("null");
                return;
            }

            Put('"');
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"': Put('\\'); Put('"'); break;
                    case '\\': Put('\\'); Put('\\'); break;
                    case '\n': Put('\\'); Put('n'); break;
                    case '\r': Put('\\'); Put('r'); break;
                    case '\t': Put('\\'); Put('t'); break;
                    default:
                        if (c < 0x20)
                        {
                            Put('\\');
                            Put('u');
                            Put('0');
                            Put('0');
                            Put(Hex(c >> 4));
                            Put(Hex(c & 0xF));
                        }
                        else
                        {
                            Put(c);
                        }
                        break;
                }

                if (_overflow) return;
            }
            Put('"');
        }

        private static char Hex(int v) => (char)(v < 10 ? '0' + v : 'a' + v - 10);
    }
}
