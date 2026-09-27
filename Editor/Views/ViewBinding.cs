using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Жёсткая привязка вью к id: имя GameObject, путь вёрстки, класс и компонент контроллера.
    /// Общая логика инспекторов, валидатора и миграции.
    /// </summary>
    public static class ViewBinding
    {
        public const string IdField = "__id";

        /// <summary>Имя сериализованного поля id у компонента вида.</summary>
        public static string IdPropertyName(ViewKind kind) => kind switch
        {
            ViewKind.Popup => "popupId",
            ViewKind.Fragment => "fragmentId",
            _ => "pageId",
        };

        public const string VisualTreeProperty = "visualTree";
        public const string ControllerProperty = "controller";
        public const string LegacyFullProperty = "legacyFullTree";
        public const string LegacySafeProperty = "legacySafeTree";

        public static ViewKind KindOf(Component view) => view switch
        {
            AppPopup => ViewKind.Popup,
            AppFragment => ViewKind.Fragment,
            _ => ViewKind.Page,
        };

        public static Type ComponentType(ViewKind kind) => kind switch
        {
            ViewKind.Popup => typeof(AppPopup),
            ViewKind.Fragment => typeof(AppFragment),
            _ => typeof(AppPage),
        };

        public static Type ControllerBaseType(ViewKind kind) => kind switch
        {
            ViewKind.Popup => typeof(AppPopupController),
            ViewKind.Fragment => typeof(AppFragmentController),
            _ => typeof(AppPageController),
        };

        public static string GetId(Component view)
        {
            var so = new SerializedObject(view);
            return so.FindProperty(IdPropertyName(KindOf(view)))?.stringValue ?? string.Empty;
        }

        // ============================================================ поиск вью в открытых сценах

        /// <summary>Все вью вида в открытых сценах и в открытом префабе (включая неактивные).</summary>
        public static List<Component> FindViewsInOpenScenes(ViewKind kind)
        {
            var type = ComponentType(kind);
            var result = new List<Component>();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                    result.AddRange(root.GetComponentsInChildren(type, true));
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
                result.AddRange(stage.prefabContentsRoot.GetComponentsInChildren(type, true));

            return result;
        }

        /// <summary>Id, уже занятые другими вью этого вида.</summary>
        public static HashSet<string> UsedIds(ViewKind kind, Component except)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var view in FindViewsInOpenScenes(kind))
            {
                if (view == except) continue;
                var id = GetId(view);
                if (!string.IsNullOrEmpty(id)) used.Add(id);
            }
            return used;
        }

        // ============================================================ контроллер

        /// <summary>Тип контроллера вью по канону имени; <c>null</c>, если класс ещё не скомпилирован.</summary>
        public static Type FindControllerType(ViewKind kind, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            var className = AppNaming.ControllerName(id, kind);
            return TypeCache.GetTypesDerivedFrom(ControllerBaseType(kind))
                .FirstOrDefault(t => !t.IsAbstract && t.Name == className);
        }

        /// <summary>
        /// Приводит вью к канону: имя GameObject, вёрстка по пути, ссылка на компонент контроллера,
        /// если он уже висит на объекте. Ничего не создаёт на диске. <c>true</c> — что-то поменялось.
        /// </summary>
        public static bool Sync(Component view, bool recordUndo)
        {
            var kind = KindOf(view);
            var so = new SerializedObject(view);
            var id = so.FindProperty(IdPropertyName(kind)).stringValue;
            if (string.IsNullOrEmpty(id)) return false;

            var changed = false;

            if (view.gameObject.name != id)
            {
                if (recordUndo) Undo.RecordObject(view.gameObject, "Rename App View");
                view.gameObject.name = id;
                changed = true;
            }

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AppCorePaths.ViewUxmlPath(kind, id));
            var treeProp = so.FindProperty(VisualTreeProperty);
            if (uxml != null && treeProp.objectReferenceValue != uxml)
            {
                treeProp.objectReferenceValue = uxml;
                changed = true;
            }

            var controllerType = FindControllerType(kind, id);
            var controllerProp = so.FindProperty(ControllerProperty);
            if (controllerType != null)
            {
                var existing = view.GetComponent(controllerType);
                if (existing != null && controllerProp.objectReferenceValue != existing)
                {
                    controllerProp.objectReferenceValue = existing;
                    changed = true;
                }
            }

            if (so.hasModifiedProperties)
            {
                if (recordUndo) so.ApplyModifiedProperties();
                else so.ApplyModifiedPropertiesWithoutUndo();
            }

            if (changed) MarkDirty(view);
            return changed;
        }

        /// <summary>Назначает id и сразу приводит вью к канону.</summary>
        public static void AssignId(Component view, string id)
        {
            var kind = KindOf(view);
            Undo.RecordObjects(new UnityEngine.Object[] { view, view.gameObject }, "Assign App View Id");

            var so = new SerializedObject(view);
            so.FindProperty(IdPropertyName(kind)).stringValue = id ?? string.Empty;

            // Смена id — старая вёрстка и контроллер принадлежат другому вью.
            var controller = so.FindProperty(ControllerProperty).objectReferenceValue as Component;
            var expected = FindControllerType(kind, id);
            if (controller != null && (expected == null || controller.GetType() != expected))
                so.FindProperty(ControllerProperty).objectReferenceValue = null;

            so.FindProperty(VisualTreeProperty).objectReferenceValue =
                string.IsNullOrEmpty(id) ? null : AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AppCorePaths.ViewUxmlPath(kind, id));
            so.ApplyModifiedProperties();

            Sync(view, recordUndo: true);
        }

        /// <summary>
        /// Навешивает контроллер: если класс есть — сразу, если нет — генерирует и довешивает
        /// после перекомпиляции (<see cref="PendingControllerAttach"/>).
        /// </summary>
        public static void AttachOrGenerateController(Component view, bool allowGenerate)
        {
            var kind = KindOf(view);
            var id = GetId(view);
            if (string.IsNullOrEmpty(id)) return;

            var type = FindControllerType(kind, id);
            if (type != null)
            {
                // Класс есть, но вторую половину с элементами могли ещё не сгенерировать.
                if (ViewCodeGenerator.GenerateController(kind, id, out _))
                {
                    PendingControllerAttach.Enqueue(view, kind, id);
                    return;
                }

                AttachNow(view, type);
                return;
            }

            if (!allowGenerate) return;

            ViewCodeGenerator.GenerateController(kind, id, out var message);
            PendingControllerAttach.Enqueue(view, kind, id);
            Debug.Log($"[AppCore] Контроллер создан, будет навешан после компиляции: {message}");
        }

        public static void AttachNow(Component view, Type controllerType)
        {
            var component = view.GetComponent(controllerType);
            if (component == null) component = Undo.AddComponent(view.gameObject, controllerType);

            var so = new SerializedObject(view);
            so.FindProperty(ControllerProperty).objectReferenceValue = component;
            so.ApplyModifiedProperties();
            MarkDirty(view);
        }

        public static void MarkDirty(Component view)
        {
            EditorUtility.SetDirty(view);
            if (view.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(view.gameObject.scene);
        }

        // ============================================================ проверка

        /// <summary>Проблемы конкретного вью — для инспектора и валидатора.</summary>
        public static List<string> Validate(Component view)
        {
            var problems = new List<string>();
            var kind = KindOf(view);
            var so = new SerializedObject(view);
            var id = so.FindProperty(IdPropertyName(kind)).stringValue;

            if (so.FindProperty(LegacyFullProperty)?.objectReferenceValue != null ||
                so.FindProperty(LegacySafeProperty)?.objectReferenceValue != null)
                problems.Add("Вёрстка из двух файлов (3.x) — нужна миграция: Exerussus/App/Migrate to 4.0.");

            if (string.IsNullOrEmpty(id))
            {
                problems.Add("Id не выбран.");
                return problems;
            }

            if (!AppCoreAssets.GetIds(kind).Contains(id))
                problems.Add($"Id «{id}» не зарегистрирован в NavigationSettings.");

            if (view.gameObject.name != id)
                problems.Add($"Имя GameObject «{view.gameObject.name}» не совпадает с id.");

            var uxmlPath = AppCorePaths.ViewUxmlPath(kind, id);
            var tree = so.FindProperty(VisualTreeProperty).objectReferenceValue;
            if (!File.Exists(uxmlPath)) problems.Add($"Нет вёрстки: {uxmlPath}.");
            else if (tree == null || AssetDatabase.GetAssetPath(tree) != uxmlPath)
                problems.Add($"Вёрстка должна быть {uxmlPath}.");

            if (File.Exists(uxmlPath))
            {
                try
                {
                    ViewCodeGenerator.ReadNamedElements(File.ReadAllText(uxmlPath), out var duplicates, out _);
                    if (duplicates.Count > 0)
                        problems.Add("Повторяющиеся имена элементов: " + string.Join(", ", duplicates) + ".");
                }
                catch (Exception e)
                {
                    problems.Add($"Вёрстка не разбирается: {e.Message}");
                }
            }

            var controller = so.FindProperty(ControllerProperty).objectReferenceValue as Component;
            var expectedName = AppNaming.ControllerName(id, kind);
            if (controller != null)
            {
                if (controller.GetType().Name != expectedName)
                    problems.Add($"Контроллер должен называться {expectedName}, а не {controller.GetType().Name}.");
                if (controller.gameObject != view.gameObject)
                    problems.Add("Контроллер должен висеть на том же GameObject, что и вью.");

                var script = MonoScript.FromMonoBehaviour(controller as MonoBehaviour);
                var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                var expectedPath = AppCorePaths.ControllerPath(kind, id);
                if (scriptPath != null && scriptPath != expectedPath)
                    problems.Add($"Файл контроллера должен лежать в {expectedPath}.");
            }

            return problems;
        }
    }

    /// <summary>
    /// Довешивание сгенерированного контроллера после перекомпиляции. Намерение переживает
    /// перезагрузку домена в <see cref="SessionState"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class PendingControllerAttach
    {
        private const string Key = "Exerussus.AppCore.PendingControllerAttach";

        [Serializable]
        private sealed class Entry
        {
            public string objectId;
            public ViewKind kind;
            public string id;
        }

        [Serializable]
        private sealed class Queue
        {
            public List<Entry> items = new();
        }

        static PendingControllerAttach()
        {
            EditorApplication.delayCall += Process;
        }

        public static void Enqueue(Component view, ViewKind kind, string id)
        {
            var queue = Load();
            var objectId = GlobalObjectId.GetGlobalObjectIdSlow(view).ToString();
            queue.items.RemoveAll(e => e.objectId == objectId);
            queue.items.Add(new Entry { objectId = objectId, kind = kind, id = id });
            SessionState.SetString(Key, JsonUtility.ToJson(queue));
        }

        private static Queue Load()
        {
            var json = SessionState.GetString(Key, string.Empty);
            if (string.IsNullOrEmpty(json)) return new Queue();
            try { return JsonUtility.FromJson<Queue>(json) ?? new Queue(); }
            catch { return new Queue(); }
        }

        private static void Process()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Process;
                return;
            }

            var queue = Load();
            if (queue.items.Count == 0) return;

            var left = new Queue();
            foreach (var entry in queue.items)
            {
                var type = ViewBinding.FindControllerType(entry.kind, entry.id);
                if (type == null)
                {
                    // Класс ещё не скомпилирован (или компиляция упала) — ждём следующей загрузки.
                    left.items.Add(entry);
                    continue;
                }

                if (!GlobalObjectId.TryParse(entry.objectId, out var gid)) continue;
                if (GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) is not Component view) continue;

                ViewBinding.AttachNow(view, type);
                Debug.Log($"[AppCore] Контроллер {type.Name} навешан на «{view.gameObject.name}».", view);
            }

            if (left.items.Count == 0) SessionState.EraseString(Key);
            else SessionState.SetString(Key, JsonUtility.ToJson(left));
        }
    }
}
