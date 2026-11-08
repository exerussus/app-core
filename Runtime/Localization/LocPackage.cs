using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Пакет локализации — таблица одного модуля (пака, мода, фичи), которую кладут поверх базовой
    /// через <see cref="Loc.SetPackages"/>. Порядок пакетов задаёт тот, кто их отдаёт: поздний главнее.
    /// </summary>
    /// <remarks>
    /// Пакет из файла помнит путь: <see cref="Loc.Reload"/> перечитает его сам (горячая перезагрузка
    /// в редакторе). Таблицу после передачи в <see cref="Loc.SetPackages"/> не меняйте — она хранится как есть.
    /// </remarks>
    public sealed class LocPackage
    {
        /// <summary>Уникальный id: по нему видно в истории, какой пакет что поставил.</summary>
        public readonly string Id;

        public LocTable Table { get; internal set; }

        /// <summary>Кто дал пакет: от этого зависят ограничения (<see cref="LocSource.Mod"/> — ограничения модов).</summary>
        public readonly LocSource Source;

        /// <summary>Файл, из которого пакет прочитан. <c>null</c> — таблица собрана в коде.</summary>
        public readonly string Path;

        /// <summary>Нестрогое чтение: строки с ошибкой отбрасываются, остальное принимается.</summary>
        public readonly bool Lenient;

        /// <summary>Ошибки разбора (при нестрогом чтении — то, что отброшено).</summary>
        public readonly List<LocError> Errors = new();

        private LocPackage(string id, LocTable table, LocSource source, string path, bool lenient)
        {
            Id = id;
            Table = table;
            Source = source;
            Path = path;
            Lenient = lenient;
        }

        /// <summary>Пакет из готовой таблицы.</summary>
        public static LocPackage FromTable(string id, LocTable table, LocSource source = LocSource.Project) =>
            new(id, table ?? new LocTable(), source, null, false);

        /// <summary>
        /// Пакет из текста <c>.loc</c>. Строго — текст с ошибками не принимается (<c>null</c>); нестрого —
        /// берётся всё, что разобралось, ошибки — в <see cref="Errors"/>.
        /// </summary>
        public static LocPackage FromText(string id, string text, LocSource source = LocSource.Project, bool lenient = true)
        {
            var package = new LocPackage(id, null, source, null, lenient);
            return package.Parse(text, id) ? package : null;
        }

        /// <summary>
        /// Пакет из файла <c>.loc</c>. Путь запоминается для <see cref="Loc.Reload"/>. Файл не читается
        /// или (строго) с ошибками — <c>null</c>, причина в логе.
        /// </summary>
        public static LocPackage FromFile(string id, string path, LocSource source = LocSource.Project, bool lenient = true)
        {
            var package = new LocPackage(id, null, source, path, lenient);
            return package.Read() ? package : null;
        }

        /// <summary>Перечитать файл. False — файла нет или (строго) ошибки; таблица остаётся прежней.</summary>
        internal bool Read()
        {
            if (string.IsNullOrEmpty(Path)) return Table != null;

            string text;
            try
            {
                text = File.ReadAllText(Path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Loc] Пакет «{Id}»: не читается файл {Path} — {e.Message}");
                return false;
            }

            return Parse(text, System.IO.Path.GetFileName(Path));
        }

        private bool Parse(string text, string source)
        {
            var parsed = Lenient ? LocFormat.ParseLenient(text) : LocFormat.Parse(text);

            if (!parsed.Ok && !Lenient)
            {
                Debug.LogError($"[Loc] Пакет «{Id}» не принят — ошибки:\n{parsed.ErrorText(source)}");
                return false;
            }

            Errors.Clear();
            Errors.AddRange(parsed.Errors);
            if (parsed.Errors.Count > 0)
                Debug.LogWarning($"[Loc] Пакет «{Id}»: строки с ошибками пропущены:\n{parsed.ErrorText(source)}");

            Table = parsed.Table;
            return true;
        }

        public override string ToString() => string.IsNullOrEmpty(Path) ? $"{Id} [{Source}]" : $"{Id} [{Source}] {Path}";
    }
}
