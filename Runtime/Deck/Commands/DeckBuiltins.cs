using System;
using System.Collections.Generic;
using System.Text;

namespace Exerussus.AppCore.Deck
{
    /// <summary>Встроенные команды AppDeck: справка, очистка, окно, цель.</summary>
    internal static class DeckBuiltins
    {
        public static void Register()
        {
            object owner = AppDeck.BuiltinOwner;

            // имена команд как источник подсказок: help <Tab>, кнопки со ссылкой на команду
            AppDeck.RegisterSuggestions("command", CollectCommands, owner);

            AppDeck.Command("help")
                   .Summary("Список команд или подробности одной")
                   .Arg("command", ArgKind.String, "Имя команды", optional: true, source: "command")
                   .Run(Help)
                   .Register(owner);

            AppDeck.Command("clear")
                   .Summary("Очистить консоль")
                   .Run(ctx => AppDeck.ClearLog())
                   .Register(owner);

            AppDeck.Command("echo")
                   .Summary("Напечатать текст")
                   .Arg("text", ArgKind.Rest, "Что напечатать")
                   .Run(ctx => ctx.Print(ctx.GetString(0)))
                   .Register(owner);

            AppDeck.Command("deck.layout")
                   .Summary("Размер окна: шторка или весь экран")
                   .Choice("layout", new[] { "partial", "full" }, "partial — мир виден ниже, full — весь экран")
                   .Run(ctx => AppDeck.Layout = ctx.GetInt(0) == 0 ? DeckLayout.Partial : DeckLayout.Full)
                   .Register(owner);

            AppDeck.Command("deck.close")
                   .Summary("Закрыть окно")
                   .Run(ctx => AppDeck.Close())
                   .Register(owner);

            AppDeck.Command("target")
                   .Summary("Показать выбранную цель")
                   .Run(ctx =>
                   {
                       DeckTarget t = AppDeck.Target;
                       ctx.Print(t.IsValid ? $"Цель: {t.Label} ({t.Value.GetType().Name}) в {t.Point}" : "Цель не выбрана.");
                   })
                   .Register(owner);

            AppDeck.Command("target.clear")
                   .Summary("Снять выбранную цель")
                   .Run(ctx => AppDeck.ClearTarget())
                   .Register(owner);
        }

        private static void CollectCommands(ReadOnlySpan<char> prefix, List<string> results, int max)
        {
            CommandRegistry registry = AppDeck.Commands;
            registry.PrefixRange(prefix, out int first, out int count);

            for (var i = 0; i < count && results.Count < max; i++)
            {
                CommandSpec spec = registry.Spec(registry.SortedSlot(first + i));
                if (!spec.Hidden) results.Add(spec.Name);
            }
        }

        private static void Help(CommandContext ctx)
        {
            CommandRegistry registry = AppDeck.Commands;
            string name = ctx.GetString(0);

            if (!string.IsNullOrEmpty(name))
            {
                int slot = registry.Find(name.AsSpan());
                if (slot < 0)
                {
                    ctx.Error($"Нет команды «{name}».");
                    return;
                }

                CommandSpec spec = registry.Spec(slot);
                var one = new StringBuilder();
                one.Append(spec.Signature);
                if (spec.Summary.Length > 0) one.Append("\n  ").Append(spec.Summary);

                for (var i = 0; i < spec.Args.Length; i++)
                {
                    ArgSpec arg = spec.Args[i];
                    one.Append("\n    ").Append(arg.Display);
                    if (!string.IsNullOrEmpty(arg.Description)) one.Append(" — ").Append(arg.Description);
                }

                ctx.Print(one.ToString());
                return;
            }

            var sb = new StringBuilder();
            var shown = 0;
            int total = registry.SortedCount;

            for (var i = 0; i < total; i++)
            {
                CommandSpec spec = registry.Spec(registry.SortedSlot(i));
                if (spec.Hidden) continue;

                if (shown > 0) sb.Append('\n');
                sb.Append(spec.Name);
                if (spec.Summary.Length > 0) sb.Append(" — ").Append(spec.Summary);
                shown++;
            }

            sb.Insert(0, $"Команд: {shown}. Подробно — help <команда>.\n");
            ctx.Print(sb.ToString());
        }
    }
}
