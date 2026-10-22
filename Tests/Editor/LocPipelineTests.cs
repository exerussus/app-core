using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Exerussus.AppCore.Localization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// Локализация 4.5: шаблоны, форматы, проходы, резолверы, GetKey, выключение на запуск.
    /// Каждый тест начинает с чистого Loc и своей таблицы поверх базовой проекта.
    /// </summary>
    public sealed class LocPipelineTests
    {
        private const string Table = @"@language ru Русский
@language en English

TEST_POINTS
    ru: Осталось очков: {count}
    en: Points left: {count}

TEST_COUNT
    ru: {count} шт.

TEST_ESCAPED
    ru: {{count}}

TEST_SECONDS
    ru: {seconds:0.0} с
    en: {seconds:0.0} s

TEST_GOLD_N0
    ru: {gold:N0}
    en: {gold:N0}

TEST_DAMAGE
    ru: {max:damage}

TEST_THREE_PARTS
    ru: {max:ShieldGuardCost:a}

TEST_PERCENT
    ru: {lvl:DrawSteady:%}

TEST_PLURAL_TOKEN
    ru: {count|монета|монеты|монет}

TEST_MIX
    ru: {0} и {name}

TEST_POSITIONAL
    ru: {0} из {1}

TEST_EN_ONLY
    en: Only english

TEST_GENDER
    ru: Ты пощадил{a:а}

TEST_GOLD_LINE
    ru: Золото: {gold}

TEST_PLAIN
    ru: Без проходов

TEST_COINS
    ru.one: {0} монета
    ru.few: {0} монеты
    ru: {0} монет
";

        private readonly object _owner = new();

        [SetUp]
        public void SetUp()
        {
            Loc.ResetForTests();
            Loc.OverrideEnabled(true);
            Assert.IsTrue(Loc.AddLayerFromText("test", Table, LocSource.Project, _owner));
            Loc.UseLanguageForTests("ru");
        }

        [TearDown]
        public void TearDown() => Loc.ResetForTests();

        // ------------------------------------------------------------ шаблоны

        [Test]
        public void Named_argument_is_substituted()
        {
            Assert.AreEqual("Осталось очков: 3", Loc.Format("^TEST_POINTS", ("count", 3)));
        }

        [Test]
        public void Named_token_without_argument_stays_and_does_not_log()
        {
            Assert.AreEqual("{count} шт.", Loc.Format("^TEST_COUNT", ("other", 1)));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Escaped_braces_are_literal()
        {
            Assert.AreEqual("{count}", Loc.Format("^TEST_ESCAPED", ("count", 3)));
        }

        [Test]
        public void Format_uses_language_culture()
        {
            Assert.AreEqual("1,3 с", Loc.Format("^TEST_SECONDS", ("seconds", 1.26f)));
            Loc.UseLanguageForTests("en");
            Assert.AreEqual("1.3 s", Loc.Format("^TEST_SECONDS", ("seconds", 1.26f)));
            Assert.AreEqual("12,345", Loc.Format("^TEST_GOLD_N0", ("gold", 12345)));
        }

        [Test]
        public void Suffix_outside_whitelist_keeps_token_even_with_argument()
        {
            Assert.AreEqual("{max:damage}", Loc.Format("^TEST_DAMAGE", ("max", 5)));
            Assert.AreEqual("{max:ShieldGuardCost:a}", Loc.Format("^TEST_THREE_PARTS", ("max", 5)));
            Assert.AreEqual("{lvl:DrawSteady:%}", Loc.Format("^TEST_PERCENT", ("lvl", 2)));
        }

        [Test]
        public void Plural_token_is_reserved_and_left_as_is()
        {
            Assert.AreEqual("{count|монета|монеты|монет}", Loc.Format("^TEST_PLURAL_TOKEN", ("count", 3)));
        }

        [Test]
        public void Positional_and_named_mix()
        {
            Assert.AreEqual("A и B", Loc.Format("^TEST_MIX", ("0", "A"), ("name", "B")));
            Assert.AreEqual("1 из 2", Loc.Format("^TEST_POSITIONAL", 1, 2));
        }

        [Test]
        public void Plural_forms_still_work()
        {
            Assert.AreEqual("21 монета", Loc.Plural("^TEST_COINS", 21));
            Assert.AreEqual("22 монеты", Loc.Plural("^TEST_COINS", 22));
            Assert.AreEqual("25 монет", Loc.Plural("^TEST_COINS", 25));
            Assert.AreEqual("11 монет", Loc.PluralKey("TEST_COINS", 11));
        }

        [Test]
        public void Template_is_parsed_once()
        {
            Loc.Format("^TEST_POINTS", ("count", 1));
            var parses = LocTemplate.Parses;
            for (var i = 0; i < 10; i++) Loc.Format("^TEST_POINTS", ("count", i));
            Assert.AreEqual(parses, LocTemplate.Parses);
        }

        [Test]
        public void Generic_format_allocates_only_the_result()
        {
            Loc.Format("^TEST_COUNT", ("count", 3));   // прогрев: слот, шаблон, делегат записи

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) Loc.Format("^TEST_COUNT", ("count", 3));
            var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

            // «3 шт.» — строка из пяти символов; упаковка int и массивы дали бы заметно больше.
            Assert.Less(perCall, 96, $"аллокаций на вызов: {perCall} Б");
        }

        // ------------------------------------------------------------ GetKey

        [Test]
        public void GetKey_returns_same_string_as_Get_without_allocations()
        {
            var a = Loc.Get("^TEST_PLAIN");
            var b = Loc.GetKey("TEST_PLAIN");
            Assert.AreSame(a, b);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                Loc.GetKey("TEST_PLAIN");
                Loc.Get("^TEST_PLAIN");
            }

            Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Test]
        public void TryGetKey_does_not_substitute_missing_text()
        {
            Assert.IsTrue(Loc.TryGetKey("TEST_PLAIN", out var text));
            Assert.AreEqual("Без проходов", text);
            Assert.IsFalse(Loc.TryGetKey("TEST_NOWHERE", out _));
        }

        [Test]
        public void TryGetKey_miss_is_cached()
        {
            var calls = 0;
            Loc.AddResolver("test.none", (string key, string language, out string text) =>
            {
                calls++;
                text = null;
                return false;
            }, _owner);

            Assert.IsFalse(Loc.TryGetKey("TEST_OPTIONAL", out _));
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) Loc.TryGetKey("TEST_OPTIONAL", out _);

            Assert.AreEqual(1, calls);
            Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // ------------------------------------------------------------ проходы

        private sealed class GenderPass : ILocPass
        {
            public bool Female;
            public int WantsCalls;
            private static readonly Regex Token = new(@"\{a:([^}]*)\}");

            public void Wants(string key, string pattern, ICollection<string> dependencies)
            {
                WantsCalls++;
                if (pattern.Contains("{a:")) dependencies.Add("hero.gender");
            }

            public string Apply(in LocPassContext context, string text) =>
                Token.Replace(text, m => Female ? m.Groups[1].Value : string.Empty);
        }

        private sealed class ValuesPass : ILocPass
        {
            public int Gold;

            public void Wants(string key, string pattern, ICollection<string> dependencies)
            {
                if (pattern.Contains("{gold}")) dependencies.Add("gold");
            }

            public string Apply(in LocPassContext context, string text) => text.Replace("{gold}", Gold.ToString());
        }

        private sealed class AppendPass : ILocPass
        {
            private readonly string _suffix;
            public AppendPass(string suffix) => _suffix = suffix;
            public void Wants(string key, string pattern, ICollection<string> dependencies) { if (key == "TEST_PLAIN") dependencies.Add(""); }
            public string Apply(in LocPassContext context, string text) => text + _suffix;
        }

        private sealed class ThrowingPass : ILocPass
        {
            public void Wants(string key, string pattern, ICollection<string> dependencies) { if (key == "TEST_PLAIN") dependencies.Add(""); }
            public string Apply(in LocPassContext context, string text) => throw new InvalidOperationException("boom");
        }

        private sealed class LinkPass : ILocPass
        {
            public void Wants(string key, string pattern, ICollection<string> dependencies) { if (key == "TEST_PLAIN") dependencies.Add(""); }
            public string Apply(in LocPassContext context, string text) => "<link=x>" + text + "</link>";
        }

        [Test]
        public void Pattern_pass_refreshes_only_its_strings()
        {
            var gender = new GenderPass();
            Assert.IsTrue(Loc.AddPass("test.gender", gender, LocPassStage.Pattern, _owner));

            Assert.AreEqual("Ты пощадил", Loc.Get("^TEST_GENDER"));
            Assert.AreEqual("Без проходов", Loc.Get("^TEST_PLAIN"));
            var wants = gender.WantsCalls;

            IReadOnlyCollection<string> refreshed = null;
            Loc.KeysRefreshed += keys => refreshed = keys;

            gender.Female = true;
            Loc.RefreshPass("test.gender");

            Assert.IsNotNull(refreshed);
            CollectionAssert.Contains(refreshed, "TEST_GENDER");
            CollectionAssert.DoesNotContain(refreshed, "TEST_PLAIN");

            Assert.AreEqual("Ты пощадила", Loc.Get("^TEST_GENDER"));
            Assert.AreEqual("Без проходов", Loc.Get("^TEST_PLAIN"));
            Assert.AreEqual(wants + 1, gender.WantsCalls, "пересчитан только TEST_GENDER");
        }

        [Test]
        public void Text_pass_runs_each_show_and_refreshes_by_dependency()
        {
            var values = new ValuesPass { Gold = 5 };
            Loc.AddPass("test.values", values, LocPassStage.Text, _owner);

            Assert.AreEqual("Золото: 5", Loc.Get("^TEST_GOLD_LINE"));
            values.Gold = 7;
            Assert.AreEqual("Золото: 7", Loc.Get("^TEST_GOLD_LINE"));

            IReadOnlyCollection<string> refreshed = null;
            Loc.KeysRefreshed += keys => refreshed = keys;
            Loc.RefreshPass("test.values", "gold");
            CollectionAssert.AreEquivalent(new[] { "TEST_GOLD_LINE" }, refreshed);
        }

        [Test]
        public void Passes_apply_in_order_and_are_removed_by_owner()
        {
            Loc.AddPass("test.a", new AppendPass("A"), LocPassStage.Pattern, _owner);
            Loc.AddPass("test.b", new AppendPass("B"), LocPassStage.Pattern, _owner);
            Assert.AreEqual("Без проходовAB", Loc.Get("^TEST_PLAIN"));

            Assert.AreEqual(2, Loc.RemovePasses(_owner));
            Assert.AreEqual(0, Loc.Passes.Count);
            Assert.AreEqual("Без проходов", Loc.Get("^TEST_PLAIN"));
        }

        [Test]
        public void Get_without_passes_does_not_allocate()
        {
            Loc.Get("^TEST_PLAIN");
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) Loc.Get("^TEST_PLAIN");
            Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Test]
        public void Failing_pass_is_skipped_and_logged_once()
        {
            Loc.AddPass("test.throw", new ThrowingPass(), LocPassStage.Text, _owner);

            LogAssert.Expect(LogType.Error, new Regex("test.throw"));
            Assert.AreEqual("Без проходов", Loc.Get("^TEST_PLAIN"));
            Assert.AreEqual("Без проходов", Loc.Get("^TEST_PLAIN"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Mod_pass_cannot_insert_forbidden_markup()
        {
            Loc.AddPass("mod.link", new LinkPass(), LocPassStage.Pattern, _owner, LocSource.Mod);
            Assert.AreEqual("Без проходов", Loc.Get("^TEST_PLAIN"));
        }

        [Test]
        public void Escape_doubles_braces()
        {
            Assert.AreEqual("{{gold}}", Loc.Escape("{gold}"));
            Assert.AreEqual("plain", Loc.Escape("plain"));
        }

        // ------------------------------------------------------------ резолверы

        [Test]
        public void Fallback_wins_over_resolver()
        {
            var calls = 0;
            Loc.AddResolver("test.catalog", (string key, string language, out string text) =>
            {
                calls++;
                text = "из каталога";
                return true;
            }, _owner);

            Assert.AreEqual("Only english", Loc.Get("^TEST_EN_ONLY"));
            Assert.AreEqual(0, calls);
        }

        [Test]
        public void Resolver_fills_missing_key_and_is_cached()
        {
            var calls = 0;
            Loc.AddResolver("test.catalog", (string key, string language, out string text) =>
            {
                calls++;
                text = key == "item.sword_x.name" ? "Меч X" : null;
                return text != null;
            }, _owner);

            Assert.AreEqual("Меч X", Loc.GetKey("item.sword_x.name"));
            Assert.AreEqual("Меч X", Loc.GetKey("item.sword_x.name"));
            Assert.AreEqual(1, calls);
            Assert.AreEqual(Loc.ResolverPrefix + "test.catalog", Loc.WhereFrom("item.sword_x.name"));

            Loc.RefreshResolvers();
            Loc.GetKey("item.sword_x.name");
            Assert.AreEqual(2, calls);
        }

        // ------------------------------------------------------------ выключение

        [Test]
        public void OverrideEnabled_false_keeps_keys_as_is()
        {
            Loc.ResetForTests();
            Loc.OverrideEnabled(false);

            Assert.AreEqual("^TEST_PLAIN", Loc.Get("^TEST_PLAIN"));
            Assert.AreEqual("TEST_PLAIN", Loc.GetKey("TEST_PLAIN"));
            Assert.IsFalse(Loc.AddLayerFromText("x", Table));
            Assert.IsFalse(Loc.AddPass("x", new AppendPass("!"), LocPassStage.Text));
            Assert.IsFalse(Loc.AddResolver("x", (string k, string l, out string t) => { t = null; return false; }));
            Assert.AreEqual(0, Loc.Layers.Count);
        }
    }
}
