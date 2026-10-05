using UnityEngine;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Выбранная цель: то, на что кликнули в мире при открытом AppDeck. AppCore не знает, что это —
    /// персонаж, предмет или просто GameObject: значение кладёт и читает тот, кто его выбрал
    /// (<see cref="IDeckPicker"/>), а команды получают его аргументом <see cref="ArgKind.Target"/>.
    /// </summary>
    public readonly struct DeckTarget
    {
        /// <summary>Сам объект цели (сущность игры, GameObject, что угодно). null — цели нет.</summary>
        public readonly object Value;

        /// <summary>Подпись в шапке AppDeck.</summary>
        public readonly string Label;

        /// <summary>Точка попадания в мире (если есть).</summary>
        public readonly Vector3 Point;

        /// <summary>Кто выбрал: владелец пикера. Помогает команде понять, «её» ли это цель.</summary>
        public readonly object Source;

        public DeckTarget(object value, string label, Vector3 point = default, object source = null)
        {
            Value = value;
            Label = label;
            Point = point;
            Source = source;
        }

        public bool IsValid => Value != null && !(Value is Object unityObject && unityObject == null);

        /// <summary>Значение нужного типа или null.</summary>
        public T As<T>() where T : class => IsValid ? Value as T : null;

        public static DeckTarget None => default;
    }

    /// <summary>
    /// Выбор цели кликом по миру. Регистрирует проект (<see cref="AppDeck.RegisterPicker"/>):
    /// игра знает свои сущности, AppCore — только экран и клик. Пикеры опрашиваются по убыванию
    /// приоритета, первый попавший побеждает.
    /// </summary>
    public interface IDeckPicker
    {
        /// <summary>Чем больше, тем раньше спрашивают. Встроенный физический пикер — <c>int.MinValue</c>.</summary>
        int Priority { get; }

        /// <summary>Клик по миру. <paramref name="screenPosition"/> — пиксели экрана, начало слева снизу.</summary>
        bool TryPick(Vector2 screenPosition, out DeckTarget target);
    }

    /// <summary>
    /// Пикер по умолчанию: луч из главной камеры по физике, цель — GameObject коллайдера
    /// (или его Rigidbody). Работает в любом проекте без настройки; игра перекрывает его своим
    /// пикером с большим приоритетом.
    /// </summary>
    public sealed class PhysicsDeckPicker : IDeckPicker
    {
        private readonly float _maxDistance;
        private readonly int _layers;
        private Camera _camera;

        public PhysicsDeckPicker(float maxDistance, int layers)
        {
            _maxDistance = maxDistance;
            _layers = layers;
        }

        public int Priority => int.MinValue;

        public bool TryPick(Vector2 screenPosition, out DeckTarget target)
        {
            target = default;

            // Camera.main — поиск по тегу; кэшируем и перепроверяем, только если камера пропала
            if (_camera == null || !_camera.isActiveAndEnabled) _camera = Camera.main;
            if (_camera == null) return false;

            Ray ray = _camera.ScreenPointToRay(screenPosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, _maxDistance, _layers, QueryTriggerInteraction.Ignore)) return false;

            GameObject go = hit.rigidbody != null ? hit.rigidbody.gameObject : hit.collider.gameObject;
            target = new DeckTarget(go, go.name, hit.point, this);
            return true;
        }
    }
}
