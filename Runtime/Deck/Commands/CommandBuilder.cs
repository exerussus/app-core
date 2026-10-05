using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Fluent-описание команды для C#:
    /// <code>
    /// AppDeck.Command("give")
    ///     .Summary("Выдать предмет герою")
    ///     .Arg("item", ArgKind.String, source: "item")
    ///     .Arg("count", ArgKind.Int, defaultText: "1")
    ///     .AsAction("Выдать", "cheat, items")
    ///     .Run(ctx => Give(ctx.GetString(0), ctx.GetInt(1)))
    ///     .Register(this);
    /// </code>
    /// Регистрация — разовая операция, поэтому строитель — обычный класс.
    /// </summary>
    public sealed class CommandBuilder
    {
        private readonly string _name;
        private readonly List<ArgSpec> _args = new List<ArgSpec>();
        private string _summary;
        private bool _hidden;
        private bool _asAction;
        private string _title;
        private string _tags;
        private CommandHandler _handler;

        internal CommandBuilder(string name) => _name = name;

        public CommandBuilder Summary(string summary)
        {
            _summary = summary;
            return this;
        }

        public CommandBuilder Arg(string name, ArgKind kind, string description = null, bool optional = false,
                                  string defaultText = null, string source = null)
        {
            _args.Add(new ArgSpec(name, kind, optional, defaultText, description, null, source));
            return this;
        }

        public CommandBuilder Choice(string name, string[] choices, string description = null, string defaultText = null)
        {
            _args.Add(new ArgSpec(name, ArgKind.Choice, false, defaultText, description, choices));
            return this;
        }

        /// <summary>Аргументы одной строкой — формат <see cref="CommandSignature"/>.</summary>
        public CommandBuilder Args(string signature)
        {
            _args.AddRange(CommandSignature.Parse(signature));
            return this;
        }

        /// <summary>Не показывать в подсказках и help.</summary>
        public CommandBuilder Hidden(bool hidden = true)
        {
            _hidden = hidden;
            return this;
        }

        /// <summary>Показать кнопкой во вкладке Actions.</summary>
        public CommandBuilder AsAction(string title = null, string tags = null)
        {
            _asAction = true;
            _title = title;
            _tags = tags;
            return this;
        }

        public CommandBuilder Run(CommandHandler handler)
        {
            _handler = handler;
            return this;
        }

        /// <summary>Перегрузка для команд без аргументов.</summary>
        public CommandBuilder Run(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _handler = _ => action();
            return this;
        }

        public CommandSpec Build() => new CommandSpec(_name, _summary, _args.ToArray(), _hidden, _asAction, _title, _tags);

        public CommandHandle Register(object owner = null)
        {
            if (_handler == null) throw new InvalidOperationException($"Команда «{_name}»: не задан обработчик (Run).");
            return AppDeck.Register(Build(), _handler, owner);
        }
    }
}
