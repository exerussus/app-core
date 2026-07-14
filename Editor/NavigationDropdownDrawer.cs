#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using App.Services.Navigator;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Выделенный drawer для string-полей с [PagesDropdown] или [PopupsDropdown].
/// Самодостаточен: значения тянет из NavigationData напрямую, без общего
/// DropdownValueProviderRegistry. По структуре — как BaseDropdownDrawer.
///
/// OdinValueDrawer + CanDrawValueProperty (а не OdinAttributeDrawer), потому что
/// поле может нести несколько атрибутов, и так надёжнее цепляться по факту наличия.
/// </summary>
[DrawerPriority(DrawerPriorityLevel.AttributePriority)]
public sealed class NavigationDropdownDrawer : OdinValueDrawer<string>
{
    private Func<IEnumerable<string>> _provider;
    private string[] _cachedValues;
    private double _lastCacheTime = -1;
    private const double CacheLifetimeSeconds = 2.0;

    // Применяем drawer, только если на поле есть [PagesDropdown] или [PopupsDropdown].
    protected override bool CanDrawValueProperty(InspectorProperty property)
    {
        return property.Attributes.OfType<PagesDropdownAttribute>().Any()
            || property.Attributes.OfType<PopupsDropdownAttribute>().Any();
    }

    protected override void Initialize()
    {
        // Попапы проверяем первыми; иначе — страницы. (Оба сразу вешать смысла нет.)
        if (Property.Attributes.OfType<PopupsDropdownAttribute>().Any())
            _provider = NavigationData.EditorPopupIds;
        else
            _provider = NavigationData.EditorPageIds;
    }

    protected override void DrawPropertyLayout(GUIContent label)
    {
        if (_provider == null)
        {
            CallNextDrawer(label);
            return;
        }

        RefreshCacheIfNeeded();

        var current = ValueEntry.SmartValue ?? string.Empty;

        const string NoneLabel = "(None)";
        var options = new List<string>(_cachedValues.Length + 2) { NoneLabel };
        options.AddRange(_cachedValues);

        int selectedIndex;
        if (string.IsNullOrEmpty(current))
        {
            selectedIndex = 0; // (None) — поле действительно пустое
        }
        else
        {
            var found = Array.IndexOf(_cachedValues, current);
            if (found >= 0)
            {
                selectedIndex = found + 1; // +1 из-за (None)
            }
            else
            {
                // значение есть, но его нет в выборке — показываем явно, не подменяем
                options.Add($"{current}  (missing)");
                selectedIndex = options.Count - 1;
            }
        }

        EditorGUILayout.BeginHorizontal();
        {
            if (label != null)
                EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));

            var newIndex = SirenixEditorFields.Dropdown(string.Empty, selectedIndex, options.ToArray());
            if (newIndex != selectedIndex)
            {
                if (newIndex == 0)
                    ValueEntry.SmartValue = string.Empty; // выбрали (None)
                else if (newIndex - 1 < _cachedValues.Length)
                    ValueEntry.SmartValue = _cachedValues[newIndex - 1];
                // клик по "(missing)" — оставляем как есть
            }

            if (!string.IsNullOrEmpty(current))
            {
                if (GUILayout.Button("✕", GUILayout.Width(20)))
                    ValueEntry.SmartValue = string.Empty;
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    private void RefreshCacheIfNeeded()
    {
        var now = EditorApplication.timeSinceStartup;
        if (_cachedValues != null && (now - _lastCacheTime) < CacheLifetimeSeconds)
            return;

        _cachedValues = _provider?.Invoke()?.ToArray() ?? Array.Empty<string>();
        _lastCacheTime = now;
    }
}
#endif