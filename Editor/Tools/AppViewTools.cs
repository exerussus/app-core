using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Exerussus.AppCore.Navigation;
using Exerussus.AppCore.Views;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Операции над вью и раннером без окон и диалогов — для инструментов (CLI Nexus, автоматизация):
    /// создать вью целиком (id → uxml → контроллер → объект в сцене под <see cref="AppRunner"/>), удалить,
    /// перегенерировать, проверить, расставить объекты раннера. Ошибка — <see cref="AppToolException"/>.
    /// </summary>
    public static class AppViewTools
    {
        // ============================================================ отчёт

        /// <summary>Что сделала операция: строки для человека, предупреждения, нужна ли компиляция.</summary>
        public sealed class Report
        {
            public readonly List<string> Lines = new();
            public readonly List<string> Warnings = new();

            /// <summary>На диске изменился код (NavTargets, контроллер) — будет компиляция и перезагрузка домена.</summary>
            public bool NeedsCompile;

            /// <summary>Сцена изменена и не сохранена.</summary>
            public bool SceneDirty;

            public string ScenePath;
            public string ObjectPath;

            public void Add(string line) => Lines.Add(line);
            public void Warn(string line) => Warnings.Add(line);
        }

        public sealed class CreateOptions
        {
            /// <summary>Сгенерировать контроллер и навесить его (после компиляции).</summary>
            public bool Controller;

            /// <summary>Поставить объект в сцену под раннер (фрагмент — под вью-владельца).</summary>
            public bool PlaceInScene = true;

            /// <summary>Фрагмент: id страницы или попапа-владельца.</summary>
            public string Owner;

            /// <summary>Фрагмент: id хоста (<c>FragmentHost host-id</c>) в вёрстке владельца.</summary>
            public string HostId;

            /// <summary>Фрагмент: размонтировать при скрытии.</summary>
            public bool UnmountOnHide;

            /// <summary>Страница: сделать стартовой (первой под раннером).</summary>
            public bool MakeDefault;

            /// <summary>Сохранить сцену после изменений.</summary>
            public bool SaveScene;
        }

        public sealed class DeleteOptions
        {
            /// <summary>Удалить папку вью (uxml, контроллер, .Elements.cs).</summary>
            public bool Files = true;

            /// <summary>Удалить даже с посторонними файлами в папке вью.</summary>
            public bool Force;

            /// <summary>Убрать id из реестра.</summary>
            public bool Unregister = true;

            /// <summary>Снять навигационный класс со всех uxml.</summary>
            public bool StripClasses;

            /// <summary>Фрагмент: только у этого владельца.</summary>
            public string Owner;

            public bool SaveScene;
        }

        public sealed class ViewInfo
        {
            public ViewKind Kind;
            public string Id;
            public bool Registered;
            public string Uxml;
            public bool UxmlExists;
            public bool ControllerExists;
            public bool ControllerCompiled;
            public List<string> SceneObjects = new();
        }

        // ============================================================ раннер

        /// <summary>Раннер в открытых сценах. Нет или больше одного — исключение с пояснением.</summary>
        public static AppRunner FindRunner()
        {
            var runners = Object.FindObjectsByType<AppRunner>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(r => r != null && r.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(r)).ToList();
            if (runners.Count == 1) return runners[0];
            if (runners.Count == 0) throw new AppToolException("В открытых сценах нет AppRunner — создайте: app.runner.create (или откройте сцену приложения).");
            throw new AppToolException("AppRunner в открытых сценах несколько: " + string.Join(", ", runners.Select(PathOf)) + ".");
        }

        public static AppRunner TryFindRunner()
        {
            try
            {
                return FindRunner();
            }
            catch (AppToolException)
            {
                return null;
            }
        }

        /// <summary>Создать объект «App» с AppRunner (PanelRenderer добавляется сам) в активной сцене.</summary>
        public static AppRunner CreateRunner(string name, Report report)
        {
            if (TryFindRunner() != null) throw new AppToolException("AppRunner уже есть в сцене: " + PathOf(FindRunner()));

            var go = new GameObject(string.IsNullOrWhiteSpace(name) ? "App" : name.Trim());
            Undo.RegisterCreatedObjectUndo(go, "Create AppRunner");
            var runner = Undo.AddComponent<AppRunner>(go);

            var panel = AssetDatabase.LoadMainAssetAtPath(AppCorePaths.PanelSettingsPath);
            if (panel != null)
            {
                if (!AssignFirstReference(go, panel)) report.Warn("Panel Settings не назначены автоматически — назначьте в PanelRenderer: " + AppCorePaths.PanelSettingsPath);
            }
            else
            {
                report.Warn("Нет " + AppCorePaths.PanelSettingsPath + " — назначьте Panel Settings в PanelRenderer.");
            }

            MarkSceneDirty(go, report);
            report.ObjectPath = PathOf(runner);
            report.Add("✓ AppRunner: " + report.ObjectPath);
            return runner;
        }

        /// <summary>Поля раннера, которые можно расставить объектами сцены.</summary>
        public static readonly string[] RunnerObjectFields =
            { "bootstrapper", "inputAdapter", "appServiceRegistry", "loadingScreen", "errorScreen", "criticalScreen" };

        /// <summary>
        /// Поставить объект в поле раннера: найти компонент нужного типа под раннером или создать дочерний объект
        /// с ним и назначить. Абстрактный тип поля (бутстраппер, адаптер ввода, реестр сервисов) — нужен
        /// <paramref name="typeName"/> наследника.
        /// </summary>
        public static Component SetRunnerObject(string field, string typeName, Report report)
        {
            var runner = FindRunner();
            var info = RunnerField(field);
            var type = ResolveComponentType(info.FieldType, typeName);

            var component = runner.GetComponentInChildren(type, true);
            if (component == null)
            {
                var go = new GameObject(type.Name);
                Undo.RegisterCreatedObjectUndo(go, "Create App Object");
                Undo.SetTransformParent(go.transform, runner.transform, "Create App Object");
                component = Undo.AddComponent(go, type);
                report.Add($"создан {PathOf(component)} ({type.Name})");
            }
            else
            {
                report.Add($"найден {PathOf(component)} ({type.Name})");
            }

            // экран загрузки: своя вёрстка по канону, если поле пустое
            if (component is Exerussus.AppCore.Screens.LoadingScreen && File.Exists(AppCorePaths.LoadingScreenPath))
                AssignIfEmpty(component, "visualTree", AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AppCorePaths.LoadingScreenPath), report);

            var so = new SerializedObject(runner);
            var prop = so.FindProperty(info.Name);
            if (prop == null) throw new AppToolException($"Поле раннера «{info.Name}» не сериализуется.");
            prop.objectReferenceValue = component;
            so.ApplyModifiedProperties();
            MarkSceneDirty(runner, report);
            report.Add($"✓ AppRunner.{info.Name} = {type.Name}");
            return component;
        }

        private static FieldInfo RunnerField(string field)
        {
            foreach (var name in RunnerObjectFields)
            {
                if (!string.Equals(name, field, StringComparison.OrdinalIgnoreCase)) continue;
                var f = typeof(AppRunner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f;
            }

            throw new AppToolException($"Поле «{field}» не из списка: {string.Join(", ", RunnerObjectFields)}.");
        }

        private static Type ResolveComponentType(Type baseType, string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                if (!baseType.IsAbstract) return baseType;
                var candidates = TypeCache.GetTypesDerivedFrom(baseType).Where(t => !t.IsAbstract && !t.IsGenericType).ToList();
                if (candidates.Count == 1) return candidates[0];
                throw new AppToolException(candidates.Count == 0
                    ? $"Тип {baseType.Name} абстрактный, наследников нет — создайте класс-наследник."
                    : $"Тип {baseType.Name} абстрактный — укажите наследника: {string.Join(", ", candidates.Select(t => t.Name))}.");
            }

            var all = TypeCache.GetTypesDerivedFrom(baseType).Append(baseType)
                .Where(t => !t.IsAbstract && (t.Name == typeName || t.FullName == typeName)).Distinct().ToList();
            if (all.Count == 1) return all[0];
            if (all.Count == 0) throw new AppToolException($"Нет неабстрактного типа «{typeName}», наследника {baseType.Name}.");
            throw new AppToolException($"«{typeName}» неоднозначен: {string.Join(", ", all.Select(t => t.FullName))}.");
        }

        // ============================================================ вью: создание

        /// <summary>
        /// Создать вью целиком: id в реестре (+ NavTargets, Navigation.uss), uxml по шаблону, по желанию
        /// контроллер, объект в сцене с привязкой к id. Уже существующие части не трогаются.
        /// </summary>
        public static Report CreateView(ViewKind kind, string id, CreateOptions options)
        {
            options ??= new CreateOptions();
            id = (id ?? string.Empty).Trim();
            var report = new Report();

            // проверки — до любых изменений
            if (kind == ViewKind.Fragment && options.PlaceInScene && string.IsNullOrWhiteSpace(options.Owner))
                throw new AppToolException("Фрагменту нужен владелец: id страницы или попапа (--in).");
            if (options.MakeDefault && kind != ViewKind.Page) throw new AppToolException("Стартовой может быть только страница.");

            var registered = AppCoreAssets.GetIds(kind).Contains(id);
            if (!registered)
            {
                var problem = AppNavigation.CheckNewId(kind, id);
                if (problem != null) throw new AppToolException(problem);
            }

            Transform parent = null;
            if (options.PlaceInScene)
            {
                var runner = FindRunner();
                parent = kind == ViewKind.Fragment ? FindOwner(options.Owner).transform : runner.transform;
            }

            // 1. реестр
            if (!registered)
            {
                AppNavigation.Register(kind, id);
                report.NeedsCompile = true;
                report.Add($"✓ id «{id}» зарегистрирован → NavTargets.{AppNavigation.NavTargetName(kind, id)}");
            }
            else
            {
                report.Add($"id «{id}» уже зарегистрирован");
            }

            // 2. вёрстка
            var uxmlExisted = File.Exists(AppCorePaths.ViewUxmlPath(kind, id));
            if (!ViewScaffold.EnsureUxml(kind, id, out var uxmlPath)) throw new AppToolException("Не удалось создать вёрстку " + uxmlPath);
            report.Add((uxmlExisted ? "вёрстка есть: " : "✓ вёрстка: ") + uxmlPath);

            // 3. объект в сцене
            Component view = null;
            if (parent != null)
            {
                view = FindView(kind, id, kind == ViewKind.Fragment ? parent : null);
                if (view != null)
                {
                    report.Add("объект есть: " + PathOf(view));
                }
                else
                {
                    var go = new GameObject(id);
                    Undo.RegisterCreatedObjectUndo(go, "Create App View");
                    Undo.SetTransformParent(go.transform, parent, "Create App View");
                    go.transform.localPosition = Vector3.zero;
                    view = Undo.AddComponent(go, ViewBinding.ComponentType(kind));
                    ViewBinding.AssignId(view, id);
                    report.Add("✓ объект: " + PathOf(view));
                }

                if (kind == ViewKind.Fragment)
                {
                    var so = new SerializedObject(view);
                    if (!string.IsNullOrWhiteSpace(options.HostId)) so.FindProperty("hostId").stringValue = options.HostId.Trim();
                    so.FindProperty("unmountOnHide").boolValue = options.UnmountOnHide;
                    so.ApplyModifiedProperties();
                }

                if (options.MakeDefault)
                {
                    Undo.SetSiblingIndex(view.transform, 0, "Default App Page");
                    report.Add("✓ стартовая страница (первая под раннером)");
                }

                ViewBinding.Sync(view, recordUndo: true);
                MarkSceneDirty(view, report);
                report.ObjectPath = PathOf(view);
            }

            // 4. контроллер
            if (options.Controller) AddController(kind, id, view, report);

            if (options.SaveScene) SaveScene(view != null ? (Component)view : null, report);
            return report;
        }

        /// <summary>Сгенерировать контроллер (если нет) и навесить на объект вью (сразу или после компиляции).</summary>
        public static void AddController(ViewKind kind, string id, Component view, Report report)
        {
            var path = AppCorePaths.ControllerPath(kind, id);
            var existed = File.Exists(path);
            view ??= FindView(kind, id, null);

            if (view != null)
            {
                var compiled = ViewBinding.FindControllerType(kind, id) != null;
                ViewBinding.AttachOrGenerateController(view, allowGenerate: true);
                if (compiled && File.Exists(AppCorePaths.ControllerElementsPath(kind, id)))
                {
                    report.Add($"✓ контроллер {AppNaming.ControllerName(id, kind)} навешан");
                }
                else
                {
                    report.NeedsCompile = true;
                    report.Add($"✓ контроллер {(existed ? "есть" : "создан")}: {path} — навесится после компиляции");
                }

                MarkSceneDirty(view, report);
                return;
            }

            if (ViewCodeGenerator.GenerateController(kind, id, out var message)) report.NeedsCompile = true;
            report.Add((existed ? "контроллер есть: " : "✓ контроллер: ") + message);
            report.Warn("Объекта вью в сцене нет — контроллер не навешан (создайте объект: app.create … без --no-scene).");
        }

        // ============================================================ вью: удаление

        /// <summary>Удалить вью: объекты в сцене, папку вью, id из реестра; ссылки в коде — в предупреждения.</summary>
        public static Report DeleteView(ViewKind kind, string id, DeleteOptions options)
        {
            options ??= new DeleteOptions();
            id = (id ?? string.Empty).Trim();
            var report = new Report();
            var dir = AppCorePaths.ViewDir(kind, id);

            var registered = AppCoreAssets.GetIds(kind).Contains(id);
            var views = ViewBinding.FindViewsInOpenScenes(kind).Where(v => ViewBinding.GetId(v) == id).ToList();
            if (kind == ViewKind.Fragment && !string.IsNullOrWhiteSpace(options.Owner))
            {
                var owner = FindOwner(options.Owner).transform;
                views = views.Where(v => v.transform.IsChildOf(owner)).ToList();
            }

            if (!registered && views.Count == 0 && !AssetDatabase.IsValidFolder(dir))
                throw new AppToolException($"Вью {kind} «{id}» нет: ни в реестре, ни в сцене, ни на диске ({dir}).");

            // посторонние файлы в папке вью — только с Force
            List<string> extra = null;
            if (options.Files && AssetDatabase.IsValidFolder(dir))
            {
                extra = ForeignFiles(kind, id, dir);
                if (extra.Count > 0 && !options.Force)
                    throw new AppToolException($"В {dir} есть не только файлы вью: {string.Join(", ", extra)} — удалите их сами или добавьте --force.");
            }

            // ссылки в коде — до удаления: после него проект может не собраться
            foreach (var hit in FindCodeReferences(kind, id, dir)) report.Warn("ссылка в коде: " + hit);

            // 1. сцена (фрагменты внутри удаляемой страницы уходят вместе с ней)
            foreach (var view in views)
            {
                if (view == null) continue;
                var path = PathOf(view);
                var nested = view.GetComponentsInChildren<AppFragment>(true).Where(f => f != view).Select(f => f.gameObject.name).ToList();
                var scene = view.gameObject.scene;
                Undo.DestroyObjectImmediate(view.gameObject);
                if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
                report.SceneDirty = true;
                report.Add("✓ удалён объект " + path + (nested.Count > 0 ? $" (с фрагментами: {string.Join(", ", nested)})" : string.Empty));
            }

            // 2. файлы
            if (options.Files && AssetDatabase.IsValidFolder(dir))
            {
                var hadCode = File.Exists(AppCorePaths.ControllerPath(kind, id));
                if (!AssetDatabase.DeleteAsset(dir)) throw new AppToolException("Не удалось удалить " + dir);
                if (hadCode) report.NeedsCompile = true;
                report.Add("✓ удалена папка " + dir + (extra != null && extra.Count > 0 ? $" (вместе с: {string.Join(", ", extra)})" : string.Empty));
            }

            // 3. реестр
            if (options.Unregister && registered)
            {
                var s = AppCoreAssets.FindNavigationSettings();
                var cls = AppNavigation.ClassOf(s, kind, id);
                var uses = cls != null && !options.StripClasses ? AppNavigation.CountClassUses(cls) : 0;
                AppNavigation.Unregister(kind, id, options.StripClasses, out var stripped);
                report.NeedsCompile = true;
                report.Add($"✓ id «{id}» убран из реестра (NavTargets, Navigation.uss)");
                if (stripped > 0) report.Add($"✓ класс .{cls} снят в {stripped} uxml");
                if (uses > 0) report.Warn($"класс .{cls} остался на {uses} элемент(ах) uxml — снять: --strip-classes");
            }

            if (options.SaveScene && report.SceneDirty) SaveScene(null, report);
            return report;
        }

        private static List<string> ForeignFiles(ViewKind kind, string id, string dir)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFileName(AppCorePaths.ViewUxmlPath(kind, id)),
                Path.GetFileName(AppCorePaths.ControllerPath(kind, id)),
                Path.GetFileName(AppCorePaths.ControllerElementsPath(kind, id)),
            };

            var extra = new List<string>();
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (!known.Contains(name)) extra.Add(file.Replace('\\', '/').Substring(dir.Length + 1));
            }

            return extra;
        }

        /// <summary>Использования <c>NavTargets.X_Page</c> и класса контроллера в коде проекта (кроме папки вью).</summary>
        public static List<string> FindCodeReferences(ViewKind kind, string id, string excludeDir = null)
        {
            var hits = new List<string>();
            var target = new Regex(@"\bNavTargets\s*\.\s*" + Regex.Escape(AppNavigation.NavTargetName(kind, id)) + @"\b");
            var controller = new Regex(@"\b" + Regex.Escape(AppNaming.ControllerName(id, kind)) + @"\b");
            var exclude = excludeDir?.Replace('\\', '/').TrimEnd('/') + "/";

            foreach (var file in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories))
            {
                var path = file.Replace('\\', '/');
                if (exclude != null && path.StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(path, AppCorePaths.NavTargetsPath, StringComparison.OrdinalIgnoreCase)) continue;

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(path);
                }
                catch (IOException)
                {
                    continue;
                }

                for (var i = 0; i < lines.Length; i++)
                {
                    if (!target.IsMatch(lines[i]) && !controller.IsMatch(lines[i])) continue;
                    hits.Add($"{path}:{i + 1}");
                    if (hits.Count >= 20) return hits;
                }
            }

            return hits;
        }

        // ============================================================ страница: настройки

        /// <summary>Настройки страницы: «Назад» (none | previous | page:&lt;id&gt;), курсор, стартовая.</summary>
        public static Report SetPageOptions(string id, string back, string cursor, bool makeDefault)
        {
            var report = new Report();
            var view = FindView(ViewKind.Page, id, null) ?? throw new AppToolException($"Страницы «{id}» нет в открытых сценах.");
            var so = new SerializedObject(view);

            if (!string.IsNullOrWhiteSpace(back))
            {
                var b = back.Trim();
                if (b.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
                {
                    var target = b.Substring(5).Trim();
                    if (!AppCoreAssets.GetIds(ViewKind.Page).Contains(target)) throw new AppToolException($"Страница «{target}» не зарегистрирована.");
                    so.FindProperty("backAction").enumValueIndex = (int)PageBackAction.ToPage;
                    so.FindProperty("backPageId").stringValue = target;
                }
                else if (Enum.TryParse<PageBackAction>(b, true, out var action) && action != PageBackAction.ToPage)
                {
                    so.FindProperty("backAction").enumValueIndex = (int)action;
                }
                else
                {
                    throw new AppToolException("--back: none, previous или page:<id>");
                }

                report.Add("✓ «Назад»: " + b);
            }

            if (!string.IsNullOrWhiteSpace(cursor))
            {
                if (!Enum.TryParse<PageCursorMode>(cursor.Trim(), true, out var mode)) throw new AppToolException("--cursor: keep, lock или unlock");
                so.FindProperty("cursorMode").enumValueIndex = (int)mode;
                report.Add("✓ курсор: " + mode);
            }

            so.ApplyModifiedProperties();

            if (makeDefault)
            {
                Undo.SetSiblingIndex(view.transform, 0, "Default App Page");
                report.Add("✓ стартовая страница");
            }

            MarkSceneDirty(view, report);
            return report;
        }

        // ============================================================ обзор, генерация, проверка

        /// <summary>Все вью: зарегистрированные и найденные в сцене, с состоянием файлов.</summary>
        public static List<ViewInfo> ListViews()
        {
            var list = new List<ViewInfo>();
            foreach (ViewKind kind in Enum.GetValues(typeof(ViewKind)))
            {
                var byId = new Dictionary<string, ViewInfo>(StringComparer.Ordinal);
                foreach (var id in AppCoreAssets.GetIds(kind)) byId[id] = Info(kind, id, true);
                foreach (var view in ViewBinding.FindViewsInOpenScenes(kind))
                {
                    var id = ViewBinding.GetId(view);
                    if (string.IsNullOrEmpty(id)) id = "(без id) " + view.gameObject.name;
                    if (!byId.TryGetValue(id, out var info)) byId[id] = info = Info(kind, id, false);
                    info.SceneObjects.Add(PathOf(view));
                }

                list.AddRange(byId.Values);
            }

            return list;
        }

        private static ViewInfo Info(ViewKind kind, string id, bool registered) => new()
        {
            Kind = kind,
            Id = id,
            Registered = registered,
            Uxml = AppCorePaths.ViewUxmlPath(kind, id),
            UxmlExists = File.Exists(AppCorePaths.ViewUxmlPath(kind, id)),
            ControllerExists = File.Exists(AppCorePaths.ControllerPath(kind, id)),
            ControllerCompiled = ViewBinding.FindControllerType(kind, id) != null,
        };

        /// <summary>
        /// Перегенерировать всё выводимое: навигацию (записи, Navigation.uss, NavTargets), половины контроллеров
        /// с элементами, привязку вью открытых сцен к канону.
        /// </summary>
        public static Report GenerateAll()
        {
            var report = new Report();
            var navTargetsBefore = File.Exists(AppCorePaths.NavTargetsPath) ? File.ReadAllText(AppCorePaths.NavTargetsPath) : null;
            report.Add(AppNavigation.Regenerate());
            var navTargetsAfter = File.Exists(AppCorePaths.NavTargetsPath) ? File.ReadAllText(AppCorePaths.NavTargetsPath) : null;
            if (navTargetsBefore != navTargetsAfter) report.NeedsCompile = true;

            var elements = 0;
            foreach (ViewKind kind in Enum.GetValues(typeof(ViewKind)))
                foreach (var id in AppCoreAssets.GetIds(kind))
                    if (ViewCodeGenerator.RegenerateElements(kind, id, out _))
                        elements++;
            if (elements > 0) report.NeedsCompile = true;
            report.Add($"✓ .Elements.cs обновлено: {elements}");

            var synced = 0;
            foreach (ViewKind kind in Enum.GetValues(typeof(ViewKind)))
                foreach (var view in ViewBinding.FindViewsInOpenScenes(kind))
                    if (!string.IsNullOrEmpty(ViewBinding.GetId(view)) && ViewBinding.Sync(view, recordUndo: true))
                    {
                        synced++;
                        report.SceneDirty = true;
                    }

            report.Add($"✓ вью приведено к канону: {synced}");
            return report;
        }

        // ============================================================ общее

        /// <summary>Вью вида с id в открытых сценах (фрагмент — внутри <paramref name="within"/>, если задан).</summary>
        public static Component FindView(ViewKind kind, string id, Transform within)
        {
            foreach (var view in ViewBinding.FindViewsInOpenScenes(kind))
            {
                if (ViewBinding.GetId(view) != id) continue;
                if (within != null && !view.transform.IsChildOf(within)) continue;
                return view;
            }

            return null;
        }

        /// <summary>Владелец фрагмента: страница, иначе попап с этим id.</summary>
        public static Component FindOwner(string ownerId)
        {
            var id = (ownerId ?? string.Empty).Trim();
            var page = FindView(ViewKind.Page, id, null);
            var popup = FindView(ViewKind.Popup, id, null);
            if (page != null && popup != null) throw new AppToolException($"«{id}» — и страница, и попап: укажите владельца однозначно (page:{id} или popup:{id}).");
            if (id.StartsWith("page:", StringComparison.OrdinalIgnoreCase)) page = FindView(ViewKind.Page, id.Substring(5), null);
            if (id.StartsWith("popup:", StringComparison.OrdinalIgnoreCase)) popup = FindView(ViewKind.Popup, id.Substring(6), null);
            return page != null ? page : popup != null ? popup : throw new AppToolException($"Владелец «{id}» (страница или попап) не найден в открытых сценах.");
        }

        public static string PathOf(Component c)
        {
            if (c == null) return "(нет)";
            var t = c.transform;
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }

            return c.gameObject.scene.name + ":" + path;
        }

        private static void MarkSceneDirty(Component c, Report report) => MarkSceneDirty(c != null ? c.gameObject : null, report);

        private static void MarkSceneDirty(GameObject go, Report report)
        {
            if (go == null || !go.scene.IsValid()) return;
            EditorSceneManager.MarkSceneDirty(go.scene);
            report.SceneDirty = true;
            report.ScenePath = go.scene.path;
        }

        private static void SaveScene(Component c, Report report)
        {
            var scene = c != null ? c.gameObject.scene : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                report.Warn("Сцена без пути — не сохранена (сохраните её с именем).");
                return;
            }

            if (EditorSceneManager.SaveScene(scene))
            {
                report.SceneDirty = false;
                report.Add("✓ сцена сохранена: " + scene.path);
            }
            else
            {
                report.Warn("Сцена не сохранена: " + scene.path);
            }
        }

        private static void AssignIfEmpty(Component component, string property, Object value, Report report)
        {
            if (value == null) return;
            var so = new SerializedObject(component);
            var prop = so.FindProperty(property);
            if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference || prop.objectReferenceValue != null) return;
            prop.objectReferenceValue = value;
            so.ApplyModifiedProperties();
            report.Add($"✓ {component.GetType().Name}.{property} = {AssetDatabase.GetAssetPath(value)}");
        }

        /// <summary>Назначить ассет в первое пустое подходящее по типу поле-ссылку компонентов объекта.</summary>
        private static bool AssignFirstReference(GameObject go, Object asset)
        {
            var typeName = asset.GetType().Name;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component is AppRunner) continue;
                var so = new SerializedObject(component);
                var it = so.GetIterator();
                while (it.NextVisible(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (it.type != $"PPtr<{typeName}>" && it.type != $"PPtr<${typeName}>") continue;
                    it.objectReferenceValue = asset;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Операция инструмента не выполнена: сообщение — для человека.</summary>
    public sealed class AppToolException : Exception
    {
        public AppToolException(string message) : base(message)
        {
        }
    }
}
