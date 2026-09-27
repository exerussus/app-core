using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Exerussus.AppCore.Editor
{
    /// <summary>
    /// Слияние двух uxml (3.x: полноэкранный и безопасный) в один со слоями
    /// <c>ac:FullLayer</c> и <c>ac:SafeLayer</c>.
    /// </summary>
    public static class UxmlLayerMerge
    {
        public const string ViewsNamespace = "Exerussus.AppCore.Views";
        private const string UiNamespace = "UnityEngine.UIElements";

        private static readonly HashSet<string> HeaderTags = new(StringComparer.Ordinal) { "Style", "Template" };

        /// <summary>
        /// Сливает вёрстку. Любой из аргументов может быть <c>null</c>. Вёрстка, уже разложенная
        /// по слоям, переносится как есть.
        /// </summary>
        public static string Merge(string fullUxml, string safeUxml)
        {
            var full = Parse(fullUxml);
            var safe = Parse(safeUxml);

            var ui = XNamespace.Get(UiNamespace);
            var ac = XNamespace.Get(ViewsNamespace);

            var root = new XElement(ui + "UXML",
                new XAttribute(XNamespace.Xmlns + "ui", UiNamespace),
                new XAttribute(XNamespace.Xmlns + "ac", ViewsNamespace));

            // Прочие объявления namespace и атрибуты корня (editor-extension-mode и т.п.).
            foreach (var source in new[] { full?.Root, safe?.Root })
            {
                if (source == null) continue;

                foreach (var attr in source.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                    {
                        var prefix = attr.Name.LocalName;
                        if (prefix == "ui" || prefix == "ac" || attr.Name == "xmlns") continue;
                        if (root.Attribute(XNamespace.Xmlns + prefix) == null)
                            root.Add(new XAttribute(XNamespace.Xmlns + prefix, attr.Value));
                    }
                    else if (root.Attribute(attr.Name) == null)
                    {
                        root.Add(new XAttribute(attr.Name, attr.Value));
                    }
                }
            }

            // Заголовок: стили и шаблоны обоих файлов без повторов.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in new[] { full?.Root, safe?.Root })
            {
                if (source == null) continue;

                foreach (var el in source.Elements().Where(e => HeaderTags.Contains(e.Name.LocalName)))
                {
                    var key = el.Name.LocalName + "|" + ((string)el.Attribute("src") ?? (string)el.Attribute("path") ?? el.ToString());
                    if (seen.Add(key)) root.Add(new XElement(el));
                }
            }

            AddLayer(root, full?.Root, ac + "FullLayer", "FullLayer");
            AddLayer(root, safe?.Root, ac + "SafeLayer", "SafeLayer");

            return root.ToString() + Environment.NewLine;
        }

        /// <summary>Есть ли в вёрстке слои на верхнем уровне.</summary>
        public static bool HasLayers(string uxml)
        {
            var doc = Parse(uxml);
            return doc?.Root != null && doc.Root.Elements().Any(e => e.Name.LocalName is "FullLayer" or "SafeLayer");
        }

        private static void AddLayer(XElement root, XElement source, XName layerName, string layerLocal)
        {
            if (source == null) return;

            var body = source.Elements().Where(e => !HeaderTags.Contains(e.Name.LocalName)).ToList();
            if (body.Count == 0) return;

            // Уже разложено по слоям — переносим слои как есть, не вкладывая слой в слой.
            if (body.All(e => e.Name.LocalName is "FullLayer" or "SafeLayer"))
            {
                foreach (var el in body) root.Add(new XElement(el));
                return;
            }

            var layer = new XElement(layerName);
            foreach (var el in body)
            {
                if (el.Name.LocalName == layerLocal) foreach (var child in el.Elements()) layer.Add(new XElement(child));
                else layer.Add(new XElement(el));
            }

            root.Add(layer);
        }

        private static XDocument Parse(string uxml)
        {
            if (string.IsNullOrWhiteSpace(uxml)) return null;
            return XDocument.Parse(uxml, LoadOptions.None);
        }
    }
}
