using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Audio.Editor
{
    /// <summary>
    /// Обмен со звуковиком. Экспорт — zip: <c>sound-intake.html</c> с вшитым каталогом, <c>manifest.json</c>
    /// с настройками, <c>Clips/&lt;уровни ключа&gt;/…</c> с текущими клипами. Импорт — тот же формат (zip или
    /// распакованная папка): разница по ключам, применение выбранного через <see cref="SoundEdit.Apply"/>.
    /// </summary>
    public static class SoundExchange
    {
        public const string Format = "appcore-sounds";
        public const int Version = 1;
        public const string ManifestName = "manifest.json";
        public const string HtmlName = "sound-intake.html";
        private const string CatalogMarker = "/*@CATALOG@*/null";

        // ---------------------------------------------------------------- формат (общий с html)

        [Serializable]
        public sealed class PackManifest
        {
            public string format = Format;
            public int version = Version;
            public string project;
            public string created;
            public float masterVolume = -1f;
            public PackEntry[] entries = Array.Empty<PackEntry>();
        }

        [Serializable]
        public sealed class PackEntry
        {
            public string key;
            public string title;
            public string when;
            public bool isDefault;
            public string mode;
            public string defaultKey;
            public float volume = 1f;
            public float pitchMin = SoundEntry.DefaultPitchMin;
            public float pitchMax = SoundEntry.DefaultPitchMax;
            public float cooldown = SoundEntry.DefaultCooldown;
            public string note;
            public PackClip[] clips = Array.Empty<PackClip>();
        }

        [Serializable]
        public sealed class PackClip
        {
            public string file;
            public float volume = 1f;
            public string source;
        }

        /// <summary>Прочитанный пакет: манифест и папка, относительно которой лежат файлы.</summary>
        public sealed class Pack
        {
            public PackManifest Manifest;
            public string BaseDir;
            public string Source;
        }

        /// <summary>Разница одного ключа (или общей громкости — <see cref="Key"/> = <c>*</c>).</summary>
        public sealed class KeyDiff
        {
            public string Key;
            public bool IsNew;
            public bool ClipsChanged;
            public bool Selected;
            public string Note;
            public PackEntry Entry;
            public readonly List<string> Changes = new();
            public readonly List<string> Errors = new();

            public bool HasChanges => IsNew || ClipsChanged || Changes.Count > 0;
        }

        // ---------------------------------------------------------------- экспорт

        public static string ProjectName()
        {
            var sb = new StringBuilder();
            foreach (char c in PlayerSettings.productName ?? "App")
                if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "App";
        }

        /// <summary>Собрать zip для звуковика. Возвращает ошибку или <c>null</c>.</summary>
        public static string Export(SoundSettings settings, string zipPath, bool withClips, List<string> report)
        {
            if (settings == null) return "нет настроек звуков";

            string template = TemplatePath();
            if (!File.Exists(template)) return $"нет шаблона {template}";

            string project = ProjectName();
            PackManifest manifest = BuildManifest(settings, withClips, project, out List<(string zipPath, string assetPath)> clipFiles);
            string html = File.ReadAllText(template).Replace(CatalogMarker, CatalogJson(settings, project));

            string root = project + "Sounds/";
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using (FileStream stream = File.Create(zipPath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteText(zip, root + HtmlName, html);
                WriteText(zip, root + ManifestName, JsonUtility.ToJson(manifest, true));
                foreach ((string inZip, string asset) in clipFiles)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(root + inZip, System.IO.Compression.CompressionLevel.Optimal);
                    using Stream to = entry.Open();
                    using FileStream from = File.OpenRead(asset);
                    from.CopyTo(to);
                }
            }

            report?.Add($"{Path.GetFileName(zipPath)}: ключей {manifest.entries.Length}, клипов {clipFiles.Count}");
            return null;
        }

        private static void WriteText(ZipArchive zip, string path, string text)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path, System.IO.Compression.CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        /// <summary>Шаблон html лежит в пакете рядом с этим кодом.</summary>
        public static string TemplatePath()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SoundExchange).Assembly);
            string root = package != null ? package.resolvedPath : Path.GetFullPath("Packages/com.exerussus.app-core");
            return Path.Combine(root, "Editor", "Audio", "SoundIntake.html");
        }

        private static PackManifest BuildManifest(SoundSettings settings, bool withClips, string project,
                                                  out List<(string, string)> clipFiles)
        {
            clipFiles = new List<(string, string)>();
            SoundEntry[] entries = settings.Entries;
            var manifest = new PackManifest
            {
                project = project,
                created = DateTime.UtcNow.ToString("o"),
                masterVolume = settings.MasterVolume,
                entries = new PackEntry[entries.Length],
            };

            for (int i = 0; i < entries.Length; i++)
            {
                SoundEntry e = entries[i];
                PackEntry p = ToPack(e);
                var clips = new List<PackClip>();
                if (withClips && e.clips != null)
                {
                    int n = 0;
                    foreach (SoundClip c in e.clips)
                    {
                        if (c.clip == null) continue;
                        string asset = AssetDatabase.GetAssetPath(c.clip);
                        if (!File.Exists(asset)) continue;
                        n++;
                        string file = ClipsDir(e.key) + SoundKeys.ClipFileName(e.key, n, Path.GetExtension(asset));
                        clips.Add(new PackClip { file = file, volume = c.volume, source = Path.GetFileName(asset) });
                        clipFiles.Add((file, asset));
                    }
                }
                p.clips = clips.ToArray();
                manifest.entries[i] = p;
            }
            return manifest;
        }

        private static PackEntry ToPack(in SoundEntry e) => new()
        {
            key = e.key,
            title = e.title ?? string.Empty,
            when = e.when ?? string.Empty,
            isDefault = e.isDefault,
            mode = e.mode.ToString(),
            defaultKey = e.isDefault ? string.Empty : e.defaultKey ?? string.Empty,
            volume = e.volume,
            pitchMin = e.pitchMin,
            pitchMax = e.pitchMax,
            cooldown = e.cooldown,
            note = string.Empty,
        };

        private static string ClipsDir(string key) => "Clips/" + key.Replace('.', '/') + "/";

        /// <summary>Каталог для html: ключи, описания, текущие настройки (то, к чему html «сбрасывает»).</summary>
        private static string CatalogJson(SoundSettings settings, string project)
        {
            var catalog = new PackManifest { project = project, created = DateTime.UtcNow.ToString("o"), masterVolume = settings.MasterVolume };
            var list = new List<PackEntry>();
            foreach (SoundEntry e in settings.Entries)
                if (!string.IsNullOrEmpty(e.key)) list.Add(ToPack(e));
            catalog.entries = list.ToArray();
            // строка уходит внутрь <script>: «</» не должно закрыть тег
            return JsonUtility.ToJson(catalog).Replace("</", "<\\/");
        }

        // ---------------------------------------------------------------- импорт

        /// <summary>Прочитать zip или папку. Возвращает ошибку или <c>null</c>.</summary>
        public static string Read(string path, out Pack pack)
        {
            pack = null;
            string dir = path;

            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                dir = Path.Combine(Path.GetFullPath("Temp"), "AppSoundsImport", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try { Extract(path, dir); }
                catch (Exception e) { return "архив не распаковался: " + e.Message; }
            }

            if (!Directory.Exists(dir)) return "нет такой папки или архива";

            string manifestPath = FindManifest(dir);
            if (manifestPath == null) return $"нет {ManifestName}";

            PackManifest manifest;
            try { manifest = JsonUtility.FromJson<PackManifest>(File.ReadAllText(manifestPath)); }
            catch (Exception e) { return $"{ManifestName} не прочитан: {e.Message}"; }
            if (manifest == null || manifest.format != Format) return $"{ManifestName} не из звуков AppCore (format ≠ {Format})";

            pack = new Pack { Manifest = manifest, BaseDir = Path.GetDirectoryName(manifestPath), Source = Path.GetFileName(path) };
            return null;
        }

        private static void Extract(string zipPath, string dir)
        {
            string rootFull = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            using FileStream stream = File.OpenRead(zipPath);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // папка
                string target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                if (!target.StartsWith(rootFull, StringComparison.Ordinal)) continue; // путь наружу — мимо
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using Stream from = entry.Open();
                using FileStream to = File.Create(target);
                from.CopyTo(to);
            }
        }

        private static string FindManifest(string dir)
        {
            string direct = Path.Combine(dir, ManifestName);
            if (File.Exists(direct)) return direct;
            foreach (string file in Directory.EnumerateFiles(dir, ManifestName, SearchOption.AllDirectories)) return file;
            return null;
        }

        /// <summary>Разница пакета с каталогом. Выбраны по умолчанию ключи с изменениями без ошибок.</summary>
        public static List<KeyDiff> Diff(SoundSettings settings, Pack pack)
        {
            var result = new List<KeyDiff>();
            SoundEntry[] entries = settings.Entries;

            if (pack.Manifest.masterVolume >= 0f && !Mathf.Approximately(pack.Manifest.masterVolume, settings.MasterVolume))
            {
                var master = new KeyDiff { Key = "*", Selected = true };
                master.Changes.Add($"общая громкость {settings.MasterVolume:0.##} → {pack.Manifest.masterVolume:0.##}");
                result.Add(master);
            }

            foreach (PackEntry p in pack.Manifest.entries ?? Array.Empty<PackEntry>())
            {
                if (p == null || string.IsNullOrEmpty(p.key)) continue;
                var diff = new KeyDiff { Key = p.key, Entry = p, Note = p.note };

                string keyError = SoundKeys.ValidateKey(p.key);
                if (keyError != null) diff.Errors.Add(keyError);

                // файлы пакета
                foreach (PackClip c in p.clips ?? Array.Empty<PackClip>())
                    if (c == null || string.IsNullOrEmpty(c.file) || !File.Exists(Path.Combine(pack.BaseDir, c.file)))
                        diff.Errors.Add($"нет файла {c?.file}");

                int index = SoundEdit.IndexOf(entries, p.key);
                if (index < 0)
                {
                    diff.IsNew = true;
                    diff.Changes.Add($"новый ключ{(p.isDefault ? " (дефолт)" : string.Empty)}, клипов {p.clips?.Length ?? 0}");
                }
                else
                {
                    SoundEntry e = entries[index];
                    Compare(diff, e, p);
                    CompareClips(diff, e, p, pack.BaseDir);
                }

                diff.Selected = diff.HasChanges && diff.Errors.Count == 0;
                if (diff.HasChanges || diff.Errors.Count > 0 || !string.IsNullOrEmpty(diff.Note)) result.Add(diff);
            }

            return result;
        }

        private static void Compare(KeyDiff diff, in SoundEntry e, PackEntry p)
        {
            if (Enum.TryParse(p.mode, out SoundMode mode) && mode != e.mode) diff.Changes.Add($"режим {Ru(e.mode)} → {Ru(mode)}");
            if (!e.isDefault && (p.defaultKey ?? string.Empty) != (e.defaultKey ?? string.Empty) && mode == SoundMode.Default)
                diff.Changes.Add($"дефолт {e.defaultKey} → {p.defaultKey}");
            Num(diff, "громкость", e.volume, p.volume);
            Num(diff, "высота мин", e.pitchMin, p.pitchMin);
            Num(diff, "высота макс", e.pitchMax, p.pitchMax);
            if (!e.isDefault) Num(diff, "кулдаун", e.cooldown, p.cooldown);
        }

        private static void Num(KeyDiff diff, string name, float from, float to)
        {
            if (Mathf.Abs(from - to) > 0.0005f) diff.Changes.Add($"{name} {from:0.###} → {to:0.###}");
        }

        private static string Ru(SoundMode mode) => mode switch
        {
            SoundMode.Off => "Молчит",
            SoundMode.Default => "Дефолт",
            _ => "Свои",
        };

        private static void CompareClips(KeyDiff diff, in SoundEntry e, PackEntry p, string baseDir)
        {
            var current = new List<SoundClip>();
            if (e.clips != null)
                foreach (SoundClip c in e.clips)
                    if (c.clip != null) current.Add(c);

            PackClip[] incoming = p.clips ?? Array.Empty<PackClip>();
            bool changed = current.Count != incoming.Length;
            for (int i = 0; i < incoming.Length && !changed; i++)
            {
                string a = AssetDatabase.GetAssetPath(current[i].clip);
                string b = Path.Combine(baseDir, incoming[i].file ?? string.Empty);
                changed = !SameFile(a, b);
            }

            if (changed)
            {
                diff.ClipsChanged = true;
                diff.Changes.Add($"клипы {current.Count} → {incoming.Length} (новые файлы)");
                return;
            }

            for (int i = 0; i < incoming.Length; i++)
                if (Mathf.Abs(current[i].volume - incoming[i].volume) > 0.0005f)
                    diff.Changes.Add($"клип {i + 1}: громкость {current[i].volume:0.##} → {incoming[i].volume:0.##}");
        }

        private static bool SameFile(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using MD5 md5 = MD5.Create();
            byte[] ha, hb;
            using (FileStream s = fa.OpenRead()) ha = md5.ComputeHash(s);
            using (FileStream s = fb.OpenRead()) hb = md5.ComputeHash(s);
            for (int i = 0; i < ha.Length; i++)
                if (ha[i] != hb[i]) return false;
            return true;
        }

        /// <summary>Применить выбранные изменения. Каждый ключ — через черновик и <see cref="SoundEdit.Apply"/>.</summary>
        public static void Apply(SoundSettings settings, Pack pack, List<KeyDiff> diffs, List<string> report)
        {
            foreach (KeyDiff diff in diffs)
            {
                if (!diff.Selected || diff.Errors.Count > 0) continue;

                if (diff.Key == "*")
                {
                    var so = new SerializedObject(settings);
                    so.FindProperty("masterVolume").floatValue = Mathf.Clamp01(pack.Manifest.masterVolume);
                    so.ApplyModifiedProperties();
                    AssetDatabase.SaveAssetIfDirty(settings);
                    report?.Add("общая громкость");
                    continue;
                }

                PackEntry p = diff.Entry;
                int index = SoundEdit.IndexOf(settings.Entries, p.key);
                SoundDraft draft = index >= 0 ? SoundDraft.From(settings.Entries[index]) : SoundDraft.New(p.key, p.isDefault);

                if (index < 0)
                {
                    draft.Entry.title = p.title ?? string.Empty;
                    draft.Entry.when = p.when ?? string.Empty;
                }

                if (Enum.TryParse(p.mode, out SoundMode mode)) draft.Entry.mode = mode;
                if (!draft.Entry.isDefault && !string.IsNullOrEmpty(p.defaultKey)) draft.Entry.defaultKey = p.defaultKey;
                draft.Entry.volume = Mathf.Clamp01(p.volume);
                draft.Entry.pitchMin = Mathf.Clamp(Mathf.Min(p.pitchMin, p.pitchMax), 0.5f, 2f);
                draft.Entry.pitchMax = Mathf.Clamp(Mathf.Max(p.pitchMin, p.pitchMax), 0.5f, 2f);
                draft.Entry.cooldown = Mathf.Max(0f, p.cooldown);

                PackClip[] incoming = p.clips ?? Array.Empty<PackClip>();
                if (diff.ClipsChanged || index < 0)
                {
                    draft.Clips.Clear();
                    foreach (PackClip c in incoming)
                        draft.Clips.Add(new DraftClip { ExternalPath = Path.GetFullPath(Path.Combine(pack.BaseDir, c.file)), Volume = c.volume });
                }
                else
                {
                    for (int i = 0; i < draft.Clips.Count && i < incoming.Length; i++)
                    {
                        DraftClip d = draft.Clips[i];
                        d.Volume = incoming[i].volume;
                        draft.Clips[i] = d;
                    }
                }

                var local = new List<string>();
                string error = SoundEdit.Apply(settings, draft, local);
                report?.Add(error == null ? $"{p.key}" : $"{p.key}: не применён — {error}");
            }
        }
    }
}
