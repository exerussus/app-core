using System;
using System.Collections.Generic;
using UnityEngine;

namespace Exerussus.AppCore.Analytics
{
    public static partial class AppAnalytics
    {
        private static IAnalyticsStore _store;
        private static IAnalyticsAdapter _adapter;
        private static bool _defaultAdapter;
        private static readonly List<AnalyticsPacket> _queue = new();
        private static readonly List<AnalyticsPacket> _candidates = new();
        private static long _queuedBytes;
        private static double _nextPump;
        private static double _deliverNotBefore;
        private static int _maxQueuedPackets;
        private static long _maxQueuedBytes;
        private static bool _pumping;
        private static bool _storeWarned;

        /// <summary>Пакетов в очереди (ждут доставки или у адаптера).</summary>
        public static int QueuedPackets => _queue.Count;

        /// <summary>Тип назначенного адаптера (для отладки), «—» — нет.</summary>
        public static string AdapterName => _adapter != null ? _adapter.GetType().Name : "—";

        /// <summary>
        /// Назначить доставку. <c>null</c> — дефолт по настройкам (экспорт в файлы или ждать; в лёгком режиме —
        /// не писать события вовсе).
        /// Назначайте до первого кадра: <c>RuntimeInitializeOnLoad</c> с AfterAssembliesLoaded или BeforeSceneLoad
        /// (не SubsystemRegistration — там модуль сбрасывает статику) — иначе первые пакеты может забрать дефолт.
        /// </summary>
        public static void SetAdapter(IAnalyticsAdapter adapter)
        {
            var wasSilent = _booted && _lean && _adapter == null;
            _adapter = adapter;
            _defaultAdapter = false;

            // лёгкий режим молчал без адаптера — сессия начинается для данных сейчас
            if (wasSilent && adapter != null) WriteSystem("app.session.start");
            UpdatePump();
        }

        /// <summary>Заменить хранилище очереди. Уже накопленные пакеты переносятся в новое.</summary>
        public static void SetStore(IAnalyticsStore store)
        {
            if (store == null || store == _store) return;
            if (_lean)
            {
                Debug.LogWarning("[AppAnalytics] Лёгкий режим: хранилище очереди не используется.");
                return;
            }

            var previous = _store;
            _store = store;

            // копия: код хранилища может позвать Complete и изменить очередь
            foreach (var packet in _queue.ToArray())
            {
                StoreSave(packet);
                if (previous == null) continue;
                try
                {
                    previous.Delete(packet.Id);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AppAnalytics] Старое хранилище не удалило пакет {packet.Id}: {e.Message}");
                }
            }
        }

        // ---------------------------------------------------------------- хранилище (ошибка — дальше без диска)

        private static void StoreSave(AnalyticsPacket packet)
        {
            if (_store == null) return;
            try
            {
                _store.Save(packet.Id, packet.Content);
            }
            catch (Exception e)
            {
                DropStore(e);
            }
        }

        private static void StoreDelete(string packetId)
        {
            if (_store == null) return;
            try
            {
                _store.Delete(packetId);
            }
            catch (Exception e)
            {
                DropStore(e);
            }
        }

        /// <summary>Хранилище бросило исключение — работать дальше только в памяти.</summary>
        private static void DropStore(Exception e)
        {
            _store = null;
            if (_storeWarned) return;
            _storeWarned = true;
            Debug.LogWarning($"[AppAnalytics] Хранилище очереди отказало, дальше — только память: {e.Message}");
        }

        /// <summary>Пакет доставлен (сохранён/отправлен): удалить из очереди.</summary>
        public static void Complete(string packetId)
        {
            var index = IndexOf(packetId);
            if (index < 0) return;

            var packet = _queue[index];
            _queue.RemoveAt(index);
            _queuedBytes -= packet.Bytes;
            StoreDelete(packetId);
        }

        /// <summary>
        /// Доставить ждущие пакеты сейчас, без пауз повторов — например, адаптер снова подключился к серверу.
        /// Ответивший <see cref="Fail"/> пакет иначе ждал бы нарастающую паузу (до retryMax).
        /// </summary>
        public static void RetryNow()
        {
            if (!_booted) return;
            foreach (var packet in _queue)
                if (packet.State == PacketState.Pending) packet.RetryAt = 0;
            _deliverNotBefore = 0;
            Pump(force: true, urgent: false);
            UpdatePump();
        }

        /// <summary>
        /// Доставка не удалась: пакет вернётся в очередь. <paramref name="retryAfterSeconds"/> — не раньше чем через
        /// столько секунд (иначе нарастающая пауза из настроек).
        /// </summary>
        public static void Fail(string packetId, double retryAfterSeconds = -1)
        {
            var index = IndexOf(packetId);
            if (index < 0) return;

            if (double.IsNaN(retryAfterSeconds) || double.IsInfinity(retryAfterSeconds)) retryAfterSeconds = -1;

            var packet = _queue[index];
            packet.State = PacketState.Pending;
            packet.RetryAt = Now() + Math.Max(Backoff(packet.Attempt), retryAfterSeconds);
        }

        // ---------------------------------------------------------------- пакеты

        /// <summary>
        /// Закрыть открытый пакет: в очередь (на диск, если есть хранилище) и под лимиты.
        /// Единственная аллокация на пакет — строка содержимого.
        /// </summary>
        private static void Seal()
        {
            if (!_booted) return;

            try
            {
                if (_inEvent) EndEvent();

                if (_bufferPacketId == null || _bufferEvents == 0)
                {
                    _len = 0;
                    _bufferPacketId = null;
                    return;
                }

                var packet = new AnalyticsPacket(_bufferPacketId, new string(_chars, 0, _len), _bufferEvents);
                _len = 0;
                _bufferPacketId = null;
                _bufferEvents = 0;

                StoreSave(packet);
                _queue.Add(packet);
                _queuedBytes += packet.Bytes;
                EnforceLimits();
                UpdatePump();
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>
        /// Переполнение: выбросить самые старые ждущие пакеты, запомнить сколько событий потеряно. Лимиты — на ждущие:
        /// пакеты у адаптера (их не больше maxInFlight) не вытесняют новые, пока сеть медленная.
        /// </summary>
        private static void EnforceLimits()
        {
            var pending = 0;
            long pendingBytes = 0;
            foreach (var p in _queue)
                if (p.State == PacketState.Pending)
                {
                    pending++;
                    pendingBytes += p.Bytes;
                }

            while (pending > _maxQueuedPackets || pendingBytes > _maxQueuedBytes)
            {
                var oldest = -1;
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (_queue[i].State != PacketState.Pending) continue;
                    oldest = i;
                    break;
                }

                if (oldest < 0) break;

                var packet = _queue[oldest];
                _queue.RemoveAt(oldest);
                _queuedBytes -= packet.Bytes;
                pending--;
                pendingBytes -= packet.Bytes;
                StoreDelete(packet.Id);
                _dropped += Math.Max(1, packet.EventCount);
            }

            // жёсткий потолок на всю очередь, включая пакеты у адаптера: срочные сбросы (скрытие страницы) отдают
            // всё ждущее, и адаптер, который держит пакеты, не должен растить память без предела — самые старые
            // теряются (поздний Complete по ним просто ничего не найдёт)
            var settings = _settings;
            var extra = settings != null ? settings.MaxInFlight : 4;
            var packetBytes = settings != null ? (long)settings.MaxPacketChars * sizeof(char) : 0;
            var hardPackets = _maxQueuedPackets + extra;
            var hardBytes = _maxQueuedBytes + extra * packetBytes;
            while (_queue.Count > 0 && (_queue.Count > hardPackets || _queuedBytes > hardBytes))
            {
                var packet = _queue[0];
                _queue.RemoveAt(0);
                _queuedBytes -= packet.Bytes;
                StoreDelete(packet.Id);
                _dropped += Math.Max(1, packet.EventCount);
            }
        }

        private static void ClearQueue()
        {
            // сначала очередь пуста, потом хранилище — по копии: его код может позвать Complete
            var snapshot = _queue.ToArray();
            _queue.Clear();
            _queuedBytes = 0;
            foreach (var packet in snapshot) StoreDelete(packet.Id);
        }

        private static int IndexOf(string packetId)
        {
            for (int i = 0; i < _queue.Count; i++)
                if (string.Equals(_queue[i].Id, packetId, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static double Backoff(int attempt)
        {
            var settings = _settings;
            if (settings == null) return 60;
            var delay = settings.RetryBase * Math.Pow(2, Math.Min(30, Math.Max(0, attempt - 1)));
            return Math.Min(delay, settings.RetryMax);
        }

        private static IAnalyticsAdapter Adapter()
        {
            if (_adapter != null) return _adapter;
            if (_lean || _settings == null || !_settings.ExportToFilesByDefault) return null;

            _adapter = new FileExportAdapter();
            _defaultAdapter = true;
            return _adapter;
        }

        // ---------------------------------------------------------------- доставка (каждый кадр, не чаще 4 раз в секунду)

        /// <summary>
        /// Доставка. <paramref name="urgent"/> (скрытие страницы, выход): все ждущие пакеты — адаптеру сразу,
        /// мимо паузы старта, паузы повторов и лимита пакетов в полёте: другого кадра может не быть.
        /// </summary>
        private static void Pump(bool force, bool urgent)
        {
            if (!_booted || _pumping) return; // адаптер позвал Flush/Pump изнутри Deliver — не входить второй раз

            // простой (сбор выключен игроком или некуда доставлять, очередь пуста, пакета нет)
            if (!force && _queue.Count == 0 && _bufferPacketId == null && !CanWrite) return;

            _pumping = true;
            try
            {
                PumpCore(force, urgent);
            }
            catch (Exception e)
            {
                Fault(e);
            }
            finally
            {
                _pumping = false;
            }
        }

        private static void PumpCore(bool force, bool urgent)
        {
            // без опоры на catch (WebGL: NRE не ловится) — настройки проверяются явно на каждом выходе наружу
            if (_settings == null) return;

            var now = Now();
            if (!force && now < _nextPump) return;
            _nextPump = now + 0.25;

            // старый открытый пакет — закрыть
            if (_bufferPacketId != null && _bufferEvents > 0 && now - _bufferOpenedAt >= _settings.MaxPacketAge) Seal();
            if (!_booted) return;

            if (!urgent && now < _deliverNotBefore) return;

            var inFlight = 0;
            _candidates.Clear();
            foreach (var packet in _queue)
            {
                if (packet.State == PacketState.InFlight)
                {
                    // аренда истекла — адаптер не ответил, пакет снова ждёт
                    if (now >= packet.LeaseUntil)
                    {
                        packet.State = PacketState.Pending;
                        packet.RetryAt = now + Backoff(packet.Attempt);
                    }
                    else
                    {
                        inFlight++;
                    }
                    continue;
                }

                if (urgent || now >= packet.RetryAt) _candidates.Add(packet);
            }

            if (_candidates.Count == 0) return;

            var adapter = Adapter();
            if (adapter == null) return;

            foreach (var packet in _candidates)
            {
                var settings = _settings;
                if (settings == null) break;
                if (!urgent && inFlight >= settings.MaxInFlight) break;
                if (packet.State != PacketState.Pending || IndexOf(packet.Id) < 0) continue;

                packet.State = PacketState.InFlight;
                packet.Attempt++;
                packet.LeaseUntil = now + settings.DeliveryLease;
                inFlight++;

                try
                {
                    adapter.Deliver(packet);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Fail(packet.Id);
                }

                // адаптер мог выключить модуль (отказ, выключение сбора с очисткой) — дальше без настроек нельзя
                if (!_booted) break;
            }

            _candidates.Clear();
        }
    }
}
