using System;
using System.Text;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Описание команды — чистые данные: имя, описание, аргументы. AppCore по нему разбирает
    /// строку, подсказывает на Tab и строит форму кнопки. Что команда делает — знает только её владелец.
    /// </summary>
    public sealed class CommandSpec
    {
        /// <summary>Имя без пробелов: <c>god</c>, <c>give.item</c>. Регистр не важен.</summary>
        public readonly string Name;

        public readonly string Summary;

        public readonly ArgSpec[] Args;

        /// <summary>Не показывать в подсказках и <c>help</c> (служебная). Выполнить всё равно можно.</summary>
        public readonly bool Hidden;

        /// <summary>
        /// Показать команду кнопкой во вкладке Actions. Аргументы, если есть, станут формой под кнопкой.
        /// </summary>
        public readonly bool AsAction;

        /// <summary>Заголовок кнопки. null — имя команды.</summary>
        public readonly string Title;

        /// <summary>Теги через запятую: <c>"cheat, economy"</c>. Для фильтра во вкладке Actions.</summary>
        public readonly string Tags;

        public CommandSpec(string name, string summary = null, ArgSpec[] args = null, bool hidden = false,
                           bool asAction = false, string title = null, string tags = null)
        {
            if (!IsValidName(name))
                throw new ArgumentException($"Недопустимое имя команды «{name}»: нужны буквы, цифры, «_», «.» или «-», без пробелов.", nameof(name));

            Name = name;
            Summary = summary ?? string.Empty;
            Args = args ?? Array.Empty<ArgSpec>();
            Hidden = hidden;
            AsAction = asAction;
            Title = title;
            Tags = tags;

            for (var i = 0; i < Args.Length; i++)
            {
                if (Args[i] == null) throw new ArgumentException($"Команда «{name}»: аргумент #{i} пуст.");
                if (Args[i].Kind == ArgKind.Rest && i != Args.Length - 1)
                    throw new ArgumentException($"Команда «{name}»: аргумент-остаток «{Args[i].Name}» должен быть последним.");

                for (var j = 0; j < i; j++)
                {
                    if (string.Equals(Args[i].Name, Args[j].Name, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"Команда «{name}»: два аргумента с именем «{Args[i].Name}».");
                }
            }
        }

        /// <summary>Сигнатура для подсказки и ошибок: <c>give &lt;item:item&gt; [count:int=1]</c>.</summary>
        public string Signature
        {
            get
            {
                if (_signature != null) return _signature;

                var sb = new StringBuilder(Name);
                for (var i = 0; i < Args.Length; i++) sb.Append(' ').Append(Args[i].Display);
                _signature = sb.ToString();
                return _signature;
            }
        }

        private string _signature;

        /// <summary>Сколько аргументов обязательны (подряд с начала — после первого необязательного все считаются необязательными).</summary>
        public int RequiredCount
        {
            get
            {
                var n = 0;
                while (n < Args.Length && !Args[n].Optional) n++;
                return n;
            }
        }

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            for (var i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-') continue;
                return false;
            }

            return true;
        }
    }
}
