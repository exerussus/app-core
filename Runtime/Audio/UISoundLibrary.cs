using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;
#if UNITY_EDITOR
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
#endif

namespace Exerussus.AppCore.Audio
{
    [CreateAssetMenu(fileName = "UIAudioLibrary", menuName = "UI/Audio Library")]
    public class UISoundLibrary : ScriptableObject
    {
        public string defaultButton;
        public Match[] matches;

        [Serializable]
        public struct Match
        {
            public string className;
            public string sound;
        }

#if UNITY_EDITOR
        private const string TagProperty   = "--ui-tag"; // служебное USS-свойство
        private const string AudioTagValue = "audio";    // какие классы тащим сюда

        private static readonly Regex CommentRegex = new Regex(@"/\*.*?\*/", RegexOptions.Singleline);
        private static readonly Regex ClassRegex   = new Regex(@"\.(-?[_a-zA-Z][_a-zA-Z0-9-]*)");
        private static readonly Regex TagRegex     =
            new Regex(Regex.Escape(TagProperty) + @"\s*:\s*""?([_a-zA-Z][_a-zA-Z0-9-]*)""?");

        [PropertyOrder(-1)]
        [OnInspectorGUI]
        private void DrawUssDropZone()
        {
            var rect = GUILayoutUtility.GetRect(0f, 46f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, $"Перетащите .uss сюда (тег: {AudioTagValue})", EditorStyles.helpBox);

            var evt = Event.current;
            if (!rect.Contains(evt.mousePosition)) return;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;

            var ussPaths = CollectUssPaths();
            DragAndDrop.visualMode = ussPaths.Count > 0
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;

            if (evt.type == EventType.DragPerform && ussPaths.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                foreach (var path in ussPaths)
                    ImportFromUssPath(path);
            }
            evt.Use();
        }

        private static List<string> CollectUssPaths()
        {
            var result = new List<string>();
            foreach (var obj in DragAndDrop.objectReferences)
            {
                var p = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                    result.Add(p);
            }
            return result;
        }

        private void ImportFromUssPath(string path)
        {
            var ussText = File.ReadAllText(path);
            var classes = ExtractClassNames(ussText, AudioTagValue);

            var existing = new HashSet<string>();
            if (matches != null)
                foreach (var m in matches)
                    if (!string.IsNullOrEmpty(m.className))
                        existing.Add(m.className);

            var toAdd = new List<Match>();
            foreach (var c in classes)
                if (!existing.Contains(c))
                    toAdd.Add(new Match { className = c, sound = defaultButton });

            if (toAdd.Count == 0)
            {
                Debug.Log($"[UISoundLibrary] Новых '{AudioTagValue}'-классов нет ({Path.GetFileName(path)}).", this);
                return;
            }

            Undo.RecordObject(this, "Import USS classes");
            var list = new List<Match>(matches ?? Array.Empty<Match>());
            list.AddRange(toAdd);
            matches = list.ToArray();
            EditorUtility.SetDirty(this);

            Debug.Log($"[UISoundLibrary] Добавлено {toAdd.Count} классов из {Path.GetFileName(path)}.", this);
        }

        // Парсим правила целиком: класс берём только если тело помечено нужным тегом
        private static List<string> ExtractClassNames(string ussText, string requiredTag)
        {
            var result = new List<string>();
            var seen = new HashSet<string>();

            ussText = CommentRegex.Replace(ussText, " ");

            int i = 0;
            while (true)
            {
                int open = ussText.IndexOf('{', i);
                if (open < 0) break;
                int close = ussText.IndexOf('}', open + 1);
                if (close < 0) break;

                var selector = ussText.Substring(i, open - i);
                var body     = ussText.Substring(open + 1, close - open - 1);
                i = close + 1;

                if (requiredTag != null)
                {
                    var tagMatch = TagRegex.Match(body);
                    if (!tagMatch.Success ||
                        !string.Equals(tagMatch.Groups[1].Value, requiredTag, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                foreach (System.Text.RegularExpressions.Match cm in ClassRegex.Matches(selector))
                {
                    var name = cm.Groups[1].Value;
                    if (seen.Add(name))
                        result.Add(name);
                }
            }
            return result;
        }
#endif
    }
}
