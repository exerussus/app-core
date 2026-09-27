using System;

// Живёт в глобальном namespace сознательно: атрибут должен вешаться на поле
// без using, как соседние dropdown-атрибуты проекта. Рисует его NavigationIdDrawer (Exerussus.AppCore.Editor).
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class PagesDropdownAttribute : UnityEngine.PropertyAttribute { }
