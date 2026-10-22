using System.Collections.Generic;

namespace Exerussus.AppCore.Localization
{
    /// <summary>Откуда пришёл слой. От источника зависят ограничения: моду нельзя то, что можно проекту.</summary>
    public enum LocSource
    {
        /// <summary>Встроенные строки AppCore (кнопки скринов).</summary>
        AppCore = 0,

        /// <summary>Базовая таблица проекта из настроек.</summary>
        Base = 1,

        /// <summary>Слой, добавленный кодом проекта. Ограничений нет.</summary>
        Project = 2,

        /// <summary>Слой мода. Удалять ключи по умолчанию нельзя, защищённые ключи не трогает.</summary>
        Mod = 3,
    }

    /// <summary>Форма числа (CLDR). <see cref="Other"/> — значение без формы.</summary>
    public enum PluralCategory
    {
        Zero = 0,
        One = 1,
        Two = 2,
        Few = 3,
        Many = 4,
        Other = 5,
    }

    /// <summary>Язык таблицы: заголовок <c>@language</c>.</summary>
    public sealed class LocLanguage
    {
        /// <summary>Код языка: <c>ru</c>, <c>en</c>, <c>pt-BR</c>.</summary>
        public string Code;

        /// <summary>Имя для переключателя языка, на самом языке: «Русский», «English».</summary>
        public string Name;

        /// <summary>Правило окончаний (<see cref="PluralRules"/>). Пусто — угадывается по коду.</summary>
        public string Plural;

        /// <summary>Язык, из которого брать непереведённое. Пусто — основной язык.</summary>
        public string Fallback;

        /// <summary>Строка заголовка в файле. 0 — язык создан кодом.</summary>
        public int Line;

        public LocLanguage() { }

        public LocLanguage(string code, string name, string plural = null, string fallback = null)
        {
            Code = code;
            Name = name;
            Plural = plural;
            Fallback = fallback;
        }

        public LocLanguage Clone() => new(Code, Name, Plural, Fallback) { Line = Line };

        public override string ToString() => string.IsNullOrEmpty(Name) ? Code : $"{Code} ({Name})";
    }

    /// <summary>Значение ключа на одном языке и в одной форме.</summary>
    public sealed class LocValue
    {
        public string Language;

        /// <summary>Форма числа. <c>null</c> — основное значение (оно же запасное для всех форм).</summary>
        public string Form;

        /// <summary>Текст. <c>null</c> — значение удаляется (<c>ru: ~</c>), это бывает только в патче.</summary>
        public string Text;

        public int Line;

        public bool IsRemove => Text == null;

        /// <summary>Ключ значения внутри записи: <c>ru</c> или <c>ru.few</c>.</summary>
        public string Slot => LocSlots.Make(Language, Form);

        public LocValue Clone() => new() { Language = Language, Form = Form, Text = Text, Line = Line };
    }

    /// <summary>Ключ со всеми значениями.</summary>
    public sealed class LocEntry
    {
        /// <summary>Ключ без <c>^</c>.</summary>
        public string Key;

        /// <summary>Патч удаляет ключ целиком (<c>KEY ~</c>). Значений у такой записи нет.</summary>
        public bool RemoveKey;

        public readonly List<LocValue> Values = new();

        /// <summary>Комментарии над ключом — писатель сохраняет их на месте.</summary>
        public readonly List<string> Comments = new();

        public int Line;

        public LocValue Find(string language, string form = null)
        {
            for (var i = 0; i < Values.Count; i++)
            {
                var value = Values[i];
                if (LocSlots.Same(value.Language, language) && LocSlots.SameForm(value.Form, form)) return value;
            }

            return null;
        }

        public LocEntry Clone()
        {
            var copy = new LocEntry { Key = Key, RemoveKey = RemoveKey, Line = Line };
            for (var i = 0; i < Values.Count; i++) copy.Values.Add(Values[i].Clone());
            copy.Comments.AddRange(Comments);
            return copy;
        }
    }

    /// <summary>Ошибка разбора: номер строки и что не так.</summary>
    public readonly struct LocError
    {
        public readonly int Line;
        public readonly string Message;

        public LocError(int line, string message)
        {
            Line = line;
            Message = message;
        }

        public override string ToString() => Line > 0 ? $"строка {Line}: {Message}" : Message;
    }

    /// <summary>Что слой сделал с одним значением.</summary>
    public enum LocStepKind
    {
        /// <summary>Значения не было — слой его поставил.</summary>
        Set = 0,

        /// <summary>Значение было — слой его заменил.</summary>
        Replace = 1,

        /// <summary>Слой удалил значение или ключ.</summary>
        Remove = 2,

        /// <summary>Слой пытался, но ему не дали (см. <see cref="LocStep.Reason"/>).</summary>
        Rejected = 3,

        /// <summary>На этом языке значения нет — показано с другого языка.</summary>
        Fallback = 4,

        /// <summary>Значения нет нигде — показана заглушка.</summary>
        Missing = 5,

        /// <summary>Проход изменил строку (<see cref="LocStep.Reason"/> — стадия).</summary>
        Pass = 6,

        /// <summary>Значение дал резолвер (<see cref="LocStep.LayerId"/> — его id).</summary>
        Resolved = 7,
    }

    /// <summary>Шаг истории ключа: кто, что и почему.</summary>
    public sealed class LocStep
    {
        public string LayerId;
        public LocSource Source;
        public LocStepKind Kind;
        public string Language;
        public string Form;

        /// <summary>Новое значение (для Set/Replace), показанное (для Fallback).</summary>
        public string Value;

        /// <summary>Что было до шага.</summary>
        public string Previous;

        /// <summary>Причина отказа или язык, с которого взят фолбэк.</summary>
        public string Reason;

        public override string ToString()
        {
            var slot = LocSlots.Make(Language, Form);
            return Kind switch
            {
                LocStepKind.Set => $"{LayerId}: {slot} = «{Value}»",
                LocStepKind.Replace => $"{LayerId}: {slot} «{Previous}» → «{Value}»",
                LocStepKind.Remove => $"{LayerId}: {slot} удалено (было «{Previous}»)",
                LocStepKind.Rejected => $"{LayerId}: {slot} отклонено — {Reason}",
                LocStepKind.Fallback => $"{slot}: нет значения, показано с {Reason} — «{Value}»",
                LocStepKind.Missing => $"{slot}: нет значения нигде — заглушка",
                LocStepKind.Pass => $"проход {LayerId} ({Reason}): «{Previous}» → «{Value}»",
                LocStepKind.Resolved => $"{slot}: нет в таблицах, значение дал резолвер {LayerId} — «{Value}»",
                _ => LayerId,
            };
        }
    }

    /// <summary>Значение, которое писали несколько слоёв: последний выиграл.</summary>
    public readonly struct LocOverride
    {
        public readonly string Key;
        public readonly string Slot;

        /// <summary>Слои в порядке записи. Последний — победитель.</summary>
        public readonly IReadOnlyList<string> Layers;

        public string Winner => Layers.Count > 0 ? Layers[Layers.Count - 1] : null;

        public LocOverride(string key, string slot, IReadOnlyList<string> layers)
        {
            Key = key;
            Slot = slot;
            Layers = layers;
        }
    }

    /// <summary>Слой как его видно снаружи.</summary>
    public readonly struct LocLayerInfo
    {
        public readonly string Id;
        public readonly LocSource Source;
        public readonly int Keys;
        public readonly string Path;

        /// <summary>Сколько изменений слоя отклонено (защита, удаления, разметка).</summary>
        public readonly int Rejected;

        public LocLayerInfo(string id, LocSource source, int keys, string path, int rejected)
        {
            Id = id;
            Source = source;
            Keys = keys;
            Path = path;
            Rejected = rejected;
        }

        public override string ToString() => $"{Id} [{Source}] ключей: {Keys}" + (Rejected > 0 ? $", отклонено: {Rejected}" : string.Empty);
    }

    /// <summary>Ключи значений внутри записи: <c>ru</c>, <c>ru.few</c>.</summary>
    public static class LocSlots
    {
        public static readonly string[] Forms = { "zero", "one", "two", "few", "many", "other" };

        public static string Make(string language, string form) =>
            string.IsNullOrEmpty(form) ? language : language + "." + form;

        public static bool Same(string a, string b) => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);

        public static bool SameForm(string a, string b) =>
            string.IsNullOrEmpty(a) ? string.IsNullOrEmpty(b) : string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);

        public static bool IsForm(string form)
        {
            for (var i = 0; i < Forms.Length; i++)
                if (string.Equals(Forms[i], form, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string FormOf(PluralCategory category) => Forms[(int)category];

        /// <summary>Порядок форм при записи: основное значение первым, дальше по CLDR.</summary>
        public static int FormOrder(string form)
        {
            if (string.IsNullOrEmpty(form)) return -1;
            for (var i = 0; i < Forms.Length; i++)
                if (string.Equals(Forms[i], form, System.StringComparison.OrdinalIgnoreCase)) return i;
            return Forms.Length;
        }

        /// <summary>Ключ допустим: латиница, цифры, <c>_</c>, <c>.</c>, <c>-</c>.</summary>
        public static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (var ch in key)
                if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-')) return false;
            return true;
        }

        /// <summary>Код языка допустим: латиница, цифры, <c>-</c>, <c>_</c>.</summary>
        public static bool IsValidLanguage(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            foreach (var ch in code)
                if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return false;
            return true;
        }
    }
}
