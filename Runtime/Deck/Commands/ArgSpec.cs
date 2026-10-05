using System;

namespace Exerussus.AppCore.Deck
{
    /// <summary>Тип аргумента команды. AppCore разбирает и проверяет значение, смысл — у владельца команды.</summary>
    public enum ArgKind : byte
    {
        /// <summary>Целое (long внутри).</summary>
        Int = 0,

        /// <summary>Дробное. Принимает и точку, и запятую.</summary>
        Float = 1,

        /// <summary>true/false, 1/0, on/off, yes/no, да/нет.</summary>
        Bool = 2,

        /// <summary>Слово или строка в кавычках.</summary>
        String = 3,

        /// <summary>Одно из перечисленных значений (без учёта регистра).</summary>
        Choice = 4,

        /// <summary>
        /// Цель. Пропущенный аргумент или <c>$</c> — текущая выбранная цель; другое слово разрешается
        /// резолверами (<see cref="AppDeck.RegisterTargetResolver"/>), а само слово всегда доступно текстом
        /// (<c>all</c> и прочее команда решает сама).
        /// </summary>
        Target = 5,

        /// <summary>Весь остаток строки как есть. Только последним.</summary>
        Rest = 6,
    }

    /// <summary>
    /// Описание одного аргумента. Неизменяемо после создания: реестр раздаёт ссылку на него
    /// и автоподстановке, и форме кнопки, и разбору — копий нет.
    /// </summary>
    public sealed class ArgSpec
    {
        public readonly string Name;
        public readonly ArgKind Kind;

        /// <summary>Необязательный: можно не писать. Значение — <see cref="DefaultText"/> или «пусто».</summary>
        public readonly bool Optional;

        /// <summary>Значение по умолчанию в исходном виде (как его написал бы человек). null — нет.</summary>
        public readonly string DefaultText;

        public readonly string Description;

        /// <summary>Варианты для <see cref="ArgKind.Choice"/>. Для прочих — null.</summary>
        public readonly string[] Choices;

        /// <summary>Имя источника подсказок (<see cref="AppDeck.RegisterSuggestions"/>). null — нет.</summary>
        public readonly string Source;

        /// <summary>Разобранное значение по умолчанию. Считается один раз при регистрации.</summary>
        internal ArgValue DefaultValue;

        /// <summary>Есть ли разобранное значение по умолчанию.</summary>
        internal bool HasDefault;

        public ArgSpec(string name, ArgKind kind, bool optional = false, string defaultText = null,
                       string description = null, string[] choices = null, string source = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("У аргумента нет имени.", nameof(name));
            if (kind == ArgKind.Choice && (choices == null || choices.Length == 0))
                throw new ArgumentException($"Аргумент «{name}»: у Choice нет вариантов.", nameof(choices));

            Name = name;
            Kind = kind;
            DefaultText = defaultText;
            // значение по умолчанию делает аргумент необязательным само по себе;
            // цель всегда необязательна — её подставит текущий выбор
            Optional = optional || defaultText != null || kind == ArgKind.Target;
            Description = description;
            Choices = choices;
            Source = source;
        }

        /// <summary>Подпись для подсказки: <c>count:int=1</c>, <c>[mode:a|b]</c>.</summary>
        public string Display
        {
            get
            {
                if (_display != null) return _display;

                string type = Kind switch
                {
                    ArgKind.Int => "int",
                    ArgKind.Float => "float",
                    ArgKind.Bool => "bool",
                    ArgKind.Choice => string.Join("|", Choices),
                    ArgKind.Target => "target",
                    ArgKind.Rest => "text…",
                    _ => Source != null ? Source : "string",
                };

                string body = DefaultText != null ? $"{Name}:{type}={DefaultText}" : $"{Name}:{type}";
                _display = Optional ? $"[{body}]" : $"<{body}>";
                return _display;
            }
        }

        private string _display;
    }

    /// <summary>
    /// Разобранное значение аргумента. Структура лежит в переиспользуемом массиве контекста:
    /// числа не боксятся, строка — та, что уже есть (вариант Choice, источник, подстрока ввода).
    /// </summary>
    public struct ArgValue
    {
        public bool Present;
        public long Int;
        public double Float;
        public bool Bool;
        public string Text;
        public DeckTarget Target;
    }
}
