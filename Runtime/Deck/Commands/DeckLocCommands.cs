using System;
using System.Collections.Generic;
using System.Text;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Deck
{
    /// <summary>
    /// Команды локализации: язык, откуда значение, история патчей, непереведённое, слои.
    /// Регистрируются, только если локализация включена.
    /// </summary>
    internal static class DeckLocCommands
    {
        public static void Register()
        {
            if (!Loc.Enabled) return;

            object owner = AppDeck.BuiltinOwner;

            AppDeck.RegisterSuggestions("loc.key", CollectKeys, owner);
            AppDeck.RegisterSuggestions("loc.language", CollectLanguages, owner);

            AppDeck.Command("loc.lang")
                   .Summary("Текущий язык и список; с кодом — переключить")
                   .Arg("code", ArgKind.String, "Код языка", optional: true, source: "loc.language")
                   .Run(Language)
                   .Register(owner);

            AppDeck.Command("loc.where")
                   .Summary("Значение ключа и слой, который его поставил")
                   .Arg("key", ArgKind.String, "Ключ (с ^ или без)", source: "loc.key")
                   .Arg("lang", ArgKind.String, "Язык; по умолчанию текущий", optional: true, source: "loc.language")
                   .Run(Where)
                   .Register(owner);

            AppDeck.Command("loc.history")
                   .Summary("История ключа: какой патч что поставил, заменил, удалил, что отклонено")
                   .Arg("key", ArgKind.String, "Ключ (с ^ или без)", source: "loc.key")
                   .Arg("lang", ArgKind.String, "Язык; без него — все языки", optional: true, source: "loc.language")
                   .Run(History)
                   .Register(owner);

            AppDeck.Command("loc.missing")
                   .Summary("Ключи без перевода на языке и запрошенные, но отсутствующие")
                   .Arg("lang", ArgKind.String, "Язык; по умолчанию текущий", optional: true, source: "loc.language")
                   .Run(Missing)
                   .Register(owner);

            AppDeck.Command("loc.layers")
                   .Summary("Слои таблицы в порядке применения")
                   .Run(Layers)
                   .Register(owner);

            AppDeck.Command("loc.reload")
                   .Summary("Перечитать базовую таблицу и слои из файлов")
                   .Run(ctx =>
                   {
                       Loc.Reload();
                       ctx.Print($"Таблица перечитана: ключей {Count(Loc.Keys)}, слоёв {Loc.Layers.Count}.");
                   })
                   .Register(owner);
        }

        private static void Language(CommandContext ctx)
        {
            var code = ctx.GetString(0);
            if (!string.IsNullOrEmpty(code))
            {
                if (Loc.SetLanguage(code)) ctx.Print($"Язык: {Loc.Language}.");
                else ctx.Error($"Языка «{code}» нет.");
                return;
            }

            var sb = new StringBuilder();
            sb.Append($"Язык: {Loc.Language}, основной: {Loc.PrimaryLanguage}.");
            foreach (var language in Loc.Languages)
            {
                sb.Append("\n  ").Append(language.Code == Loc.Language ? "● " : "  ").Append(language);
                sb.Append("  окончания: ").Append(PluralRules.RuleFor(language));
                if (!string.IsNullOrEmpty(language.Fallback)) sb.Append(", фолбэк: ").Append(language.Fallback);
            }

            ctx.Print(sb.ToString());
        }

        private static void Where(CommandContext ctx)
        {
            var key = Loc.KeyOf(ctx.GetString(0));
            var language = ctx.GetString(1) ?? Loc.Language;

            if (!Loc.Has(key))
            {
                ctx.Error($"Ключа «{key}» нет.");
                return;
            }

            Loc.TryGet(key, out var value, language);
            var from = Loc.WhereFrom(key, language);
            ctx.Print(from != null
                ? $"{key} [{language}] = «{value}»\n  слой: {from}"
                : !Loc.TraceActive
                    ? $"{key} [{language}] = «{value}»\n  слой неизвестен: трассировка выключена (LocalizationSettings → Отладка → Trace)"
                    : $"{key} [{language}] = «{value}»\n  на этом языке значения нет — показано с фолбэка (loc.history {key} {language})");
        }

        private static void History(CommandContext ctx)
        {
            var key = Loc.KeyOf(ctx.GetString(0));
            var language = ctx.GetString(1);

            var steps = Loc.History(key, language);
            var sb = new StringBuilder();
            sb.Append($"История {key}").Append(language != null ? $" [{language}]" : string.Empty).Append(':');

            if (!Loc.TraceActive) sb.Append("\n  (трассировка выключена — LocalizationSettings → Отладка → Trace; виден только итог)");

            if (steps.Count == 0) sb.Append("\n  пусто — ключ ни одним слоем не задавался");
            foreach (var step in steps) sb.Append("\n  ").Append(step);

            ctx.Print(sb.ToString());
        }

        private static void Missing(CommandContext ctx)
        {
            var language = ctx.GetString(0) ?? Loc.Language;
            var untranslated = Loc.Untranslated(language);

            var sb = new StringBuilder();
            sb.Append($"Без перевода на {language}: {untranslated.Count}");
            for (var i = 0; i < untranslated.Count && i < 100; i++) sb.Append("\n  ").Append(untranslated[i]);
            if (untranslated.Count > 100) sb.Append($"\n  … и ещё {untranslated.Count - 100}");

            var requested = Loc.RequestedButMissing;
            if (requested.Count > 0)
            {
                sb.Append($"\nЗапрошены, но нет нигде: {requested.Count}");
                foreach (var key in requested) sb.Append("\n  ").Append(key);
            }

            ctx.Print(sb.ToString());
        }

        private static void Layers(CommandContext ctx)
        {
            var sb = new StringBuilder();
            var layers = Loc.Layers;
            sb.Append($"Слоёв: {layers.Count} (позже — главнее)");
            for (var i = 0; i < layers.Count; i++)
            {
                sb.Append($"\n  {i + 1}. ").Append(layers[i]);
                if (!string.IsNullOrEmpty(layers[i].Path)) sb.Append("  ").Append(layers[i].Path);
            }

            var overrides = Loc.Overrides();
            if (overrides.Count > 0) sb.Append($"\nПерекрытых значений: {overrides.Count}");

            ctx.Print(sb.ToString());
        }

        private static void CollectKeys(ReadOnlySpan<char> prefix, List<string> results, int max)
        {
            var text = prefix.ToString();
            if (text.Length > 0 && text[0] == Loc.Prefix) text = text.Substring(1);

            foreach (var key in Loc.Keys)
            {
                if (results.Count >= max) break;
                if (key.StartsWith(text, StringComparison.OrdinalIgnoreCase)) results.Add(key);
            }

            results.Sort(StringComparer.Ordinal);
        }

        private static void CollectLanguages(ReadOnlySpan<char> prefix, List<string> results, int max)
        {
            var text = prefix.ToString();
            foreach (var language in Loc.Languages)
            {
                if (results.Count >= max) break;
                if (language.Code.StartsWith(text, StringComparison.OrdinalIgnoreCase)) results.Add(language.Code);
            }
        }

        private static int Count(IEnumerable<string> items)
        {
            var n = 0;
            foreach (var _ in items) n++;
            return n;
        }
    }
}
