using System;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Exerussus.AppCore.Analytics
{
    public static partial class AppAnalytics
    {
        private static bool _loopInstalled;
        private static bool _quitHooked;
        private static bool _focusHooked;
        private static bool _flushOnFocusLoss;
        private static double _unfocusedAt = -1;
        private static double _lastPauseFlush = double.NegativeInfinity;
        private static GameObject _host;

        /// <summary>Сброс по потере фокуса не чаще раза в столько секунд: мигание фокуса (клавиатура, оверлеи) не плодит пакеты.</summary>
        private const double PauseFlushInterval = 5.0;

        // статика между запусками без перезагрузки домена — в ноль
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            try
            {
                Shutdown(seal: false);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _booted = false;
            }

            _faulted = false;
            _adapter = null;
            _defaultAdapter = false;
            _store = null;
            _gameClock = null;
            ProfileChanged = null;
            CollectingChanged = null;
        }

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

            if (_lean)
            {
                // WebGL: файлы живут в памяти страницы (и в IndexedDB) — никакого диска, очередь маленькая
                _store = null;
                _maxQueuedPackets = settings.LeanMaxQueuedPackets;
                _maxQueuedBytes = settings.LeanMaxQueuedBytes;
                _queue.Clear();
                _queuedBytes = 0;
            }
            else
            {
                _maxQueuedPackets = settings.MaxQueuedPackets;
                _maxQueuedBytes = settings.MaxQueuedBytes;
                _store ??= new FileAnalyticsStore();
                LoadQueue();
            }

            _profileId = PlayerPrefs.GetString(PrefsProfile, string.Empty);
            if (string.IsNullOrEmpty(_profileId))
            {
                _profileId = ProfilePrefix + Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(PrefsProfile, _profileId);
                PlayerPrefs.Save();
            }

            _collecting = PlayerPrefs.GetInt(PrefsCollect, settings.CollectByDefault ? 1 : 0) == 1;

            _sessionId = Guid.NewGuid().ToString("N");
            _sessionStart = Now();
            _sessionStartUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _deliverNotBefore = _sessionStart + 1.0; // дать проекту назначить адаптер до первой доставки

            if (Application.isPlaying)
            {
                InstallLoop();
                CreateHost();

                if (!_quitHooked)
                {
                    Application.quitting += OnQuitting;
                    _quitHooked = true;
                }

                if (!_focusHooked)
                {
                    Application.focusChanged += OnFocusChanged;
                    _focusHooked = true;
                }

                _flushOnFocusLoss = Application.platform == RuntimePlatform.WebGLPlayer || Application.isMobilePlatform;
                // на старте isFocused часто ещё false — не считать это уходом из приложения
                _unfocusedAt = -1;
                _lastPauseFlush = double.NegativeInfinity;

#if UNITY_EDITOR
                UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
            }

            _booted = true;
            AnalyticsDeckCommands.Register();

            WriteSystem("app.session.start");
        }

        private static void Shutdown(bool seal)
        {
            if (seal && _booted && _collecting) Seal();

            _booted = false;
            UninstallLoop();

            if (_quitHooked)
            {
                Application.quitting -= OnQuitting;
                _quitHooked = false;
            }

            if (_focusHooked)
            {
                Application.focusChanged -= OnFocusChanged;
                _focusHooked = false;
            }

            if (_host != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_host);
                else UnityEngine.Object.DestroyImmediate(_host);
            }

            _host = null;
            _unfocusedAt = -1;
            AnalyticsDeckCommands.Unregister();

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
            _dropped = 0;
            _queue.Clear();
            _candidates.Clear();
            _queuedBytes = 0;
            _nextPump = 0;
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

        /// <summary>Пакеты прошлых запусков — снова в очередь.</summary>
        private static void LoadQueue()
        {
            _queue.Clear();
            _queuedBytes = 0;
            if (_store == null) return;

            System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> stored;
            try
            {
                stored = _store.LoadAll();
            }
            catch (Exception e)
            {
                DropStore(e);
                return;
            }

            foreach (var pair in stored)
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
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>
        /// Окно приложения потеряло / вернуло фокус: <c>app.focus.lost</c>, <c>app.focus.gained</c> с полем
        /// <c>away</c> — сколько секунд приложение было не в фокусе.
        /// </summary>
        private static void OnFocusChanged(bool focused)
        {
            if (!_booted) return;

            try
            {
                FocusChanged(focused);
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        private static void FocusChanged(bool focused)
        {

            if (!focused)
            {
                if (_unfocusedAt >= 0) return;
                _unfocusedAt = Now();
                WriteSystem("app.focus.lost");

                // WebGL и мобильные: потеря фокуса — вкладку скрыли / ушли в фон, могут не вернуться.
                if (_flushOnFocusLoss) PauseFlush();
                return;
            }

            if (_unfocusedAt < 0) return;
            var away = Now() - _unfocusedAt;
            _unfocusedAt = -1;

            if (!BeginSystem("app.focus.gained")) return;
            WriteField("away", away);
            EndEvent();
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

        /// <summary>Страница WebGL скрыта (visibilitychange / pagehide из jslib) или снова видна.</summary>
        internal static void OnPageVisibility(bool visible)
        {
            OnFocusChanged(visible);
        }

        private static void PauseFlush()
        {
            var now = Now();
            if (now - _lastPauseFlush < PauseFlushInterval) return;
            _lastPauseFlush = now;
            Flush(AnalyticsFlushReason.Pause);
        }

        private const string HostName = "[AppAnalytics]";

        private static void CreateHost()
        {
            if (_host != null) return;
            // HideInHierarchy, не DontSave: такой объект Unity уничтожает при выходе из Play Mode
            _host = new GameObject(HostName) { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<AnalyticsHost>();

#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                AppAnalytics_HookVisibility(OnVisibilityNative);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppAnalytics] Не удалось подписаться на видимость страницы: {e.Message}");
            }
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private delegate void VisibilityCallback(int visible);

        // Plugins/WebGL/AppAnalytics.jslib: visibilitychange + pagehide → сюда (WebView в фоне не шлёт blur)
        [DllImport("__Internal")]
        private static extern void AppAnalytics_HookVisibility(VisibilityCallback callback);

        [AOT.MonoPInvokeCallback(typeof(VisibilityCallback))]
        private static void OnVisibilityNative(int visible)
        {
            try
            {
                OnPageVisibility(visible != 0);
            }
            catch
            {
                // из браузерного колбэка исключение не выпускать
            }
        }
#endif

#if UNITY_EDITOR
        /// <summary>Выход из Play Mode без перезагрузки домена — модуль снимается целиком (цикл, буфер, очередь, подписки).</summary>
        private static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;

            try
            {
                Shutdown(seal: true);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _booted = false;
            }
        }
#endif

        // ---------------------------------------------------------------- PlayerLoop

        /// <summary>Маркер системы в PlayerLoop.</summary>
        private struct AppAnalyticsPump { }

        private static readonly PlayerLoopSystem.UpdateFunction PumpTick = () => Pump(force: false);

        private static void InstallLoop()
        {
            if (_loopInstalled) return;
            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (!Modify(ref root, true)) return;
            PlayerLoop.SetPlayerLoop(root);
            _loopInstalled = true;
        }

        private static void UninstallLoop()
        {
            if (!_loopInstalled) return;
            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (Modify(ref root, false)) PlayerLoop.SetPlayerLoop(root);
            _loopInstalled = false;
        }

        /// <summary>Добавить (или убрать) свою систему в конец PostLateUpdate.</summary>
        private static bool Modify(ref PlayerLoopSystem root, bool add)
        {
            var phases = root.subSystemList;
            if (phases == null) return false;

            for (int p = 0; p < phases.Length; p++)
            {
                if (phases[p].type != typeof(PostLateUpdate)) continue;

                var systems = phases[p].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                var existing = -1;
                for (int s = 0; s < systems.Length; s++)
                    if (systems[s].type == typeof(AppAnalyticsPump)) existing = s;

                if (add)
                {
                    if (existing >= 0)
                    {
                        // осталась система прошлого запуска — подменить делегат, а не ставить вторую
                        systems[existing].updateDelegate = PumpTick;
                        phases[p].subSystemList = systems;
                        root.subSystemList = phases;
                        return true;
                    }

                    var next = new PlayerLoopSystem[systems.Length + 1];
                    Array.Copy(systems, next, systems.Length);
                    next[systems.Length] = new PlayerLoopSystem { type = typeof(AppAnalyticsPump), updateDelegate = PumpTick };
                    phases[p].subSystemList = next;
                }
                else
                {
                    if (existing < 0) return false;
                    var next = new PlayerLoopSystem[systems.Length - 1];
                    for (int s = 0, d = 0; s < systems.Length; s++)
                        if (s != existing) next[d++] = systems[s];
                    phases[p].subSystemList = next;
                }

                root.subSystemList = phases;
                return true;
            }

            return false;
        }
    }
}
