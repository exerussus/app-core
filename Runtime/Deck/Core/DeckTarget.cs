using UnityEngine;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Выбранная цель. AppCore не знает, что это —
    /// персонаж, предмет или просто GameObject и как его выбрали: значение кладёт и читает тот, кто выбрал
    /// (<see cref="AppDeck.SetTarget"/>), а команды получают его аргументом <see cref="ArgKind.Target"/>.
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
    /// Разрешить токен аргумента <see cref="ArgKind.Target"/> в цель: <c>#42</c>, <c>me</c>, имя игрока — как
    /// угодно владельцу. AppCore не знает, что за сущности в игре и как их называют: он спрашивает
    /// зарегистрированные резолверы по очереди (<see cref="AppDeck.RegisterTargetResolver"/>), первый ответивший
    /// побеждает. Не ответил никто — команда получает слово как есть (<c>all</c> и подобное решает сама).
    /// </summary>
    public delegate bool TargetResolver(string token, out DeckTarget target);
}
