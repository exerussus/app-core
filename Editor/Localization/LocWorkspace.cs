using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>
    /// Рабочая копия всех таблиц локализации для редактора: база и пакеты каталога, сведённые в одну
    /// таблицу (<see cref="Merged"/>) так же, как их сводит игра: поздний файл перекрывает значение по слоту.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Правки делаются в <see cref="Merged"/> как в обычной <see cref="LocTable"/>, а ключевые операции —
    /// методами (<see cref="AddKey"/>, <see cref="RenameKey"/>, <see cref="DeleteKey"/>, <see cref="MoveKey"/>).
    /// <see cref="Save"/> сравнивает сводку со снимком при чтении и раскладывает изменения по файлам:
    /// значение — в файл, откуда пришло действующее значение этого слота, а нет его — в «дом» ключа
    /// (последний записываемый файл с ключом, для нового ключа — выбранный пакет). Снятое значение
    /// снимается во всех записываемых файлах. Файл только для чтения не меняется никогда.
    /// </para>
    /// <para>
    /// Файл, изменённый на диске после чтения, не затирается: сохранение отказывает целиком и просит перечитать.
    /// </para>
    /// </remarks>
    public sealed class LocWorkspace
    {
        /// <summary>Один файл рабочей копии.</summary>
        public sealed class File
        {
            public LocPackageFile Info;
            public LocTable Table;
            public readonly List<LocError> Errors = new();

            /// <summary>Текст при чтении; <c>null</c> — файла ещё нет (создаётся сохранением).</summary>
            public string LoadedText;

            public int Index { get; internal set; }

            public bool Exists => LoadedText != null;
            public bool Writable => !Info.ReadOnly;
            public string Name => Info.Name;

            public override string ToString() => Info.ToString();
        }

        /// <summary>Итог сохранения.</summary>
        public sealed class SaveResult
        {
            public readonly List<string> Written = new();
            public readonly List<string> Conflicts = new();
            public readonly List<string> Errors = new();
            public readonly List<string> Warnings = new();

            public bool Ok => Conflicts.Count == 0 && Errors.Count == 0;
        }

        private enum OpKind { Rename, Delete }

        private readonly struct Op
        {
            public readonly OpKind Kind;
            public readonly string Key;
            public readonly string To;

            public Op(OpKind kind, string key, string to)
            {
                Kind = kind;
                Key = key;
                To = to;
            }
        }

        private readonly List<File> _files = new();
        private readonly List<Op> _ops = new();

        // ключ → слот → файлы, писавшие значение, по порядку (последний — действующий)
        private readonly Dictionary<string, Dictionary<string, List<File>>> _writers = new(StringComparer.Ordinal);

        // ключ → файлы, где он есть, по порядку
        private readonly Dictionary<string, List<File>> _keyFiles = new(StringComparer.Ordinal);

        // ключ → куда писать (новый ключ, перенос)
        private readonly Dictionary<string, File> _home = new(StringComparer.Ordinal);

        // ключи, которые при сохранении переписываются в дом целиком (перенос)
        private readonly HashSet<string> _moved = new(StringComparer.Ordinal);

        private LocTable _baseline;

        /// <summary>Сводная таблица — её показывает и правит редактор.</summary>
        public LocTable Merged { get; private set; } = new();

        public IReadOnlyList<File> Files => _files;

        /// <summary>Файлы, которые читались с ошибками (строки с ошибкой при сохранении пропадут).</summary>
        public IEnumerable<File> FilesWithErrors
        {
            get
            {
                foreach (var file in _files)
                    if (file.Errors.Count > 0) yield return file;
            }
        }

        /// <summary>Куда идут новые ключи по умолчанию: база, иначе первый записываемый файл.</summary>
        public File DefaultFile
        {
            get
            {
                foreach (var file in _files)
                    if (file.Info.IsBase && file.Writable) return file;
                foreach (var file in _files)
                    if (file.Writable) return file;
                return null;
            }
        }

        // ================================================================ чтение

        /// <summary>Читает каталог и все файлы.</summary>
        public static LocWorkspace Load(LocSettings settings = null)
        {
            var workspace = new LocWorkspace();
            workspace.Read(LocPackageCatalog.Collect(settings));
            return workspace;
        }

        /// <summary>Рабочая копия по готовому списку файлов (порядок — порядок применения). Для инструментов и тестов.</summary>
        public static LocWorkspace Load(List<LocPackageFile> files)
        {
            var workspace = new LocWorkspace();
            workspace._fixed = files;
            workspace.Read(files);
            return workspace;
        }

        // список файлов, заданный снаружи (Load(files)): Reload читает его, а не каталог
        private List<LocPackageFile> _fixed;

        /// <summary>Перечитать с диска (несохранённое пропадает).</summary>
        public void Reload(LocSettings settings = null) => Read(_fixed ?? LocPackageCatalog.Collect(settings));

        private void Read(List<LocPackageFile> catalog)
        {
            _files.Clear();
            _ops.Clear();
            _home.Clear();
            _moved.Clear();

            foreach (var info in catalog)
            {
                var file = new File { Info = info, Index = _files.Count };

                if (System.IO.File.Exists(info.FullPath))
                {
                    try
                    {
                        file.LoadedText = System.IO.File.ReadAllText(info.FullPath);
                        var parsed = LocFormat.ParseLenient(file.LoadedText);
                        file.Table = parsed.Table;
                        file.Errors.AddRange(parsed.Errors);
                    }
                    catch (Exception e)
                    {
                        file.Table = new LocTable();
                        file.Errors.Add(new LocError(0, "не читается: " + e.Message));
                        file.Info.ReadOnly = true;
                    }
                }
                else
                {
                    file.Table = new LocTable();
                }

                _files.Add(file);
            }

            BuildMerged();
        }

        private void BuildMerged()
        {
            _writers.Clear();
            _keyFiles.Clear();
            var merged = new LocTable();

            foreach (var file in _files)
            {
                foreach (var header in file.Table.Languages)
                {
                    var existing = merged.GetLanguage(header.Code);
                    if (existing == null) merged.AddLanguage(header.Code, header.Name, header.Plural, header.Fallback);
                    else
                    {
                        if (!string.IsNullOrEmpty(header.Name)) existing.Name = header.Name;
                        if (!string.IsNullOrEmpty(header.Plural)) existing.Plural = header.Plural;
                        if (!string.IsNullOrEmpty(header.Fallback)) existing.Fallback = header.Fallback;
                    }
                }

                foreach (var entry in file.Table.Entries)
                {
                    var key = entry.Key;
                    if (!_keyFiles.TryGetValue(key, out var files)) _keyFiles[key] = files = new List<File>(1);
                    if (!files.Contains(file)) files.Add(file);

                    if (entry.RemoveKey)
                    {
                        merged.Remove(key);
                        _writers.Remove(key);
                        continue;
                    }

                    var target = merged.GetOrAdd(key);
                    if (entry.Comments.Count > 0)
                    {
                        target.Comments.Clear();
                        target.Comments.AddRange(entry.Comments);
                    }

                    foreach (var value in entry.Values)
                    {
                        var slot = value.Slot;
                        if (value.IsRemove)
                        {
                            merged.Clear(key, value.Language, value.Form);
                            if (_writers.TryGetValue(key, out var removed)) removed.Remove(slot);
                            continue;
                        }

                        merged.Set(key, value.Language, value.Text, value.Form);

                        if (!_writers.TryGetValue(key, out var slots))
                            _writers[key] = slots = new Dictionary<string, List<File>>(StringComparer.OrdinalIgnoreCase);
                        if (!slots.TryGetValue(slot, out var writers)) slots[slot] = writers = new List<File>(1);
                        writers.Add(file);
                    }
                }
            }

            Merged = merged;
            _baseline = merged.Clone();
        }

        // ================================================================ вопросы

        /// <summary>Файл, откуда пришло действующее значение слота. <c>null</c> — значения нет.</summary>
        public File OriginOf(string key, string language, string form = null)
        {
            if (key == null || !_writers.TryGetValue(key, out var slots)) return null;
            return slots.TryGetValue(LocSlots.Make(language, form), out var writers) && writers.Count > 0 ? writers[writers.Count - 1] : null;
        }

        /// <summary>Все файлы, писавшие слот, по порядку (последний — действующий), с их текстами.</summary>
        public List<(File file, string text)> ChainOf(string key, string language, string form = null)
        {
            var list = new List<(File, string)>();
            if (key == null || !_writers.TryGetValue(key, out var slots)) return list;
            if (!slots.TryGetValue(LocSlots.Make(language, form), out var writers)) return list;

            foreach (var file in writers)
                list.Add((file, file.Table.Get(key)?.Find(language, form)?.Text));
            return list;
        }

        /// <summary>Файлы, где есть ключ (по порядку применения).</summary>
        public IReadOnlyList<File> FilesOf(string key) =>
            key != null && _keyFiles.TryGetValue(key, out var files) ? files : Array.Empty<File>();

        /// <summary>Значение какого-то слота ключа писали два и больше файла.</summary>
        public bool IsOverridden(string key)
        {
            if (key == null || !_writers.TryGetValue(key, out var slots)) return false;
            foreach (var pair in slots)
                if (pair.Value.Count > 1) return true;
            return false;
        }

        /// <summary>
        /// «Дом» ключа: куда пишутся его новые значения и комментарии — выбранный пакет (новый или
        /// перенесённый ключ), иначе последний записываемый файл с ключом, иначе <see cref="DefaultFile"/>.
        /// </summary>
        public File HomeOf(string key)
        {
            if (key != null && _home.TryGetValue(key, out var home)) return home;

            var files = FilesOf(key);
            for (var i = files.Count - 1; i >= 0; i--)
                if (files[i].Writable) return files[i];

            return DefaultFile;
        }

        /// <summary>
        /// Куда ляжет правка слота: в файл действующего значения, если он записываемый; иначе — в записываемый
        /// файл после него (лучше с этим ключом), а нет такого — в дом ключа. <paramref name="shadowed"/> —
        /// правка ляжет под файлом только для чтения и в игре видна не будет.
        /// </summary>
        public File TargetFor(string key, string language, string form, out bool shadowed)
        {
            shadowed = false;
            var origin = OriginOf(key, language, form);
            if (origin == null || origin.Writable) return origin ?? HomeOf(key);

            File after = null;
            for (var i = origin.Index + 1; i < _files.Count; i++)
            {
                var file = _files[i];
                if (!file.Writable) continue;
                if (file.Table.Contains(key)) return file;
                after ??= file;
            }

            if (after != null) return after;

            shadowed = true;
            return HomeOf(key);
        }

        /// <summary>Ключ только в файлах для чтения — правки лягут перекрытием в записываемый файл.</summary>
        public bool IsReadOnlyKey(string key)
        {
            var files = FilesOf(key);
            if (files.Count == 0) return false;
            foreach (var file in files)
                if (file.Writable) return false;
            return true;
        }

        public File FindFile(string idOrPath)
        {
            foreach (var file in _files)
                if (file.Info.Id == idOrPath || string.Equals(file.Info.Path, idOrPath, StringComparison.OrdinalIgnoreCase) || file.Info.Name == idOrPath)
                    return file;
            return null;
        }

        // ================================================================ операции

        /// <summary>Новый ключ в файле (<c>null</c> — <see cref="DefaultFile"/>). False — ключ занят, недопустим или файл только для чтения.</summary>
        public bool AddKey(string key, File file = null)
        {
            file ??= DefaultFile;
            if (file == null || !file.Writable || !LocSlots.IsValidKey(key) || Merged.Contains(key)) return false;

            Merged.GetOrAdd(key);
            _home[key] = file;
            return true;
        }

        /// <summary>Переименование ключа во всех записываемых файлах.</summary>
        public bool RenameKey(string from, string to)
        {
            if (from == to) return true;
            if (!Merged.Rename(from, to)) return false;

            _baseline.Rename(from, to);
            Rekey(_writers, from, to);
            Rekey(_keyFiles, from, to);
            Rekey(_home, from, to);
            if (_moved.Remove(from)) _moved.Add(to);
            _ops.Add(new Op(OpKind.Rename, from, to));
            return true;
        }

        /// <summary>Удаление ключа из всех записываемых файлов.</summary>
        public bool DeleteKey(string key)
        {
            if (!Merged.Remove(key)) return false;

            _baseline.Remove(key);
            _home.Remove(key);
            _moved.Remove(key);
            _ops.Add(new Op(OpKind.Delete, key, null));
            return true;
        }

        /// <summary>Перенос ключа в файл: при сохранении он целиком ляжет туда и уйдёт из остальных записываемых.</summary>
        public bool MoveKey(string key, File target)
        {
            if (target == null || !target.Writable || !Merged.Contains(key)) return false;

            _home[key] = target;
            _moved.Add(key);
            return true;
        }

        /// <summary>
        /// Новый пакет: пустой файл с языками сводки. Пишется при сохранении. Путь должен подходить под маски
        /// пакетов, иначе после перечитывания файл выпадет из каталога (см. предупреждение в <see cref="SaveResult"/>).
        /// </summary>
        public File CreatePackage(string projectPath)
        {
            var info = LocPackageFile.Create(projectPath);
            if (info.ReadOnly || FindFile(info.Path) != null) return null;
            if (!info.Path.EndsWith(LocFormat.Extension, StringComparison.OrdinalIgnoreCase)) return null;

            info.Provider = LocPackageCatalog.MasksProvider;
            var table = new LocTable();
            foreach (var language in Merged.Languages) table.AddLanguage(language.Code, language.Name);

            var file = new File { Info = info, Table = table, Index = _files.Count };
            _files.Add(file);
            return file;
        }

        private static void Rekey<T>(Dictionary<string, T> map, string from, string to)
        {
            if (!map.Remove(from, out var value)) return;
            map[to] = value;
        }

        // ================================================================ запись

        /// <summary>Есть ли что сохранять (сравнение сводки со снимком и операции).</summary>
        public bool HasChanges()
        {
            if (_ops.Count > 0 || _moved.Count > 0) return true;
            foreach (var file in _files)
                if (!file.Exists && file.Writable) return true;
            return LocFormat.Write(Merged) != LocFormat.Write(_baseline);
        }

        /// <summary>
        /// Раскладывает правки по файлам и пишет изменённые (LF, без BOM). Изменённый на диске файл — конфликт,
        /// ничего не пишется. После успеха рабочая копия перечитана.
        /// </summary>
        public SaveResult Save()
        {
            var result = new SaveResult();

            // Конфликты — до любых изменений.
            foreach (var file in _files)
            {
                if (!file.Writable) continue;
                var disk = System.IO.File.Exists(file.Info.FullPath) ? SafeRead(file.Info.FullPath) : null;
                if (disk != file.LoadedText) result.Conflicts.Add(file.Info.Path);
            }

            if (result.Conflicts.Count > 0) return result;

            var tables = new Dictionary<File, LocTable>();
            LocTable Edit(File file)
            {
                if (!tables.TryGetValue(file, out var table)) tables[file] = table = file.Table.Clone();
                return table;
            }

            // 1. операции над ключами
            foreach (var op in _ops)
            {
                foreach (var file in _files)
                {
                    var has = tables.TryGetValue(file, out var current) ? current.Contains(op.Key) : file.Table.Contains(op.Key);
                    if (!has) continue;

                    if (!file.Writable)
                    {
                        result.Warnings.Add(op.Kind == OpKind.Rename
                            ? $"{file.Info.Path} (только чтение): ключ {op.Key} там остался под старым именем"
                            : $"{file.Info.Path} (только чтение): ключ {op.Key} там остался");
                        continue;
                    }

                    var table = Edit(file);
                    if (op.Kind == OpKind.Delete) table.Remove(op.Key);
                    else if (!table.Rename(op.Key, op.To)) result.Errors.Add($"{file.Info.Path}: не переименовать {op.Key} → {op.To}");
                }
            }

            // 2. значения и комментарии
            foreach (var entry in Merged.Entries)
            {
                var key = entry.Key;
                var before = _baseline.Get(key);
                var home = HomeOf(key);

                if (_moved.Contains(key))
                {
                    if (home == null || !home.Writable) { result.Errors.Add($"{key}: некуда перенести"); continue; }

                    foreach (var file in FilesOf(key))
                        if (file != home && file.Writable) Edit(file).Remove(key);

                    var target = Edit(home);
                    target.Remove(key);
                    var copy = target.GetOrAdd(key);
                    copy.Comments.AddRange(entry.Comments);
                    foreach (var value in entry.Values) copy.Values.Add(value.Clone());
                    continue;
                }

                // новый ключ: запись в доме, даже если значений пока нет
                if (before == null || FilesOf(key).Count == 0)
                {
                    if (home == null || !home.Writable)
                    {
                        result.Errors.Add($"{key}: нет записываемого файла для нового ключа");
                        continue;
                    }

                    Edit(home).GetOrAdd(key);
                }

                foreach (var value in entry.Values)
                {
                    var previous = before?.Find(value.Language, value.Form)?.Text;
                    if (previous == value.Text) continue;

                    var target = TargetFor(key, value.Language, value.Form, out var shadowed);
                    if (target == null || !target.Writable)
                    {
                        result.Errors.Add($"{key} [{value.Slot}]: нет записываемого файла");
                        continue;
                    }

                    if (shadowed)
                        result.Warnings.Add($"{key} [{value.Slot}]: записано в {target.Info.Path}, но в игре действует значение из " +
                                            $"{OriginOf(key, value.Language, value.Form)?.Info.Path} (только чтение, позже по порядку)");

                    Edit(target).Set(key, value.Language, value.Text, value.Form);
                }

                if (before != null)
                {
                    foreach (var value in before.Values)
                    {
                        if (entry.Find(value.Language, value.Form) != null) continue;

                        foreach (var (file, _) in ChainOf(key, value.Language, value.Form))
                        {
                            if (file.Writable) Edit(file).Clear(key, value.Language, value.Form);
                            else result.Warnings.Add($"{key} [{value.Slot}]: значение осталось в {file.Info.Path} (только чтение)");
                        }
                    }
                }

                if (!SameLines(before?.Comments, entry.Comments) && home != null && home.Writable)
                {
                    var target = Edit(home).GetOrAdd(key);
                    target.Comments.Clear();
                    target.Comments.AddRange(entry.Comments);
                }
            }

            // ключи, пропавшие из сводки мимо DeleteKey
            foreach (var entry in _baseline.Entries)
            {
                if (Merged.Contains(entry.Key)) continue;
                foreach (var file in FilesOf(entry.Key))
                    if (file.Writable) Edit(file).Remove(entry.Key);
            }

            // 3. языки
            SaveLanguages(Edit, result);

            if (!result.Ok) return result;

            // 4. запись
            foreach (var file in _files)
            {
                if (!file.Writable) continue;

                var table = tables.TryGetValue(file, out var edited) ? edited : file.Exists ? null : file.Table;
                if (table == null) continue;

                var text = LocFormat.Write(table);
                if (text == file.LoadedText) continue;

                try
                {
                    var dir = Path.GetDirectoryName(file.Info.FullPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(file.Info.FullPath, text, new UTF8Encoding(false));
                    result.Written.Add(file.Info.Path);
                }
                catch (Exception e)
                {
                    result.Errors.Add($"{file.Info.Path}: {e.Message}");
                }

                if (!file.Exists && !LocPackageCatalog.MatchesMasks(file.Info.Path) && !file.Info.IsBase)
                    result.Warnings.Add($"{file.Info.Path} не подходит под маски пакетов — добавьте маску, иначе редактор его не увидит");
            }

            foreach (var path in result.Written)
                if (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
                    AssetDatabase.ImportAsset(path);

            Reload();
            return result;
        }

        private void SaveLanguages(Func<File, LocTable> edit, SaveResult result)
        {
            var target = DefaultFile;

            // добавленные и изменённые
            foreach (var language in Merged.Languages)
            {
                var before = _baseline.GetLanguage(language.Code);
                if (before != null && before.Name == language.Name && before.Plural == language.Plural && before.Fallback == language.Fallback)
                    continue;

                var declared = false;
                foreach (var file in _files)
                {
                    if (!file.Writable || file.Table.GetLanguage(language.Code) == null) continue;

                    var header = edit(file).GetLanguage(language.Code);
                    if (header == null) continue;
                    header.Name = language.Name;
                    header.Plural = language.Plural;
                    header.Fallback = language.Fallback;
                    declared = true;
                }

                if (declared) continue;
                if (target == null)
                {
                    result.Errors.Add($"язык {language.Code}: нет записываемого файла для заголовка");
                    continue;
                }

                edit(target).AddLanguage(language.Code, language.Name, language.Plural, language.Fallback);
            }

            // снятые
            foreach (var language in _baseline.Languages)
            {
                if (Merged.GetLanguage(language.Code) != null) continue;

                foreach (var file in _files)
                {
                    if (file.Table.GetLanguage(language.Code) == null && !HasValues(file.Table, language.Code)) continue;
                    if (file.Writable) edit(file).RemoveLanguage(language.Code);
                    else result.Warnings.Add($"язык {language.Code} остался в {file.Info.Path} (только чтение)");
                }
            }

            // порядок — в базе (или файле по умолчанию)
            if (target == null || SameOrder(Merged, _baseline)) return;

            var table = edit(target);
            var position = 0;
            foreach (var language in Merged.Languages)
            {
                if (table.GetLanguage(language.Code) == null) table.AddLanguage(language.Code, language.Name, language.Plural, language.Fallback);
                table.MoveLanguage(language.Code, position++);
            }
        }

        private static bool HasValues(LocTable table, string language)
        {
            foreach (var entry in table.Entries)
                if (entry.Find(language) != null) return true;
            return false;
        }

        private static bool SameOrder(LocTable a, LocTable b)
        {
            if (a.Languages.Count != b.Languages.Count) return false;
            for (var i = 0; i < a.Languages.Count; i++)
                if (!LocSlots.Same(a.Languages[i].Code, b.Languages[i].Code)) return false;
            return true;
        }

        private static bool SameLines(List<string> a, List<string> b)
        {
            var countA = a?.Count ?? 0;
            var countB = b?.Count ?? 0;
            if (countA != countB) return false;
            for (var i = 0; i < countA; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static string SafeRead(string path)
        {
            try { return System.IO.File.ReadAllText(path); }
            catch (Exception) { return null; }
        }
    }
}
