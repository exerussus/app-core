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
    /// <para>Вызов: <c>AppEvents.Game.Module.Focus.Raise(moduleId)</c> — сгенерированный типизированный метод,
    /// или <c>Begin(IdOf("Game.Module.Focus")).Field(...).End()</c> для ключа, взятого по строке.</para>
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
        public static string RunId => _runId;

        /// <summary>Выбор игрока: сбор включён (хранится в PlayerPrefs). Пишется ли сейчас — <see cref="IsCollecting"/>.</summary>
        public static bool CollectEnabled => _collecting;

        /// <summary>Сменился профиль.</summary>
        public static event Action ProfileChanged;

        /// <summary>Сбор включён/выключен.</summary>
        public static event Action CollectingChanged;

        /// <summary>Включить или выключить сбор (выбор игрока, хранится в PlayerPrefs).</summary>
        public static void SetCollecting(bool on)
        {
            PlayerPrefs.SetInt(PrefsCollect, on ? 1 : 0);
            PlayerPrefs.Save();

            if (!_booted)
            {
                _collecting = on;
                return;
            }

            if (_collecting == on) return;

            if (!on)
            {
                WriteSystem("app.analytics.off");
                Seal();
                _collecting = false;
                if (_settings != null && _settings.DiscardQueueOnDisable) ClearQueue();
            }
            else
            {
                _collecting = true;
                WriteSystem("app.analytics.on");
            }

            Notify(CollectingChanged);
        }

        /// <summary>Новый профиль: текущий пакет закрывается, дальше события идут под новым id.</summary>
        public static string NewProfile()
        {
            var previous = _profileId;
            if (_booted) Seal();

            _profileId = ProfilePrefix + Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(PrefsProfile, _profileId);
            PlayerPrefs.Save();

            if (_booted)
            {
                if (BeginSystem("app.profile.new"))
                {
                    WriteField("previous", previous);
                    EndEvent();
                }

                Flush(AnalyticsFlushReason.ProfileChanged);
            }

            Notify(ProfileChanged);
            return _profileId;
        }

        /// <summary>Часы игрового времени (стоят на паузе). Пишутся в события полем <c>g</c>. <c>null</c> — не писать.</summary>
        public static void SetGameClock(Func<double> clock) => _gameClock = clock;

        /// <summary>
        /// Начать прогон (партию). Закрывает текущий пакет: пакет не смешивает прогоны.
        /// <paramref name="tag"/> — что запускается (например, id сценария).
        /// </summary>
        public static void BeginRun(string tag = null)
        {
            if (!_booted) return;
            if (_runId != null) EndRun("interrupted");

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

            if (BeginSystem("app.run.end"))
            {
                WriteField("result", result);
                WriteField("duration", Now() - _runStart);
                EndEvent();
            }

            _runId = null;
            Flush(AnalyticsFlushReason.RunEnd);
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
            if (!CanWrite || (uint)id >= (uint)_keys.Length || !_eventOn[id]) return default;

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

                Pump(force: true);
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

            try
            {
                Shutdown(seal: false);
            }
            catch
            {
                _booted = false;
            }
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
            if (!CanWrite) return false;

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

        private static void StartEvent(string key)
        {
            if (_inEvent) EndEvent();
            if (_chars.Length == 0) AllocateBuffer(_settings); // лениво: модуль, который не пишет, не держит буфер
            if (_len >= _softLimit) Seal();
            if (!_booted) return; // Seal мог выключить модуль ошибкой
            EnsureHeader();

            _inEvent = true;
            _overflow = false;
            _eventStart = _len;
            _seq++;

            var now = Now();
            var origin = _runId != null ? _runStart : _sessionStart;

            Put("{\"s\":");
            PutLong(_seq);
            Put(",\"t\":");
            PutNumber(now - origin);

            var clock = _gameClock;
            if (clock != null)
            {
                double game;
                try
                {
                    game = clock();
                }
                catch
                {
                    game = double.NaN;
                }

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
            if (_bufferEvents >= _settings.MaxEventsPerPacket) Seal();
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
            PutString(_runId);
            Put(",\"clock\":");
            PutString(_runId != null ? "run" : "session");
            Put(",\"sessionStartUtc\":");
            PutString(_sessionStartUtc);
            Put(",\"build\":");
            PutString(_buildVersion);
            Put(",\"platform\":");
            PutString(_platform);
            Put(",\"unity\":");
            PutString(_unityVersion);
            if (_runId != null)
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
                var dropped = _dropped;
                _dropped = 0;
                _seq++;
                Put("{\"s\":");
                PutLong(_seq);
                Put(",\"t\":0,\"e\":\"app.analytics.dropped\",\"count\":");
                PutLong(dropped);
                Put("}\n");
                _bufferEvents++;
            }

            _overflow = false;
        }

        // заголовочные строки — один раз при старте, не на каждый пакет
        private static string _buildVersion = string.Empty;
        private static string _platform = string.Empty;
        private static string _unityVersion = string.Empty;

        private static void CacheHeaderStrings()
        {
            try
            {
                var v = Exerussus.AppCore.Build.BuildInfo.CurrentFullVersion;
                _buildVersion = string.IsNullOrEmpty(v) ? Application.version : v;
            }
            catch
            {
                _buildVersion = Application.version;
            }

            _platform = Application.platform.ToString();
            _unityVersion = Application.unityVersion;
        }

        // ---------------------------------------------------------------- буфер без аллокаций

        /// <summary>Выделить буфер пакета один раз: размер пакета из настроек + запас.</summary>
        private static void AllocateBuffer(AnalyticsSettings settings)
        {
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
