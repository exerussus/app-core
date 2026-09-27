using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Выпадающий список id для string-полей с <c>[PagesDropdown]</c>, <c>[PopupsDropdown]</c>
    /// и <c>[FragmentsDropdown]</c>. Значения берёт из единственного <c>NavigationSettings</c>.
    /// </summary>
    [CustomPropertyDrawer(typeof(PagesDropdownAttribute))]
    [CustomPropertyDrawer(typeof(PopupsDropdownAttribute))]
    [CustomPropertyDrawer(typeof(FragmentsDropdownAttribute))]
    public sealed class NavigationIdDrawer : PropertyDrawer
    {
        private const string NoneLabel = "(None)";

        private ViewKind Kind => attribute switch
        {
            PopupsDropdownAttribute => ViewKind.Popup,
            FragmentsDropdownAttribute => ViewKind.Fragment,
            _ => ViewKind.Page,
        };

        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.String)
                return new PropertyField(property);

            var choices = BuildChoices(property.stringValue, out var index);
            var field = new DropdownField(property.displayName, choices, index);
            field.AddToClassList(BaseField<string>.alignedFieldUssClassName);

            field.RegisterValueChangedCallback(evt =>
            {
                property.serializedObject.Update();
                property.stringValue = ToValue(evt.newValue);
                property.serializedObject.ApplyModifiedProperties();
            });

            // Реестр мог измениться, пока инспектор открыт.
            field.RegisterCallback<FocusInEvent>(_ =>
            {
                property.serializedObject.Update();
                field.choices = BuildChoices(property.stringValue, out var i);
                field.SetValueWithoutNotify(field.choices[i]);
            });

            return field;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            var choices = BuildChoices(property.stringValue, out var index);

            EditorGUI.BeginProperty(position, label, property);
            var newIndex = EditorGUI.Popup(position, label.text, index, choices.ToArray());
            if (newIndex != index) property.stringValue = ToValue(choices[newIndex]);
            EditorGUI.EndProperty();
        }

        private List<string> BuildChoices(string current, out int index)
        {
            var choices = new List<string> { NoneLabel };
            choices.AddRange(AppCoreAssets.GetIds(Kind));

            if (string.IsNullOrEmpty(current))
            {
                index = 0;
                return choices;
            }

            index = choices.IndexOf(current);
            if (index > 0) return choices;

            // Значение есть, но в реестре его нет — показываем явно, а не подменяем.
            choices.Add(current + "  (missing)");
            index = choices.Count - 1;
            return choices;
        }

        private static string ToValue(string choice)
        {
            if (choice == NoneLabel) return string.Empty;
            const string missing = "  (missing)";
            return choice.EndsWith(missing) ? choice.Substring(0, choice.Length - missing.Length) : choice;
        }
    }
}
