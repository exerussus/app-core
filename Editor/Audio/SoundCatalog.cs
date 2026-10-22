using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Audio.Editor
{
    /// <summary>
    /// Редакторские операции над каталогом звуков: порядок, проверка, генерация ключей, папки клипов.
    /// Все правки каталога идут через <see cref="Commit"/>: сортировка → запись ассета → <c>AppSounds.cs</c>.
    /// </summary>
    public static class SoundCatalog
    {
        public static readonly string[] AudioExtensions = { ".wav", ".ogg", ".mp3", ".aif", ".aiff" };

        public static bool IsAudioFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(AudioExtensions, ext) >= 0;
        }

        // ---------------------------------------------------------------- ассет

        public static SoundSettings Load() => AssetDatabase.LoadAssetAtPath<SoundSettings>(SoundSettings.AssetPath);

        /// <summary>Ассет настроек: найти или создать (если AppCore развёрнут — есть Assets/App/Settings).</summary>
        public static SoundSettings Ensure(bool force)
        {
            SoundSettings settings = Load();
            if (settings != null) return settings;

            if (!AssetDatabase.IsValidFolder(AppCorePaths.SettingsDir))
            {
                if (force) Debug.LogWarning($"[AppSound] Нет папки {AppCorePaths.SettingsDir} — AppCore не развёрнут в проекте.");
                return null;
            }

            settings = ScriptableObject.CreateInstance<SoundSettings>();
            AssetDatabase.CreateAsset(settings, SoundSettings.AssetPath);
            SoundSettings.ResetLookup();
            Debug.Log($"[AppSound] Создан ассет настроек: {SoundSettings.AssetPath}");
            return settings;
        }

        // ---------------------------------------------------------------- запись каталога

        /// <summary>
        /// Записать каталог: сортировка по ключу (ordinal) — это номера констант, запись ассета, генерация
        /// <c>AppSounds.cs</c> (только при изменении текста). Undo — одним шагом.
        /// </summary>
        public static void Commit(SoundSettings settings, SoundEntry[] entries, string undoName)
        {
            Undo.RecordObject(settings, undoName);
            SoundEntry[] sorted = Sorted(entries);
            settings.SetEntries(sorted);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            Generate(sorted);
            if (Application.isPlaying) AppSound.Reload();
        }

        public static SoundEntry[] Sorted(SoundEntry[] entries)
        {
            var copy = (SoundEntry[])(entries ?? Array.Empty<SoundEntry>()).Clone();
            Array.Sort(copy, (a, b) => string.CompareOrdinal(a.key, b.key));
            return copy;
        }

        public static bool IsSorted(SoundEntry[] entries)
        {
            for (int i = 1; i < entries.Length; i++)
                if (string.CompareOrdinal(entries[i - 1].key, entries[i].key) > 0) return false;
            return true;
        }

        /// <summary>Перегенерировать <c>AppSounds.cs</c>. true — файл изменился (будет перекомпиляция).</summary>
        public static bool Generate(SoundEntry[] sortedEntries)
        {
            string text = SoundKeys.Generate(sortedEntries);
            if (!global::Exerussus.AppCore.Editor.AppCoreAssets.WriteTextIfChanged(SoundSettings.KeysPath, text)) return false;
            AssetDatabase.ImportAsset(SoundSettings.KeysPath);
            return true;
        }

        // ---------------------------------------------------------------- проверка

        public enum IssueLevel { Info, Warning, Error }

        public readonly struct Issue
        {
            public readonly IssueLevel Level;
            public readonly string Key;
            public readonly string Text;

            public Issue(IssueLevel level, string key, string text)
            {
                Level = level;
                Key = key;
                Text = text;
            }
        }

        /// <summary>Все замечания по каталогу: ключи, дубли, режимы, дефолты, клипы.</summary>
        public static List<Issue> Validate(SoundEntry[] entries)
        {
            var issues = new List<Issue>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Length; i++)
                if (!string.IsNullOrEmpty(entries[i].key)) index[entries[i].key] = i;

            for (int i = 0; i < entries.Length; i++)
            {
                SoundEntry e = entries[i];
                string error = SoundKeys.ValidateKey(e.key);
                if (error != null) issues.Add(new Issue(IssueLevel.Error, e.key, error));
                if (!seen.Add(e.key ?? string.Empty)) issues.Add(new Issue(IssueLevel.Error, e.key, "ключ повторяется"));

                int clips = CountClips(e);
                if (e.isDefault && e.mode == SoundMode.Default)
                    issues.Add(new Issue(IssueLevel.Error, e.key, "дефолт не может ссылаться на дефолт"));

                if (e.mode == SoundMode.Custom && clips == 0)
                    issues.Add(new Issue(e.isDefault ? IssueLevel.Info : IssueLevel.Warning, e.key,
                        e.isDefault ? "дефолт без клипов — события с ним молчат" : "режим «Свои клипы», но клипов нет — тишина"));
                else if (e.mode != SoundMode.Custom && clips > 0)
                    issues.Add(new Issue(IssueLevel.Info, e.key, "клипы есть, но режим не «Свои клипы» — не звучат"));

                if (!e.isDefault && e.mode == SoundMode.Default)
                {
                    if (!index.TryGetValue(e.defaultKey ?? string.Empty, out int d))
                        issues.Add(new Issue(IssueLevel.Warning, e.key, $"дефолт «{e.defaultKey}» не найден — тишина"));
                    else if (!entries[d].isDefault)
                        issues.Add(new Issue(IssueLevel.Warning, e.key, $"«{e.defaultKey}» не помечен как дефолт — тишина"));
                    else if (entries[d].mode == SoundMode.Off)
                        issues.Add(new Issue(IssueLevel.Info, e.key, $"дефолт «{e.defaultKey}» выключен — тишина"));
                    else if (CountClips(entries[d]) == 0)
                        issues.Add(new Issue(IssueLevel.Info, e.key, $"дефолт «{e.defaultKey}» без клипов — тишина"));
                }

                if (e.bus == SoundBus.World)
                    issues.Add(new Issue(IssueLevel.Info, e.key, "шина «Мир (3D)» пока проигрывается как 2D"));
            }

            return issues;
        }

        public static int CountClips(in SoundEntry entry)
        {
            if (entry.clips == null) return 0;
            int n = 0;
            foreach (SoundClip c in entry.clips)
                if (c.clip != null) n++;
            return n;
        }

        // ---------------------------------------------------------------- папки

        /// <summary>
        /// Свести каталог с папками <see cref="SoundSettings.ClipsRoot"/>: папка с аудиофайлами на своём уровне —
        /// ключ (запись появляется, если её нет); клипы записи — файлы её папки по имени, поправки громкости
        /// сохраняются по клипу. Записи без папки остаются (режим «Дефолт»/«Молчит» клипов не требует).
        /// </summary>
        public static SoundEntry[] SyncWithFolders(SoundEntry[] entries, List<string> report)
        {
            var list = new List<SoundEntry>(entries);
            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++)
                if (!string.IsNullOrEmpty(list[i].key)) byKey[list[i].key] = i;

            var found = ScanFolders();
            foreach (KeyValuePair<string, List<AudioClip>> pair in found)
            {
                string key = pair.Key;
                string error = SoundKeys.ValidateKey(key);
                if (error != null)
                {
                    report?.Add($"папка «{SoundKeys.FolderOf(key)}» пропущена: {error}");
                    continue;
                }

                if (!byKey.TryGetValue(key, out int i))
                {
                    SoundEntry created = SoundEntry.Create(key);
                    created.mode = SoundMode.Custom;
                    i = list.Count;
                    list.Add(created);
                    byKey[key] = i;
                    report?.Add($"новый ключ {key}");
                }

                SoundEntry entry = list[i];
                SoundClip[] merged = MergeClips(entry.clips, pair.Value);
                if (!SameClips(entry.clips, merged))
                {
                    report?.Add($"{key}: клипов {merged.Length}");
                    entry.clips = merged;
                    list[i] = entry;
                }
            }

            // клипы, удалённые из проекта, выпадают из записей
            for (int i = 0; i < list.Count; i++)
            {
                SoundEntry entry = list[i];
                if (entry.clips == null || found.ContainsKey(entry.key ?? string.Empty)) continue;
                int alive = CountClips(entry);
                if (alive == entry.clips.Length) continue;
                var kept = new List<SoundClip>(alive);
                foreach (SoundClip c in entry.clips)
                    if (c.clip != null) kept.Add(c);
                entry.clips = kept.ToArray();
                list[i] = entry;
            }

            return list.ToArray();
        }

        /// <summary>Ключ → клипы папки (по имени файла), для всех папок под корнем, где лежат аудиофайлы.</summary>
        public static SortedDictionary<string, List<AudioClip>> ScanFolders()
        {
            var result = new SortedDictionary<string, List<AudioClip>>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(SoundSettings.ClipsRoot)) return result;

            string prefix = SoundSettings.ClipsRoot + "/";
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SoundSettings.ClipsRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;

                string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (folder == null || folder.Length <= prefix.Length - 1) continue; // файл прямо в корне — без ключа
                string key = folder.Substring(prefix.Length).Replace('/', '.');

                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) continue;
                if (!result.TryGetValue(key, out List<AudioClip> clips)) result[key] = clips = new List<AudioClip>();
                clips.Add(clip);
            }

            foreach (List<AudioClip> clips in result.Values)
                clips.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
            return result;
        }

        private static SoundClip[] MergeClips(SoundClip[] old, List<AudioClip> clips)
        {
            var merged = new SoundClip[clips.Count];
            for (int i = 0; i < clips.Count; i++)
            {
                merged[i] = SoundClip.Of(clips[i]);
                if (old == null) continue;
                foreach (SoundClip o in old)
                    if (o.clip == clips[i]) { merged[i].volume = o.volume; break; }
            }
            return merged;
        }

        private static bool SameClips(SoundClip[] a, SoundClip[] b)
        {
            a ??= Array.Empty<SoundClip>();
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].clip != b[i].clip || !Mathf.Approximately(a[i].volume, b[i].volume)) return false;
            return true;
        }

        [MenuItem("Exerussus/App/Sounds/Синхронизировать с папками")]
        private static void SyncMenu()
        {
            SoundSettings settings = Ensure(true);
            if (settings == null) return;

            var report = new List<string>();
            SoundEntry[] synced = SyncWithFolders(settings.Entries, report);
            Commit(settings, synced, "Синхронизация звуков с папками");
            Debug.Log(report.Count == 0 ? "[AppSound] Каталог совпадает с папками." : "[AppSound] Синхронизация:\n  " + string.Join("\n  ", report));
        }

        [MenuItem("Exerussus/App/Sounds/Проверить каталог")]
        private static void ValidateMenu()
        {
            SoundSettings settings = Load();
            if (settings == null) { Debug.LogWarning("[AppSound] Нет настроек звуков."); return; }

            List<Issue> issues = Validate(settings.Entries);
            if (issues.Count == 0) { Debug.Log("[AppSound] Каталог в порядке.", settings); return; }
            foreach (Issue issue in issues)
            {
                string line = $"[AppSound] {issue.Key}: {issue.Text}";
                if (issue.Level == IssueLevel.Error) Debug.LogError(line, settings);
                else if (issue.Level == IssueLevel.Warning) Debug.LogWarning(line, settings);
                else Debug.Log(line, settings);
            }
        }

        [MenuItem("Exerussus/App/Sounds/Настройки")]
        private static void SelectMenu()
        {
            SoundSettings settings = SoundSettingsBootstrap.Ensure(true);
            if (settings != null) Selection.activeObject = settings;
        }
    }
}
