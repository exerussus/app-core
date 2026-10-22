using System;
using System.Collections.Generic;

namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Правила окончаний: по числу выбирают форму (<see cref="PluralCategory"/>). Язык ссылается
    /// на правило в заголовке (<c>@language ru Русский plural=ru</c>); без ссылки правило
    /// угадывается по коду языка.
    /// </summary>
    /// <remarks>
    /// Встроены: <c>ru</c> (русский, украинский, белорусский), <c>pl</c>, <c>cs</c> (чешский,
    /// словацкий), <c>en</c> (большинство европейских), <c>fr</c> (0 и 1 — единственное),
    /// <c>none</c> (без форм: японский, китайский, корейский). Своё — <see cref="Register"/>.
    /// </remarks>
    public static class PluralRules
    {
        private static readonly Dictionary<string, Func<double, PluralCategory>> Rules = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ru"] = Slavic,
            ["pl"] = Polish,
            ["cs"] = Czech,
            ["en"] = English,
            ["fr"] = French,
            ["none"] = _ => PluralCategory.Other,
        };

        // Язык → правило, когда в заголовке plural= не задан.
        private static readonly Dictionary<string, string> ByLanguage = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ru"] = "ru", ["uk"] = "ru", ["be"] = "ru", ["sr"] = "ru", ["hr"] = "ru", ["bs"] = "ru",
            ["pl"] = "pl",
            ["cs"] = "cs", ["sk"] = "cs",
            ["fr"] = "fr", ["pt-BR"] = "fr",
            ["ja"] = "none", ["zh"] = "none", ["ko"] = "none", ["vi"] = "none", ["th"] = "none", ["id"] = "none",
        };

        public static IEnumerable<string> Names => Rules.Keys;

        public static bool Has(string rule) => !string.IsNullOrEmpty(rule) && Rules.ContainsKey(rule);

        /// <summary>Добавляет или заменяет правило. Звать до первого обращения к таблице.</summary>
        public static void Register(string rule, Func<double, PluralCategory> select)
        {
            if (string.IsNullOrEmpty(rule)) throw new ArgumentException("Нужно имя правила.", nameof(rule));
            Rules[rule] = select ?? throw new ArgumentNullException(nameof(select));
        }

        /// <summary>Правило для языка: явное из заголовка, иначе по коду, иначе <c>en</c>.</summary>
        public static string RuleFor(LocLanguage language)
        {
            if (language == null) return "en";
            if (Has(language.Plural)) return language.Plural;
            return RuleFor(language.Code);
        }

        public static string RuleFor(string code)
        {
            if (string.IsNullOrEmpty(code)) return "en";
            if (ByLanguage.TryGetValue(code, out var rule)) return rule;

            var dash = code.IndexOf('-');
            if (dash > 0 && ByLanguage.TryGetValue(code.Substring(0, dash), out rule)) return rule;

            return "en";
        }

        public static PluralCategory Select(string rule, double n)
        {
            if (string.IsNullOrEmpty(rule) || !Rules.TryGetValue(rule, out var select)) select = English;
            return select(Math.Abs(n));
        }

        private static bool IsInteger(double n) => Math.Abs(n - Math.Floor(n)) < 1e-9;

        private static PluralCategory Slavic(double n)
        {
            if (!IsInteger(n)) return PluralCategory.Other;

            var i = (long)n;
            var mod10 = i % 10;
            var mod100 = i % 100;

            if (mod10 == 1 && mod100 != 11) return PluralCategory.One;
            if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return PluralCategory.Few;
            return PluralCategory.Many;
        }

        private static PluralCategory Polish(double n)
        {
            if (!IsInteger(n)) return PluralCategory.Other;

            var i = (long)n;
            var mod10 = i % 10;
            var mod100 = i % 100;

            if (i == 1) return PluralCategory.One;
            if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return PluralCategory.Few;
            return PluralCategory.Many;
        }

        private static PluralCategory Czech(double n)
        {
            if (!IsInteger(n)) return PluralCategory.Many;

            var i = (long)n;
            if (i == 1) return PluralCategory.One;
            if (i is >= 2 and <= 4) return PluralCategory.Few;
            return PluralCategory.Other;
        }

        private static PluralCategory English(double n) =>
            IsInteger(n) && (long)n == 1 ? PluralCategory.One : PluralCategory.Other;

        private static PluralCategory French(double n) => n < 2 ? PluralCategory.One : PluralCategory.Other;
    }
}
