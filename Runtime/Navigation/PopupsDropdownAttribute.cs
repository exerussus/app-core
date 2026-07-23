using System;

// Живёт в глобальном namespace сознательно: атрибут должен вешаться на поле
// без using, как соседние dropdown-атрибуты проекта. Рисует его NavigationIdDrawer.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class PopupsDropdownAttribute : Attribute { }
