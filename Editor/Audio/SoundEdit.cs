using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Audio.Editor
{
    /// <summary>Клип в черновике: ассет проекта или внешний файл (скопируется в папку ключа по Apply).</summary>
    public struct DraftClip
    {
        public AudioClip Clip;
        public string ExternalPath;   // абсолютный путь файла вне проекта; пусто — ассет
        public float Volume;

        public string Name => Clip != null ? Clip.name : Path.GetFileName(ExternalPath);
    }

    /// <summary>
    /// Черновик ключа: правки копятся здесь, на диск и в каталог попадают только по <see cref="SoundEdit.Apply"/>.
    /// </summary>
    public sealed class SoundDraft
    {
        /// <summary>Ключ до правок; <c>null</c> — новый ключ.</summary>
        public string OriginalKey;
        public SoundEntry Entry;
        public readonly List<DraftClip> Clips = new();

        public static SoundDraft From(in SoundEntry entry)
        {
            var draft = new SoundDraft { OriginalKey = entry.key, Entry = entry };
            draft.Entry.clips = null;
            if (entry.clips != null)
                foreach (SoundClip c in entry.clips)
                    if (c.clip != null) draft.Clips.Add(new DraftClip { Clip = c.clip, Volume = c.volume });
            return draft;
        }

        public static SoundDraft New(string key, bool isDefault) => new() { OriginalKey = null, Entry = SoundEntry.Create(key, isDefault) };

        public SoundDraft Clone()
        {
            var copy = new SoundDraft { OriginalKey = OriginalKey, Entry = Entry };
            copy.Clips.AddRange(Clips);
            return copy;
        }

        /// <summary>Совпадает ли с записью каталога (для пометки «правка»).</summary>
        public bool SameAs(in SoundEntry entry)
        {
            SoundEntry a = Entry, b = entry;
            if (a.key != b.key || a.title != b.title || a.when != b.when || a.isDefault != b.isDefault || a.mode != b.mode ||
                a.defaultKey != b.defaultKey || a.bus != b.bus || a.output != b.output ||
                !Mathf.Approximately(a.volume, b.volume) || !Mathf.Approximately(a.pitchMin, b.pitchMin) ||
                !Mathf.Approximately(a.pitchMax, b.pitchMax) || !Mathf.Approximately(a.cooldown, b.cooldown)) return false;

            int n = 0;
            if (b.clips != null)
                foreach (SoundClip c in b.clips)
                {
                    if (c.clip == null) continue;
                    if (n >= Clips.Count) return false;
                    DraftClip d = Clips[n++];
                    if (d.Clip != c.clip || !string.IsNullOrEmpty(d.ExternalPath) || !Mathf.Approximately(d.Volume, c.volume)) return false;
                }
            return n == Clips.Count;
        }
    }

    /// <summary>
    /// Применение правок каталога звуков к диску и ассету: перенос папки при смене ключа, копирование клипов
    /// в папку ключа, нумерация <c>Last_NN</c>, удаление убранных клипов (в корзину), запись каталога и ключей.
    /// </summary>
    public static class SoundEdit
    {
        /// <summary>
        /// Применить черновик. Возвращает ошибку (ничего не изменено) или <c>null</c>.
        /// В <paramref name="report"/> — что сделано и где в коде остался старый ключ.
        /// </summary>
        public static string Apply(SoundSettings settings, SoundDraft draft, List<string> report)
        {
            if (settings == null) return "нет настроек звуков";

            string key = draft.Entry.key;
            string error = SoundKeys.ValidateKey(key);
            if (error != null) return error;

            SoundEntry[] entries = settings.Entries;
            int self = IndexOf(entries, draft.OriginalKey);
            int clash = IndexOf(entries, key);
            if (clash >= 0 && clash != self) return $"ключ {key} уже есть";
            if (HasDescendantOrAncestorAsFileConflict(key)) return $"путь {SoundKeys.FolderOf(key)} занят файлом";

            if (draft.Entry.isDefault && draft.Entry.mode == SoundMode.Default) draft.Entry.mode = SoundMode.Custom;

            bool renamed = draft.OriginalKey != null && draft.OriginalKey != key;
            string folder = SoundKeys.FolderOf(key);

            // ---- перенос папки
            if (renamed)
            {
                string oldFolder = SoundKeys.FolderOf(draft.OriginalKey);
                if (AssetDatabase.IsValidFolder(oldFolder))
                {
                    if (AssetDatabase.IsValidFolder(folder) && HasOwnFiles(folder))
                        return $"в {folder} уже лежат файлы — выберите другой ключ";

                    string moveError = MoveOwnFiles(oldFolder, folder);
                    if (moveError != null) return moveError;
                    DeleteEmptyUp(oldFolder);
                }
                report?.Add($"{draft.OriginalKey} → {key}");
            }

            // ---- клипы: всё — в папку ключа, лишнее из папки — в корзину, нумерация по порядку списка
            var finalClips = new List<SoundClip>(draft.Clips.Count);
            if (draft.Clips.Count > 0) EnsureFolder(folder);

            int temp = 0;
            var keepPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (DraftClip d in draft.Clips)
            {
                string path;
                if (!string.IsNullOrEmpty(d.ExternalPath))
                {
                    if (!File.Exists(d.ExternalPath)) { report?.Add($"файл пропал: {d.ExternalPath}"); continue; }
                    path = $"{folder}/__new_{temp++}{Path.GetExtension(d.ExternalPath).ToLowerInvariant()}";
                    File.Copy(d.ExternalPath, path, true);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                }
                else
                {
                    if (d.Clip == null) continue;
                    path = AssetDatabase.GetAssetPath(d.Clip);
                    if (Path.GetDirectoryName(path)?.Replace('\\', '/') != folder || keepPaths.Contains(path))
                    {
                        string copy = $"{folder}/__new_{temp++}{Path.GetExtension(path).ToLowerInvariant()}";
                        if (!AssetDatabase.CopyAsset(path, copy)) { report?.Add($"не скопировался {path}"); continue; }
                        path = copy;
                    }
                }

                keepPaths.Add(path);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) { report?.Add($"не аудио: {path}"); continue; }
                finalClips.Add(new SoundClip { clip = clip, volume = Mathf.Clamp01(d.Volume) });
            }

            // убранные клипы папки — в корзину (только файлы этого уровня, дочерние ключи не трогаем)
            if (AssetDatabase.IsValidFolder(folder))
                foreach (string path in OwnAudioFiles(folder))
                    if (!keepPaths.Contains(path))
                    {
                        AssetDatabase.MoveAssetToTrash(path);
                        report?.Add($"в корзину: {Path.GetFileName(path)}");
                    }

            Renumber(key, finalClips);

            // ---- запись
            SoundEntry entry = draft.Entry;
            entry.clips = finalClips.ToArray();

            var list = new List<SoundEntry>(entries);
            if (self >= 0) list[self] = entry;
            else list.Add(entry);

            // переименованный дефолт — ссылки на него переезжают
            if (renamed && entry.isDefault)
                for (int i = 0; i < list.Count; i++)
                    if (list[i].defaultKey == draft.OriginalKey)
                    {
                        SoundEntry e = list[i];
                        e.defaultKey = key;
                        list[i] = e;
                    }

            SoundCatalog.Commit(settings, list.ToArray(), renamed ? $"Звук {draft.OriginalKey} → {key}" : $"Звук {key}");
            if (!AssetDatabase.IsValidFolder(folder) || finalClips.Count == 0) DeleteEmptyUp(folder);

            if (renamed)
                foreach (string usage in FindUsages(draft.OriginalKey))
                    report?.Add($"старый ключ в коде: {usage}");

            draft.OriginalKey = key;
            draft.Entry = entry;
            draft.Clips.Clear();
            foreach (SoundClip c in entry.clips) draft.Clips.Add(new DraftClip { Clip = c.clip, Volume = c.volume });
            return null;
        }

        /// <summary>Удалить ключ: запись из каталога, свои клипы — в корзину, пустые папки — прочь.</summary>
        public static void Delete(SoundSettings settings, string key, List<string> report)
        {
            SoundEntry[] entries = settings.Entries;
            int index = IndexOf(entries, key);
            if (index < 0) return;

            string folder = SoundKeys.FolderOf(key);
            if (AssetDatabase.IsValidFolder(folder))
            {
                foreach (string path in OwnAudioFiles(folder)) AssetDatabase.MoveAssetToTrash(path);
                DeleteEmptyUp(folder);
            }

            var list = new List<SoundEntry>(entries);
            list.RemoveAt(index);
            SoundCatalog.Commit(settings, list.ToArray(), $"Удалён звук {key}");

            report?.Add($"удалён {key}");
            foreach (string usage in FindUsages(key)) report?.Add($"ключ остался в коде: {usage}");
        }

        // ---------------------------------------------------------------- поиск в коде

        /// <summary>Файлы .cs/.uxml в Assets, где встречается ключ строкой или его константа (кроме AppSounds.cs).</summary>
        public static List<string> FindUsages(string key)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(key)) return result;

            string quoted = "\"" + key + "\"";
            string constant = key + SoundKeys.Suffix;
            string keys = Path.GetFullPath(SoundSettings.KeysPath).Replace('\\', '/');

            foreach (string file in EnumerateSources())
            {
                if (Path.GetFullPath(file).Replace('\\', '/') == keys) continue;
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }
                if (text.Contains(quoted) || text.Contains(constant)) result.Add(file.Replace('\\', '/'));
            }
            return result;
        }

        private static IEnumerable<string> EnumerateSources()
        {
            foreach (string pattern in new[] { "*.cs", "*.uxml" })
                foreach (string file in Directory.EnumerateFiles("Assets", pattern, SearchOption.AllDirectories))
                    yield return file;
        }

        // ---------------------------------------------------------------- файлы

        public static int IndexOf(SoundEntry[] entries, string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < entries.Length; i++)
                if (entries[i].key == key) return i;
            return -1;
        }

        /// <summary>Аудиофайлы прямо в папке (без подпапок дочерних ключей), отсортированные по имени.</summary>
        public static List<string> OwnAudioFiles(string folder)
        {
            var files = new List<string>();
            if (!Directory.Exists(folder)) return files;
            foreach (string file in Directory.GetFiles(folder))
                if (SoundCatalog.IsAudioFile(file)) files.Add(file.Replace('\\', '/'));
            files.Sort(string.CompareOrdinal);
            return files;
        }

        private static bool HasOwnFiles(string folder) => OwnAudioFiles(folder).Count > 0;

        /// <summary>Где-то на пути ключа лежит файл с именем уровня (папку не создать).</summary>
        private static bool HasDescendantOrAncestorAsFileConflict(string key)
        {
            string path = SoundSettings.ClipsRoot;
            foreach (string level in key.Split('.'))
            {
                path += "/" + level;
                if (File.Exists(path)) return true;
            }
            return false;
        }

        /// <summary>Перенести свои аудиофайлы папки в новую (дочерние ключи остаются на месте).</summary>
        private static string MoveOwnFiles(string from, string to)
        {
            EnsureFolder(to);
            foreach (string path in OwnAudioFiles(from))
            {
                string error = AssetDatabase.MoveAsset(path, to + "/" + Path.GetFileName(path));
                if (!string.IsNullOrEmpty(error)) return $"не перенёсся {path}: {error}";
            }
            return null;
        }

        /// <summary>Имена клипов — <c>Last_01…</c> по порядку списка (через временные имена, без столкновений).</summary>
        private static void Renumber(string key, List<SoundClip> clips)
        {
            var paths = new string[clips.Count];
            for (int i = 0; i < clips.Count; i++) paths[i] = AssetDatabase.GetAssetPath(clips[i].clip);

            bool ordered = true;
            for (int i = 0; i < clips.Count && ordered; i++)
            {
                string want = SoundKeys.ClipFileName(key, i + 1, Path.GetExtension(paths[i]));
                ordered = Path.GetFileName(paths[i]) == want;
            }
            if (ordered) return;

            for (int i = 0; i < clips.Count; i++)
                AssetDatabase.RenameAsset(paths[i], $"__renum_{i}_{Guid.NewGuid():N}");

            for (int i = 0; i < clips.Count; i++)
            {
                string current = AssetDatabase.GetAssetPath(clips[i].clip);
                string want = Path.GetFileNameWithoutExtension(SoundKeys.ClipFileName(key, i + 1, Path.GetExtension(current)));
                string error = AssetDatabase.RenameAsset(current, want);
                if (!string.IsNullOrEmpty(error)) Debug.LogWarning($"[AppSound] {current}: {error}");
            }
        }

        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>Удалить папку и пустых предков вверх до корня звуков (корень не трогаем).</summary>
        private static void DeleteEmptyUp(string folder)
        {
            while (!string.IsNullOrEmpty(folder) && folder.Length > SoundSettings.ClipsRoot.Length &&
                   folder.StartsWith(SoundSettings.ClipsRoot + "/", StringComparison.Ordinal))
            {
                if (!AssetDatabase.IsValidFolder(folder)) { folder = Parent(folder); continue; }
                foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
                    if (!entry.EndsWith(".meta", StringComparison.Ordinal)) return; // не пустая
                AssetDatabase.DeleteAsset(folder);
                folder = Parent(folder);
            }
        }

        private static string Parent(string folder) => Path.GetDirectoryName(folder)?.Replace('\\', '/');

        // ---------------------------------------------------------------- память

        /// <summary>Оценка памяти клипа после распаковки (PCM 16 бит), байт.</summary>
        public static long DecodedBytes(AudioClip clip) => clip == null ? 0 : (long)clip.samples * clip.channels * 2;
    }
}
