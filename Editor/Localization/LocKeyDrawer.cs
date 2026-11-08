using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>Строковое поле с <see cref="LocKeyAttribute"/> — <see cref="LocKeyField"/>.</summary>
    [CustomPropertyDrawer(typeof(LocKeyAttribute))]
    public sealed class LocKeyDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.String)
                return new HelpBox($"[LocKey] — только для string ({property.displayName}).", HelpBoxMessageType.Error);

            var attr = (LocKeyAttribute)attribute;
            var field = new LocKeyField(property.displayName, attr.Prefix, attr.Caret) { value = property.stringValue };
            field.AddToClassList(BaseField<string>.alignedFieldUssClassName);

            var path = property.propertyPath;
            var target = property.serializedObject;
            field.Changed += v =>
            {
                target.Update();
                var p = target.FindProperty(path);
                if (p == null) return;
                p.stringValue = v;
                target.ApplyModifiedProperties();
            };

            // Значение могли поменять снаружи (Undo, другой инспектор).
            field.TrackPropertyValue(property, p => field.value = p.stringValue);
            return field;
        }
    }
}
