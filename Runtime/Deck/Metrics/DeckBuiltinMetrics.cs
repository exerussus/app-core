using System;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Встроенные метрики приложения: кадр, память, мусор за кадр, страница AppCore, сцена, логи.
    /// Экземпляр живёт у хоста; читатели — его методы (замыкания создаются один раз при регистрации).
    /// </summary>
    internal sealed class DeckBuiltinMetrics : IDisposable
    {
        private const float Window = 0.5f;
        private const float Megabyte = 1024f * 1024f;

        private readonly object _owner;

        // кадр: окно в полсекунды — FPS не дрожит от кадра к кадру
        private float _accTime;
        private int _accFrames;
        private float _accMax;
        private float _fps;
        private float _avgMs;
        private float _maxMs;

        private ProfilerRecorder _gcAlloc;
        private bool _gcStarted;

        private AppRunner _runner;
        private float _runnerRetryAt;
        private float _clock;

        private Scene _scene;
        private string _sceneName;

        public DeckBuiltinMetrics(object owner) => _owner = owner;

        public void Register()
        {
            AppDeck.AddGauge("fps", () => _fps, "App", null, "F0", track: true, owner: _owner);
            AppDeck.AddGauge("frame.avg", () => _avgMs, "App", "ms", "F2", track: true, owner: _owner);
            AppDeck.AddGauge("frame.max", () => _maxMs, "App", "ms", "F2", owner: _owner);
            AppDeck.AddGauge("memory.total", () => Profiler.GetTotalAllocatedMemoryLong() / Megabyte, "Memory", "MB", "F1", track: true, owner: _owner);
            AppDeck.AddGauge("memory.managed", () => Profiler.GetMonoUsedSizeLong() / Megabyte, "Memory", "MB", "F1", track: true, owner: _owner);
            AppDeck.AddGauge("memory.gc.alloc", ReadGcAlloc, "Memory", "KB/frame", "F1", track: true, owner: _owner);
            AppDeck.AddText("app.page", ReadPage, "App", _owner);
            AppDeck.AddText("app.scene", ReadScene, "App", _owner);
            AppDeck.AddGauge("log.errors", () => AppDeck.Log?.CountOf(DeckLogType.Error) ?? 0, "Log", null, "F0", owner: _owner);
            AppDeck.AddGauge("log.warnings", () => AppDeck.Log?.CountOf(DeckLogType.Warning) ?? 0, "Log", null, "F0", owner: _owner);
            AppDeck.AddGauge("deck.commands", () => AppDeck.CommandCount, "App", null, "F0", owner: _owner);
        }

        /// <summary>Кадр. Несколько сложений — работает всегда, метрики читают готовое.</summary>
        public void Tick(float deltaTime)
        {
            _clock += deltaTime;
            _accTime += deltaTime;
            _accFrames++;
            if (deltaTime > _accMax) _accMax = deltaTime;

            if (_accTime < Window) return;

            _fps = _accFrames / _accTime;
            _avgMs = _accTime / _accFrames * 1000f;
            _maxMs = _accMax * 1000f;
            _accTime = 0f;
            _accFrames = 0;
            _accMax = 0f;
        }

        private double ReadGcAlloc()
        {
            // рекордер стартует при первом чтении: пока метрику никто не смотрит, он не работает
            if (!_gcStarted)
            {
                _gcStarted = true;
                _gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            }

            return _gcAlloc.Valid ? _gcAlloc.LastValue / 1024.0 : 0.0;
        }

        private string ReadPage()
        {
            if (_runner == null && _clock >= _runnerRetryAt)
            {
                // поиск объекта — не чаще раза в пару секунд и только пока раннера нет
                _runnerRetryAt = _clock + 2f;
                _runner = UnityEngine.Object.FindAnyObjectByType<AppRunner>();
            }

            if (_runner == null) return "нет AppRunner";

            AppPage page = _runner.CurrentPage;
            return page != null ? page.PageId : "—";
        }

        private string ReadScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (_sceneName != null && scene == _scene) return _sceneName;

            // имя сцены — новая строка на каждое чтение; кэшируем до смены сцены
            _scene = scene;
            _sceneName = scene.name;
            return _sceneName;
        }

        public void Dispose()
        {
            if (_gcStarted) _gcAlloc.Dispose();
            _gcStarted = false;
        }
    }
}
