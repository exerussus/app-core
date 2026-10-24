using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    public static partial class AppAnalytics
    {
        private static bool _quitHooked;
        private static bool _focusHooked;
        private static bool _flushOnFocusLoss;
        private static bool _visibilityHooked;
        private static double _unfocusedAt = -1;
        private static double _lastPauseFlush = double.NegativeInfinity;
        private static double _lastHideFlush = double.NegativeInfinity;

        /// <summary>Скрытие страницы WebGL сбрасывает пакет не чаще раза в столько секунд.</summary>
        private const double HideFlushInterval = 1.0;

        /// <summary>Сброс по потере фокуса (мобильные, не WebGL) не чаще раза в столько секунд.</summary>
        private const double PauseFlushInterval = 5.0;

        // хост: скрытый объект с двумя компонентами — пауза приложения (всегда) и насос доставки (включается по делу)
        private static GameObject _host;
        private static AnalyticsPump _pump;
        private static bool _pumpOn;

        // выбор игрока и id профиля, переданные до старта модуля: применяются при старте (и только тогда пишутся)
        private static bool? _pendingCollect;
        private static string _pendingProfile;

        // статика между запусками без перезагрузки домена — в ноль
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Shutdown(seal: false, fromFault: false);
            _faulted = false;
            _adapter = null;
            _defaultAdapter = false;
            _store = null;
            _gameClock = null;
            _collecting = false;
            _profileId = string.Empty;
            _sessionId = string.Empty;
            _pendingCollect = null;
            _pendingProfile = null;
            ClearProperties();
            ProfileChanged = null;
            CollectingChanged = null;
        }

        /// <summary>Для EditMode-тестов: поднять модуль на своих настройках (без объекта — не Play Mode).</summary>
        internal static void StartForTests(AnalyticsSettings settings) => Startup(settings);

        /// <summary>Для EditMode-тестов: сбросить всё, как между запусками.</summary>
        internal static void ResetForTests() => ResetStatics();

        internal static bool IsFaultedForTests => _faulted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            try
            {
                Startup(AnalyticsSettings.Current);
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        private static void Startup(AnalyticsSettings settings)
        {
            if (_booted || _faulted || settings == null || !settings.Enabled) return;

            _settings = settings;
            _lean = settings.IsLeanOn(Application.platform);
            Bake(settings);
            CacheHeaderStrings(); // буфер пакета — лениво, на первом событии

            _queue.Clear();
            _queuedBytes = 0;
            if (_lean)
            {
                // WebGL: файлы живут в памяти страницы (и в IndexedDB) — никакого диска, очередь маленькая
                _store = null;
                _maxQueuedPackets = settings.LeanMaxQueuedPackets;
                _maxQueuedBytes = settings.LeanMaxQueuedBytes;
            }
            else
            {
                _maxQueuedPackets = settings.MaxQueuedPackets;
                _maxQueuedBytes = settings.MaxQueuedBytes;
                _store ??= new FileAnalyticsStore();
                LoadQueue();
            }

            StartProfile(settings);

            _sessionId = Guid.NewGuid().ToString("N");
            _sessionStart = Now();
            _sessionStartUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _deliverNotBefore = _sessionStart + 1.0; // дать проекту назначить адаптер до первой доставки

            if (Application.isPlaying)
            {
                CreateHost();

                if (!_quitHooked)
                {
                    Application.quitting += OnQuitting;
                    _quitHooked = true;
                }

                // WebGL — только видимость страницы (jslib): фокус canvas мигает от клавиатуры и оверлеев SDK.
                // Мобильные платформы — фокус как последний шанс сбросить пакет; на ПК не подписываемся вовсе.
                _flushOnFocusLoss = Application.platform != RuntimePlatform.WebGLPlayer && Application.isMobilePlatform;
                if (_flushOnFocusLoss && !_focusHooked)
                {
                    Application.focusChanged += OnFocusChanged;
                    _focusHooked = true;
                }

                _unfocusedAt = -1;
                _lastPauseFlush = double.NegativeInfinity;
                _lastHideFlush = double.NegativeInfinity;

#if UNITY_EDITOR
                UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
            }

            _booted = true;
            AnalyticsDeckCommands.Register();
            _deckRegistered = true;

            WriteSystem("app.session.start");
            UpdatePump();
        }

        /// <summary>
        /// Профиль и выбор игрока: из PlayerPrefs; переданные до старта (<see cref="SetCollecting"/>,
        /// <see cref="SetProfileId"/>) — сильнее и записываются сейчас. Сбой PlayerPrefs (квота IndexedDB в WebGL) —
        /// значения живут в памяти до конца запуска, модуль работает.
        /// </summary>
        private static void StartProfile(AnalyticsSettings settings)
        {
            var stored = PrefsGetString(PrefsProfile);
            var pending = _pendingProfile;
            _pendingProfile = null;
            if (!string.IsNullOrEmpty(pending)) _profileId = pending;
            else if (!string.IsNullOrEmpty(stored)) _profileId = stored;
            else _profileId = ProfilePrefix + Guid.NewGuid().ToString("N");
            if (!string.Equals(stored, _profileId, StringComparison.Ordinal)) PrefsSetString(PrefsProfile, _profileId);

            var storedCollect = PrefsGetInt(PrefsCollect, settings.CollectByDefault ? 1 : 0) == 1;
            if (_pendingCollect != null)
            {
                _collecting = _pendingCollect.Value;
                _pendingCollect = null;
                if (_collecting != storedCollect) PrefsSetInt(PrefsCollect, _collecting ? 1 : 0);
            }
            else
            {
                _collecting = storedCollect;
            }
        }

        // ---------------------------------------------------------------- PlayerPrefs без исключений в игру

        private static string PrefsGetString(string key)
        {
            try
            {
                return PlayerPrefs.GetString(key, string.Empty) ?? string.Empty;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AppAnalytics] PlayerPrefs не читаются: " + e.Message);
                return string.Empty;
            }
        }

        private static int PrefsGetInt(string key, int fallback)
        {
            try
            {
                return PlayerPrefs.GetInt(key, fallback);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AppAnalytics] PlayerPrefs не читаются: " + e.Message);
                return fallback;
            }
        }

        private static void PrefsSetString(string key, string value)
        {
            try
            {
                PlayerPrefs.SetString(key, value);
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AppAnalytics] PlayerPrefs не записались (значение живёт до конца запуска): " + e.Message);
            }
        }

        private static void PrefsSetInt(string key, int value)
        {
            try
            {
                PlayerPrefs.SetInt(key, value);
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AppAnalytics] PlayerPrefs не записались (значение живёт до конца запуска): " + e.Message);
            }
        }

        // ---------------------------------------------------------------- остановка

        /// <summary>
        /// Остановить модуль. Сначала своё состояние (без вызовов наружу), потом внешнее — каждое отдельно.
        /// <paramref name="fromFault"/>: отказ мог прийти из кадра насоса — объект и подписки не трогаются в этом
        /// кадре: насос и пауза выходят на <c>!_booted</c>, объект доживает до следующего запуска.
        /// </summary>
        private static void Shutdown(bool seal, bool fromFault)
        {
            if (seal && _booted && _collecting)
            {
                try
                {
                    Seal();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            _booted = false;
            _settings = null;
            _keys = Array.Empty<string>();
            _eventOn = Array.Empty<bool>();
            _schema = string.Empty;
            _runId = null;
            _seq = 0;
            _packetIndex = 0;
            ReleaseBuffer();
            _lean = false;
            _bufferPacketId = null;
            _bufferEvents = 0;
            _inEvent = false;
            _starting = false;
            _dropped = 0;
            _queue.Clear();
            _candidates.Clear();
            _queuedBytes = 0;
            _nextPump = 0;
            _unfocusedAt = -1;
            _lastPauseFlush = double.NegativeInfinity;
            _lastHideFlush = double.NegativeInfinity;
            _buildFinal = false;

            if (fromFault)
            {
                // насос выключить можно (это флаг компонента, Unity применяет его безопасно), остальное — не трогать
                SetPumpEnabled(false);
                return;
            }

            // внешние шаги — только то, что модуль сам ставил: выключенный модуль (никогда не стартовал)
            // при сбросе статики ничего наружу не зовёт
            if (_quitHooked)
            {
                _quitHooked = false;
                Safe(UnsubscribeQuit);
            }

            if (_focusHooked)
            {
                _focusHooked = false;
                Safe(UnsubscribeFocus);
            }

            if (_visibilityHooked) Safe(UnhookVisibility);

            var host = _host;
            _host = null;
            _pump = null;
            _pumpOn = false;
            if (host != null) Safe(() => DestroyHost(host));

            if (_deckRegistered)
            {
                _deckRegistered = false;
                Safe(AnalyticsDeckCommands.Unregister);
            }
        }

        private static bool _deckRegistered;

        private static void UnsubscribeQuit() => Application.quitting -= OnQuitting;

        private static void UnsubscribeFocus() => Application.focusChanged -= OnFocusChanged;

        private static void DestroyHost(GameObject host)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(host);
            else UnityEngine.Object.DestroyImmediate(host);
        }

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>Каталог → плоские массивы: номер события = индекс записи.</summary>
        private static void Bake(AnalyticsSettings settings)
        {
            var entries = settings.Entries;
            _keys = new string[entries.Length];
            _eventOn = new bool[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                _keys[i] = entries[i].key ?? string.Empty;
                _eventOn[i] = !entries[i].disabled && !string.IsNullOrEmpty(entries[i].key);
            }

            _schema = settings.SchemaHash();
        }

        /// <summary>Пакеты прошлых запусков — снова в очередь. Хранилище проекта может вернуть null или бросить при перечислении.</summary>
        private static void LoadQueue()
        {
            _queue.Clear();
            _queuedBytes = 0;
            if (_store == null) return;

            var loaded = new List<KeyValuePair<string, string>>();
            try
            {
                var stored = _store.LoadAll();
                if (stored != null)
                    foreach (var pair in stored)
                    {
                        loaded.Add(pair);
                        if (loaded.Count > 100000) break; // не верить бесконечному перечислению
                    }
            }
            catch (Exception e)
            {
                DropStore(e);
                return;
            }

            foreach (var pair in loaded)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null || IndexOf(pair.Key) >= 0) continue;
                var packet = new AnalyticsPacket(pair.Key, pair.Value, -1);
                _queue.Add(packet);
                _queuedBytes += packet.Bytes;
            }

            // старые пакеты сверх лимитов (лимит уменьшили, удаление не прошло) — выбросить самые старые
            EnforceLimits();
        }

        // ---------------------------------------------------------------- последний шанс

        private static void OnQuitting()
        {
            try
            {
                WriteSystem("app.session.end");
                Flush(AnalyticsFlushReason.Quit);
                UnhookVisibility();
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>Мобильные (не WebGL): окно потеряло фокус — сбросить пакет. Событий о фокусе модуль не пишет.</summary>
        private static void OnFocusChanged(bool focused)
        {
            if (!_booted) return;

            try
            {
                if (focused)
                {
                    _unfocusedAt = -1;
                    return;
                }

                if (_unfocusedAt >= 0) return;
                _unfocusedAt = Now();
                if (_flushOnFocusLoss) PauseFlush();
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        internal static void OnApplicationPause(bool paused)
        {
            if (!paused || !_booted) return;
            try
            {
                PauseFlush();
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>Страница WebGL скрыта (visibilitychange / pagehide из jslib) — всегда сброс: могут не вернуться.</summary>
        internal static void OnPageVisibility(bool visible)
        {
            if (visible || !_booted) return;

            try
            {
                var now = Now();
                if (now - _lastHideFlush < HideFlushInterval) return;
                _lastHideFlush = now;
                _lastPauseFlush = now;
                Flush(AnalyticsFlushReason.Pause);
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        private static void PauseFlush()
        {
            var now = Now();
            if (now - _lastPauseFlush < PauseFlushInterval) return;
            _lastPauseFlush = now;
            Flush(AnalyticsFlushReason.Pause);
        }

        // ---------------------------------------------------------------- хост и насос

        private const string HostName = "[AppAnalytics]";

        private static void CreateHost()
        {
            if (_host != null) return;
            // HideInHierarchy, не DontSave: такой объект Unity уничтожает при выходе из Play Mode
            _host = new GameObject(HostName) { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<AnalyticsHost>();
            _pump = _host.AddComponent<AnalyticsPump>();
            _pump.enabled = false;
            _pumpOn = false;

#if UNITY_WEBGL && !UNITY_EDITOR
            HookVisibility();
#endif
        }

        /// <summary>
        /// Насос (Update компонента) работает, только когда есть что делать: события пишутся или в очереди лежат
        /// пакеты. Выключен сбор, лёгкий режим без адаптера, пусто — Unity не зовёт Update вовсе. Не PlayerLoop:
        /// библиотеки, которые подменяют цикл (SetPlayerLoop), не снимают доставку, и отказ не перестраивает цикл
        /// изнутри его обхода.
        /// </summary>
        private static void UpdatePump() => SetPumpEnabled(_booted && (CanWrite || _queue.Count > 0 || _bufferPacketId != null));

        private static void SetPumpEnabled(bool on)
        {
            if (_pumpOn == on) return;
            var pump = _pump;
            if (pump == null) return; // нет объекта (не Play Mode) или его уничтожили снаружи
            pump.enabled = on;
            _pumpOn = on;
        }

        /// <summary>Кадр насоса (AnalyticsPump.Update).</summary>
        internal static void Tick()
        {
            if (!_booted)
            {
                SetPumpEnabled(false);
                return;
            }

            Pump(force: false, urgent: false);
            UpdatePump();
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void VisibilityCallback(int visible);

        // Plugins/WebGL/AppAnalytics.jslib: visibilitychange + pagehide → сюда (WebView в фоне не шлёт blur)
        [DllImport("__Internal")]
        private static extern void AppAnalytics_HookVisibility(VisibilityCallback callback);

        [DllImport("__Internal")]
        private static extern void AppAnalytics_UnhookVisibility();

        private static void HookVisibility()
        {
            if (_visibilityHooked) return;
            try
            {
                AppAnalytics_HookVisibility(OnVisibilityNative);
                _visibilityHooked = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppAnalytics] Не удалось подписаться на видимость страницы: {e.Message}");
            }
        }

        private static void UnhookVisibility()
        {
            if (!_visibilityHooked) return;
            _visibilityHooked = false;
            AppAnalytics_UnhookVisibility();
        }

        // Колбэк из JS. На WebGL с «Explicitly Thrown Exceptions Only» catch не ловит NRE — поэтому код ниже
        // не опирается на catch: OnPageVisibility проверяет состояние сам.
        [AOT.MonoPInvokeCallback(typeof(VisibilityCallback))]
        private static void OnVisibilityNative(int visible)
        {
            try
            {
                OnPageVisibility(visible != 0);
            }
            catch
            {
                // из браузерного колбэка явное исключение не выпускать
            }
        }
#else
        private static void UnhookVisibility() => _visibilityHooked = false;
#endif

#if UNITY_EDITOR
        /// <summary>Выход из Play Mode без перезагрузки домена — модуль снимается целиком (насос, буфер, очередь, подписки).</summary>
        private static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Shutdown(seal: true, fromFault: false);
        }
#endif
    }
}
