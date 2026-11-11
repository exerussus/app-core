using System.Collections.Generic;
using Exerussus.AppCore.Localization;
using NUnit.Framework;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// Проходы внутри <see cref="Loc.Batch"/>: кэш строк сбрасывается сразу при изменении состава проходов
    /// (номера проходов в слотах не указывают на чужой проход), событие — одно, на выходе.
    /// </summary>
    public sealed class LocBatchPassTests
    {
        private const string Table = @"@language ru Русский

TEST_PLAIN
    ru: Текст
";

        private readonly object _owner = new();

        private sealed class AppendPass : ILocPass
        {
            private readonly string _suffix;
            public AppendPass(string suffix) => _suffix = suffix;
            public void Wants(string key, string pattern, ICollection<string> dependencies) { if (key == "TEST_PLAIN") dependencies.Add(""); }
            public string Apply(in LocPassContext context, string text) => text + _suffix;
        }

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

        [Test]
        public void RemovePass_inside_batch_does_not_apply_foreign_pass()
        {
            Assert.IsTrue(Loc.AddPass("test.a", new AppendPass("A"), LocPassStage.Text, _owner));
            Assert.IsTrue(Loc.AddPass("test.b", new AppendPass("B"), LocPassStage.Text, _owner));
            Assert.AreEqual("ТекстAB", Loc.Get("^TEST_PLAIN"));

            var changed = 0;
            Loc.Changed += () => changed++;

            using (Loc.Batch())
            {
                Assert.IsTrue(Loc.RemovePass("test.a"));
                Assert.AreEqual("ТекстB", Loc.Get("^TEST_PLAIN"), "слот пересобран сразу, без снятого прохода");

                Assert.IsTrue(Loc.AddPass("test.c", new AppendPass("C"), LocPassStage.Text, _owner));
                Assert.AreEqual("ТекстBC", Loc.Get("^TEST_PLAIN"));
                Assert.AreEqual(0, changed, "событие отложено до конца пачки");
            }

            Assert.AreEqual(1, changed);
            Assert.AreEqual("ТекстBC", Loc.Get("^TEST_PLAIN"));
        }

        [Test]
        public void RemovePasses_by_owner_inside_batch_clears_text_passes()
        {
            var other = new object();
            Loc.AddPass("test.a", new AppendPass("A"), LocPassStage.Text, other);
            Loc.AddPass("test.b", new AppendPass("B"), LocPassStage.Text, _owner);
            Assert.AreEqual("ТекстAB", Loc.Get("^TEST_PLAIN"));

            using (Loc.Batch())
            {
                Assert.AreEqual(1, Loc.RemovePasses(other));
                Assert.AreEqual("ТекстB", Loc.Get("^TEST_PLAIN"));
            }
        }
    }
}
