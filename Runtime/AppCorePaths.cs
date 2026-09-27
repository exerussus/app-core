using System.Text;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore
{
    /// <summary>
    /// Фиксированная раскладка папки <c>Assets/App</c>. Папка целиком принадлежит AppCore:
    /// всё, что в ней лежит, создаёт, находит и проверяет плагин, поэтому пути здесь —
    /// константы, а не настройка.
    /// </summary>
    public static class AppCorePaths
    {
        public const string Root = "Assets/App";

        public const string SettingsDir = Root + "/Settings";
        public const string PagesDir = Root + "/Pages";
        public const string PopupsDir = Root + "/Popups";
        public const string FragmentsDir = Root + "/Fragments";
        public const string StylesDir = Root + "/Styles";
        public const string ContainersDir = Root + "/Containers";
        public const string UIToolkitDir = Root + "/UIToolkit";
        public const string GeneratedDir = Root + "/Generated";

        public const string NavigationSettingsPath = SettingsDir + "/NavigationSettings.asset";
        public const string BuildInfoPath = SettingsDir + "/BuildInfo.asset";
        public const string TextSettingsPath = SettingsDir + "/TextSettings.asset";
        public const string BuildConfigPath = SettingsDir + "/build-config.json";

        public const string NavigationUssPath = StylesDir + "/Navigation.uss";
        public const string TagsUssPath = StylesDir + "/Tags.uss";
        public const string AppStyleUssPath = StylesDir + "/AppStyle.uss";
        public const string AppThemePath = UIToolkitDir + "/AppTheme.tss";
        public const string PanelSettingsPath = UIToolkitDir + "/App Settings.asset";
        public const string PanelTextSettingsPath = UIToolkitDir + "/App Text Settings.asset";

        public const string DefaultViewTemplatePath = ContainersDir + "/Default.uxml";
        public const string DefaultFragmentTemplatePath = ContainersDir + "/Fragment.uxml";
        public const string LoadingScreenPath = ContainersDir + "/LoadingScreen.uxml";

        public const string NavTargetsPath = GeneratedDir + "/NavTargets.cs";

        /// <summary>Namespace сгенерированных id (<c>NavTargets</c>).</summary>
        public const string GeneratedNamespace = "App.Navigation";

        /// <summary>Namespace сгенерированных контроллеров.</summary>
        public static string ControllersNamespace(ViewKind kind) => kind switch
        {
            ViewKind.Popup => "App.Popups",
            ViewKind.Fragment => "App.Fragments",
            _ => "App.Pages",
        };

        /// <summary>Корневая папка вью указанного вида.</summary>
        public static string KindDir(ViewKind kind) => kind switch
        {
            ViewKind.Popup => PopupsDir,
            ViewKind.Fragment => FragmentsDir,
            _ => PagesDir,
        };

        /// <summary>Папка конкретного вью: <c>Assets/App/Pages/MainMenu</c>.</summary>
        public static string ViewDir(ViewKind kind, string id) => $"{KindDir(kind)}/{AppNaming.TypeName(id, kind)}";

        /// <summary>Uxml вью: <c>Assets/App/Pages/MainMenu/main_menu.uxml</c>.</summary>
        public static string ViewUxmlPath(ViewKind kind, string id) => $"{ViewDir(kind, id)}/{AppNaming.FileName(id)}.uxml";

        /// <summary>Файл контроллера: <c>Assets/App/Pages/MainMenu/MainMenuPageController.cs</c>.</summary>
        public static string ControllerPath(ViewKind kind, string id) => $"{ViewDir(kind, id)}/{AppNaming.ControllerName(id, kind)}.cs";

        /// <summary>Сгенерированная половина контроллера с полями элементов.</summary>
        public static string ControllerElementsPath(ViewKind kind, string id) => $"{ViewDir(kind, id)}/{AppNaming.ControllerName(id, kind)}.Elements.cs";
    }

    /// <summary>Единые правила имён, выводимых из id вью.</summary>
    public static class AppNaming
    {
        /// <summary>id → PascalCase: <c>game_over</c> → <c>GameOver</c>. Разделители отбрасываются.</summary>
        public static string TypeName(string id, ViewKind kind = ViewKind.Page)
        {
            var sb = new StringBuilder();
            var up = true;

            foreach (var ch in id ?? string.Empty)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(up ? char.ToUpperInvariant(ch) : ch);
                    up = false;
                }
                else
                {
                    up = true;
                }
            }

            if (sb.Length == 0) return "Unnamed" + kind;
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        /// <summary>Суффикс контроллера: <c>PageController</c> / <c>PopupController</c> / <c>FragmentController</c>.</summary>
        public static string ControllerSuffix(ViewKind kind) => kind switch
        {
            ViewKind.Popup => "PopupController",
            ViewKind.Fragment => "FragmentController",
            _ => "PageController",
        };

        /// <summary>Имя класса контроллера: <c>MainMenuPageController</c>.</summary>
        public static string ControllerName(string id, ViewKind kind) => TypeName(id, kind) + ControllerSuffix(kind);

        /// <summary>Имя файла вёрстки без расширения: сам id, без запрещённых в имени файла символов.</summary>
        public static string FileName(string id)
        {
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0) return "view";

            var sb = new StringBuilder(id.Length);
            foreach (var ch in id)
                sb.Append(ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : ch);
            return sb.ToString();
        }
    }
}
