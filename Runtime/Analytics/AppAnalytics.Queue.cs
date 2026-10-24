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

        /// <summary>Пакетов в очереди (ждут доставки или у адаптера).</summary>
        public static int QueuedPackets => _queue.Count;

        /// <summary>Тип назначенного адаптера (для отладки), «—» — нет.</summary>
        public static string AdapterName => _adapter != null ? _adapter.GetType().Name : "—";

        /// <summary>
        /// Назначить доставку. <c>null</c> — дефолт по настройкам (экспорт в файлы или ждать; в лёгком режиме —
        /// не писать события вовсе).
        /// Назначайте до первого кадра (RuntimeInitializeOnLoad) — иначе первые пакеты может забрать дефолт.
        /// </summary>
        public static void SetAdapter(IAnalyticsAdapter adapter)
        {
            var wasSilent = _booted && _lean && _adapter == null;
            _adapter = adapter;
            _defaultAdapter = false;

            // лёгкий режим молчал без адаптера — сессия начинается для данных сейчас
            if (wasSilent && adapter != null) WriteSystem("app.session.start");
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

            foreach (var packet in _queue)
            {
                _store.Save(packet.Id, packet.Content);
                previous?.Delete(packet.Id);
            }
        }

        /// <summary>Пакет доставлен (сохранён/отправлен): удалить из очереди.</summary>
        public static void Complete(string packetId)
        {
            var index = IndexOf(packetId);
            if (index < 0) return;

            var packet = _queue[index];
            _queue.RemoveAt(index);
            _queuedBytes -= packet.Bytes;
            _store?.Delete(packetId);
        }

        /// <summary>
        /// Доставка не удалась: пакет вернётся в очередь. <paramref name="retryAfterSeconds"/> — не раньше чем через
        /// столько секунд (иначе нарастающая пауза из настроек).
        /// </summary>
        public static void Fail(string packetId, double retryAfterSeconds = -1)
        {
            var index = IndexOf(packetId);
            if (index < 0) return;

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

                _store?.Save(packet.Id, packet.Content);
                _queue.Add(packet);
                _queuedBytes += packet.Bytes;
                EnforceLimits();
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>Переполнение: выбросить самые старые ждущие пакеты, запомнить сколько событий потеряно.</summary>
        private static void EnforceLimits()
        {
            while (_queue.Count > _maxQueuedPackets || _queuedBytes > _maxQueuedBytes)
            {
                var oldest = -1;
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (_queue[i].State != PacketState.Pending) continue;
                    oldest = i;
                    break;
                }

                if (oldest < 0) return; // всё у адаптера — ждём ответа

                var packet = _queue[oldest];
                _queue.RemoveAt(oldest);
                _queuedBytes -= packet.Bytes;
                _store?.Delete(packet.Id);
                _dropped += Math.Max(1, packet.EventCount);
            }
        }

        private static void ClearQueue()
        {
            foreach (var packet in _queue) _store?.Delete(packet.Id);
            _queue.Clear();
            _queuedBytes = 0;
        }

        private static int IndexOf(string packetId)
        {
            for (int i = 0; i < _queue.Count; i++)
                if (string.Equals(_queue[i].Id, packetId, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static double Backoff(int attempt)
        {
            var delay = _settings.RetryBase * Math.Pow(2, Math.Max(0, attempt - 1));
            return Math.Min(delay, _settings.RetryMax);
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

        private static void Pump(bool force)
        {
            if (!_booted) return;

            try
            {
                PumpCore(force);
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        private static void PumpCore(bool force)
        {

            var now = Now();
            if (!force && now < _nextPump) return;
            _nextPump = now + 0.25;

            // старый открытый пакет — закрыть
            if (_bufferPacketId != null && _bufferEvents > 0 && now - _bufferOpenedAt >= _settings.MaxPacketAge) Seal();
            if (!_booted) return;

            if (now < _deliverNotBefore) return;

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

                if (now >= packet.RetryAt) _candidates.Add(packet);
            }

            if (_candidates.Count == 0) return;

            var adapter = Adapter();
            if (adapter == null) return;

            foreach (var packet in _candidates)
            {
                if (inFlight >= _settings.MaxInFlight) break;
                if (packet.State != PacketState.Pending || IndexOf(packet.Id) < 0) continue;

                packet.State = PacketState.InFlight;
                packet.Attempt++;
                packet.LeaseUntil = now + _settings.DeliveryLease;
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
            }

            _candidates.Clear();
        }
    }
}
