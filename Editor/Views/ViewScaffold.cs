using System.IO;
using System.Text;
using UnityEditor;
using Exerussus.AppCore.Views;

namespace Exerussus.AppCore.Editor
{
    /// <summary>Создание вёрстки вью по канону: шаблон из <c>Assets/App/Containers</c> или встроенный.</summary>
    public static class ViewScaffold
    {
        /// <summary>Встроенный шаблон страницы/попапа: оба слоя и общие стили, если они есть.</summary>
        public static string DefaultViewUxml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:ac=\"Exerussus.AppCore.Views\" editor-extension-mode=\"False\">");
            AppendStyles(sb);
            sb.AppendLine("    <ac:FullLayer />");
            sb.AppendLine("    <ac:SafeLayer />");
            sb.AppendLine("</ui:UXML>");
            return sb.ToString();
        }

        /// <summary>Встроенный шаблон фрагмента: без слоёв — полноэкранность решает хост.</summary>
        public static string DefaultFragmentUxml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:ac=\"Exerussus.AppCore.Views\" editor-extension-mode=\"False\">");
            AppendStyles(sb);
            sb.AppendLine("    <ui:VisualElement style=\"flex-grow: 1;\" picking-mode=\"Ignore\" />");
            sb.AppendLine("</ui:UXML>");
            return sb.ToString();
        }

        /// <summary>
        /// Создаёт uxml вью по каноническому пути, если его ещё нет.
        /// <c>true</c> — файл на месте (создан сейчас или был).
        /// </summary>
        public static bool EnsureUxml(ViewKind kind, string id, out string path)
        {
            path = AppCorePaths.ViewUxmlPath(kind, id);
            if (File.Exists(path)) return true;

            AppCoreAssets.EnsureFolder(Path.GetDirectoryName(path));

            var template = kind == ViewKind.Fragment
                ? AppCorePaths.DefaultFragmentTemplatePath
                : AppCorePaths.DefaultViewTemplatePath;

            if (File.Exists(template))
            {
                if (!AssetDatabase.CopyAsset(template, path)) return false;
            }
            else
            {
                var content = kind == ViewKind.Fragment ? DefaultFragmentUxml() : DefaultViewUxml();
                File.WriteAllText(path, content, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path);
            }

            return true;
        }

        private static void AppendStyles(StringBuilder sb)
        {
            foreach (var style in new[] { AppCorePaths.AppStyleUssPath, AppCorePaths.TagsUssPath, AppCorePaths.NavigationUssPath })
            {
                if (!File.Exists(style)) continue;
                var guid = AssetDatabase.AssetPathToGUID(style);
                sb.AppendLine($"    <Style src=\"project://database/{style}?fileID=7433441132597879392&amp;guid={guid}&amp;type=3#{Path.GetFileNameWithoutExtension(style)}\" />");
            }
        }
    }
}
