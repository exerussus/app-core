using System.Collections.Generic;

namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Проход по строке после перевода: род, подсветка ключевых слов, живые значения, стиль речи мода.
    /// Подключается извне, как слой: <see cref="Loc.AddPass"/> с id, стадией и владельцем.
    /// </summary>
    /// <remarks>
    /// Проходы работают только со строками по ключу (<c>^KEY</c>, <see cref="Loc.GetKey"/>):
    /// произвольный текст без ключа они не трогают.
    /// </remarks>
    public interface ILocPass
    {
        /// <summary>
        /// Нужна ли проходу строка и от чего она зависит. Зовётся на шаблоне один раз, результат
        /// кэшируется вместе с шаблоном (до смены языка, таблицы или <see cref="Loc.RefreshPass(string)"/>).
        /// Ничего не добавлено — проход строку не трогает и на показе не вызывается.
        /// </summary>
        /// <param name="key">Ключ без <c>^</c>.</param>
        /// <param name="pattern">Перевод (после проходов стадии Pattern, добавленных раньше).</param>
        /// <param name="dependencies">
        /// Куда сложить зависимости — имена, по которым проход потом зовёт
        /// <see cref="Loc.RefreshPass(string,string)"/>: «gold», «hero.gender». Строка нужна, а зависимостей
        /// нет — добавьте пустую строку: тогда её пересчитает только <see cref="Loc.RefreshPass(string)"/>.
        /// </param>
        void Wants(string key, string pattern, ICollection<string> dependencies);

        /// <summary>Доработать строку. Менять нечего — вернуть ту же ссылку.</summary>
        string Apply(in LocPassContext context, string text);
    }

    /// <summary>Стадия прохода.</summary>
    public enum LocPassStage
    {
        /// <summary>
        /// По шаблону до подстановки аргументов. Результат кэшируется вместе с переводом — почти бесплатно;
        /// состояние сменилось — <see cref="Loc.RefreshPass(string)"/>. Данные, которые задаёт игрок или мод,
        /// здесь вставляются только через <see cref="Loc.Escape"/>.
        /// </summary>
        Pattern = 0,

        /// <summary>По готовой строке после аргументов — на каждый показ. Для того, что меняется постоянно.</summary>
        Text = 1,
    }

    /// <summary>Что проход знает о строке.</summary>
    public readonly struct LocPassContext
    {
        /// <summary>Ключ без <c>^</c>.</summary>
        public readonly string Key;

        /// <summary>Язык, с которого строка пришла (при фолбэке — не текущий).</summary>
        public readonly string Language;

        /// <summary>Контекст биндера (<see cref="LocBinder.Context"/>) или <c>null</c> для строк из кода.</summary>
        public readonly object Owner;

        public readonly LocPassStage Stage;

        internal LocPassContext(string key, string language, object owner, LocPassStage stage)
        {
            Key = key;
            Language = language;
            Owner = owner;
            Stage = stage;
        }
    }

    /// <summary>Подключённый проход — для инструментов и AppDeck.</summary>
    public readonly struct LocPassInfo
    {
        public readonly string Id;
        public readonly LocPassStage Stage;
        public readonly object Owner;
        public readonly LocSource Source;

        /// <summary>Сборка, в которой объявлен проход.</summary>
        public readonly string Assembly;

        /// <summary>Сколько ключей проход хочет среди уже показанных.</summary>
        public readonly int Keys;

        public LocPassInfo(string id, LocPassStage stage, object owner, LocSource source, string assembly, int keys)
        {
            Id = id;
            Stage = stage;
            Owner = owner;
            Source = source;
            Assembly = assembly;
            Keys = keys;
        }
    }

    /// <summary>
    /// Резолвер ключа, которого нет ни в одном слое ни на одном языке цепочки фолбэков:
    /// <c>item.sword_x.name</c> → имя из каталога. True — текст найден.
    /// </summary>
    public delegate bool LocResolver(string key, string language, out string text);
}
