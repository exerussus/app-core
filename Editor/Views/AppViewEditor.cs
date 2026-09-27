using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Инспектор страницы, попапа и фрагмента. Руками здесь выбирается только id —
    /// имя GameObject, вёрстка и контроллер выводятся из него и проставляются сами.
    /// </summary>
    public abstract class AppViewEditor : UnityEditor.Editor
    {
        private const string NotSelected = "— не выбран —";

        private VisualElement _root;
        private DropdownField _idField;
        private VisualElement _status;
        private VisualElement _problems;

        protected abstract ViewKind Kind { get; }

        private Component View => (Component)target;

        public override VisualElement CreateInspectorGUI()
        {
            // Канон проставляется при открытии: имя, вёрстка по пути, найденный контроллер.
            if (!Application.isPlaying) ViewBinding.Sync(View, recordUndo: false);

            _root = new VisualElement();

            _idField = new DropdownField("Id");
            _idField.AddToClassList(BaseField<string>.alignedFieldUssClassName);
            _idField.RegisterValueChangedCallback(OnIdChanged);
            _idField.RegisterCallback<FocusInEvent>(_ => RefreshIdChoices());
            _root.Add(_idField);

            _status = new VisualElement();
            _status.style.marginTop = 4;
            _root.Add(_status);

            var extra = new VisualElement();
            extra.style.marginTop = 6;
            BuildKindSpecific(extra);
            _root.Add(extra);

            _problems = new VisualElement();
            _problems.style.marginTop = 6;
            _root.Add(_problems);

            RefreshAll();

            // Изменения с диска (сгенерирован контроллер, создан uxml) — перерисовываем статус.
            _root.RegisterCallback<AttachToPanelEvent>(_ => EditorApplication.projectChanged += RefreshAll);
            _root.RegisterCallback<DetachFromPanelEvent>(_ => EditorApplication.projectChanged -= RefreshAll);

            return _root;
        }

        /// <summary>Поля, специфичные для вида вью.</summary>
        protected virtual void BuildKindSpecific(VisualElement container) { }

        // ============================================================ id

        private void RefreshIdChoices()
        {
            var current = ViewBinding.GetId(View);
            var used = ViewBinding.UsedIds(Kind, View);

            var choices = new List<string> { NotSelected };
            choices.AddRange(AppCoreAssets.GetIds(Kind).Where(id => !used.Contains(id) || id == current));

            var shown = string.IsNullOrEmpty(current) ? NotSelected : current;
            if (!choices.Contains(shown)) choices.Add(shown);

            _idField.choices = choices;
            _idField.SetValueWithoutNotify(shown);
        }

        private void OnIdChanged(ChangeEvent<string> evt)
        {
            var id = evt.newValue == NotSelected ? string.Empty : evt.newValue;
            if (id == ViewBinding.GetId(View)) return;

            ViewBinding.AssignId(View, id);
            RefreshAll();
        }

        // ============================================================ статус

        private void RefreshAll()
        {
            if (_root == null || target == null) return;

            serializedObject.Update();
            RefreshIdChoices();
            RefreshStatus();
            RefreshProblems();
        }

        private void RefreshStatus()
        {
            _status.Clear();

            var id = ViewBinding.GetId(View);
            if (string.IsNullOrEmpty(id))
            {
                _status.Add(new HelpBox(
                    AppCoreAssets.GetIds(Kind).Count == 0
                        ? "В NavigationSettings нет ни одного id этого вида. Добавьте его на странице App в Nexus."
                        : "Выберите id — остальное проставится само.",
                    HelpBoxMessageType.Info));
                return;
            }

            // ---- вёрстка ----
            var uxmlPath = AppCorePaths.ViewUxmlPath(Kind, id);
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);

            var treeRow = Row();
            var treeField = new ObjectField("Вёрстка") { objectType = typeof(VisualTreeAsset), value = uxml };
            treeField.AddToClassList(BaseField<Object>.alignedFieldUssClassName);
            treeField.SetEnabled(false);
            treeField.style.flexGrow = 1;
            treeRow.Add(treeField);

            if (uxml == null)
            {
                treeRow.Add(new Button(() =>
                {
                    if (ViewScaffold.EnsureUxml(Kind, id, out _)) ViewBinding.Sync(View, recordUndo: true);
                    RefreshAll();
                }) { text = "Создать", tooltip = uxmlPath });
            }

            _status.Add(treeRow);

            // ---- контроллер ----
            var className = AppNaming.ControllerName(id, Kind);
            var type = ViewBinding.FindControllerType(Kind, id);
            var controller = serializedObject.FindProperty(ViewBinding.ControllerProperty).objectReferenceValue;

            var controllerRow = Row();
            var controllerField = new ObjectField("Контроллер") { objectType = typeof(MonoBehaviour), value = controller };
            controllerField.AddToClassList(BaseField<Object>.alignedFieldUssClassName);
            controllerField.SetEnabled(false);
            controllerField.style.flexGrow = 1;
            controllerRow.Add(controllerField);

            if (controller == null)
            {
                if (type != null)
                {
                    controllerRow.Add(new Button(() =>
                    {
                        ViewBinding.AttachOrGenerateController(View, allowGenerate: false);
                        RefreshAll();
                    }) { text = "Навесить", tooltip = className });
                }
                else
                {
                    controllerRow.Add(new Button(() =>
                    {
                        ViewBinding.AttachOrGenerateController(View, allowGenerate: true);
                        RefreshAll();
                    }) { text = "Создать и навесить", tooltip = AppCorePaths.ControllerPath(Kind, id) });
                }
            }

            _status.Add(controllerRow);

            var hint = new Label(controller == null && type == null && File.Exists(AppCorePaths.ControllerPath(Kind, id))
                ? $"{className} создан — ждём компиляцию."
                : className);
            hint.style.unityFontStyleAndWeight = FontStyle.Italic;
            hint.style.opacity = 0.6f;
            hint.style.marginLeft = 3;
            _status.Add(hint);
        }

        private void RefreshProblems()
        {
            _problems.Clear();
            foreach (var problem in ViewBinding.Validate(View))
                _problems.Add(new HelpBox(problem, HelpBoxMessageType.Warning));
        }

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }
    }

    [CustomEditor(typeof(AppPage))]
    public sealed class AppPageEditor : AppViewEditor
    {
        protected override ViewKind Kind => ViewKind.Page;

        protected override void BuildKindSpecific(VisualElement container)
        {
            var back = serializedObject.FindProperty("backAction");
            var backPage = serializedObject.FindProperty("backPageId");

            var backField = new PropertyField(back, "Назад");
            var backPageField = new PropertyField(backPage, "Цель «назад»");
            container.Add(backField);
            container.Add(backPageField);
            container.Add(new PropertyField(serializedObject.FindProperty("cursorMode"), "Курсор"));

            void UpdateVisibility() => backPageField.style.display =
                back.enumValueIndex == (int)PageBackAction.ToPage ? DisplayStyle.Flex : DisplayStyle.None;

            UpdateVisibility();
            backField.RegisterValueChangeCallback(_ => UpdateVisibility());
            container.Bind(serializedObject);
        }
    }

    [CustomEditor(typeof(AppPopup))]
    public sealed class AppPopupEditor : AppViewEditor
    {
        protected override ViewKind Kind => ViewKind.Popup;
    }

    [CustomEditor(typeof(AppFragment))]
    public sealed class AppFragmentEditor : AppViewEditor
    {
        protected override ViewKind Kind => ViewKind.Fragment;

        protected override void BuildKindSpecific(VisualElement container)
        {
            container.Add(new PropertyField(serializedObject.FindProperty("hostId"), "Хост"));
            container.Add(new PropertyField(serializedObject.FindProperty("unmountOnHide"), "Сносить при скрытии"));
            container.Bind(serializedObject);
        }
    }
}
