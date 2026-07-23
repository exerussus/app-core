using System.Collections.Generic;
using UnityEngine.UIElements;
using Exerussus.AppCore.Layout;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Безопасная зона: реестр контейнеров и покадровое переприменение отступов.
    /// </summary>
    public partial class AppRunner
    {
        /// <summary>
        /// Реестр контейнеров безопасной зоны (элементы <c>safeArea</c> страниц, попапов и скринов).
        /// Заполняется один раз при монтировании: поиск по дереву на каждом кадре недопустим.
        /// Живёт и умирает вместе с раннером, поэтому в статике утилиты ничего не накапливается.
        /// </summary>
        private readonly List<VisualElement> _safeAreaElements = new();

        /// <summary>
        /// Добавляет контейнер безопасной зоны в реестр и сразу применяет к нему текущие отступы.
        /// Вызывается ровно один раз на элемент — в момент его монтирования.
        /// </summary>
        /// <remarks><c>null</c> игнорируется: вёрстка без контейнера <c>safeArea</c> — легальный случай.</remarks>
        internal void RegisterSafeArea(VisualElement safeAreaElement)
        {
            if (safeAreaElement == null) return;

            _safeAreaElements.Add(safeAreaElement);

            // Если расчёт уже был — не ждём следующего изменения экрана, применяем немедленно.
            if (SafeAreaLayout.HasValue) SafeAreaLayout.Current.ApplyTo(safeAreaElement);
        }

        /// <summary>
        /// Покадровая проверка безопасной зоны. В обычном кадре — несколько сравнений и выход,
        /// без аллокаций. Переприменение происходит только при реальном изменении
        /// безопасной зоны, ориентации или разрешения.
        /// </summary>
        private void TickSafeArea()
        {
            if (!SafeAreaLayout.TryRefresh(_root)) return;

            var insets = SafeAreaLayout.Current;

            // Индексный обход: foreach по List<T> тоже без аллокаций, но здесь важна явность.
            for (var i = 0; i < _safeAreaElements.Count; i++)
                insets.ApplyTo(_safeAreaElements[i]);
        }
    }
}
