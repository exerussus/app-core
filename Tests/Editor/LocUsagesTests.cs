using System.Collections.Generic;
using System.Linq;
using System.Text;
using Exerussus.AppCore.Editor.Localization;
using Exerussus.AppCore.Localization;
using NUnit.Framework;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// Разбор употреблений ключей без файловой системы: «^KEY» в коде, вёрстке и YAML, комментарии C#,
    /// префиксы собираемых ключей, шаблоны «только известные», одно место — одно употребление, переименование.
    /// </summary>
    public sealed class LocUsagesTests
    {
        private static readonly string[] Known =
        {
            "MENU_PLAY", "MENU_QUIT", "ui.menu.play", "ui.menu.quit", "alchemy.why.fire", "hp_low", "a-b", "a.b", "a_b",
        };

        private const string Tr = @"Tr\(\s*""(?<key>[^""]+)""";

        private static LocUsagePattern Pattern(string regex, bool knownOnly = false, LocUsageReplace replace = LocUsageReplace.Key) =>
            new() { name = "p", regex = regex, extensions = ".cs", knownOnly = knownOnly, replace = replace };

        private static LocUsages.ScanSetup Setup(params LocUsagePattern[] patterns) =>
            LocUsages.Setup(null, patterns, null, new HashSet<string>(Known), null);

        private static List<LocUsage> Scan(string text, string file, params LocUsagePattern[] patterns) =>
            LocUsages.ScanText(text, file, Setup(patterns));

        private static string Keys(string text, string file, params LocUsagePattern[] patterns) =>
            string.Join(",", Scan(text, file, patterns).Where(u => u.Kind != LocUsages.PrefixKind).Select(u => u.Key));

        private static string Prefixes(string text, string file, params LocUsagePattern[] patterns) =>
            string.Join(",", Scan(text, file, patterns).Where(u => u.Kind == LocUsages.PrefixKind).Select(u => u.Key));

        // ------------------------------------------------------------ «^KEY» в коде

        [TestCase("var a = \"^MENU_PLAY\";", "MENU_PLAY")]
        [TestCase("var a = \"^MENU_PLAY\"; var b = \"^ui.menu.quit\";", "MENU_PLAY,ui.menu.quit")]
        [TestCase("var r = new Regex(\"^Tr\\\\(\");", "")]
        [TestCase("var r = new Regex(\"^(?<l>\\\\w+)\");", "")]
        [TestCase("var r = \"^MENU_PLAY \";", "")]
        [TestCase("var x = a ^ b;", "")]
        public void Caret_in_code_counts_only_whole_literal(string code, string expected) =>
            Assert.AreEqual(expected, Keys(code, "Assets/A.cs"));

        // ------------------------------------------------------------ комментарии

        [TestCase("// \"^MENU_PLAY\"\nvar x = 1;", "")]
        [TestCase("/* \"^MENU_PLAY\" */ var x = 1;", "")]
        [TestCase("/// <summary>\"^MENU_PLAY\" и L.MENU_QUIT</summary>\nvoid M() { }", "")]
        [TestCase("var s = \"// не комментарий\"; var t = \"^MENU_PLAY\";", "MENU_PLAY")]
        [TestCase("var s = @\"a \"\" // b\"; var t = \"^MENU_PLAY\";", "MENU_PLAY")]
        [TestCase("var s = $\"{(x ? \"/*\" : \"y\")}\"; var t = \"^MENU_PLAY\"; // */", "MENU_PLAY")]
        [TestCase("var s = $@\"{a}\"\" /* \"; var t = \"^MENU_PLAY\";", "MENU_PLAY")]
        [TestCase("var c = '\"'; var d = '/'; var t = \"^MENU_PLAY\";", "MENU_PLAY")]
        [TestCase("var s = \"\"\"\n// \"\"\"; var t = \"^MENU_PLAY\";", "MENU_PLAY")]
        [TestCase("Use(L.MENU_PLAY); // L.MENU_QUIT", "MENU_PLAY")]
        public void Comments_are_not_usages(string code, string expected) =>
            Assert.AreEqual(expected, Keys(code, "Assets/A.cs"));

        [Test]
        public void Strip_comments_keeps_offsets_and_line_breaks()
        {
            const string code = "a // x\r\nb /* y\nz */ c \"//\"";
            var stripped = LocUsages.StripComments(code);
            Assert.AreEqual(code.Length, stripped.Length);
            Assert.AreEqual("a     \r\nb     \n     c \"//\"", stripped);
            Assert.AreSame("var a = 1;", LocUsages.StripComments("var a = 1;"));
        }

        // ------------------------------------------------------------ вёрстка и YAML

        [TestCase("<ui:Label text=\"^MENU_PLAY\" />", "MENU_PLAY")]
        [TestCase("<ui:Label text=\"10^MENU_PLAY\" />", "")]
        public void Caret_in_uxml(string uxml, string expected) =>
            Assert.AreEqual(expected, Keys(uxml, "Assets/A.uxml"));

        [TestCase("%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  text: ^MENU_PLAY\n", "MENU_PLAY")]
        [TestCase("%YAML 1.1\r\n  text: ^MENU_PLAY\r\n  q: '^ui.menu.quit'\r\n", "MENU_PLAY,ui.menu.quit")]
        [TestCase("%YAML 1.1\n  list:\n  - \"^hp_low\"\n", "hp_low")]
        [TestCase("%YAML 1.1\n  regex: ^Tr\\(\n", "")]
        [TestCase("%YAML 1.1\n  regex: ^MENU_PLAY\\s*\n", "")]
        [TestCase("%YAML 1.1\n  text: 10^MENU_PLAY\n", "")]
        [TestCase("%YAML 1.1\n  text: ^MENU_PLAY и ещё\n", "")]
        [TestCase("бинарный ассет: ^MENU_PLAY\n", "")]
        public void Caret_in_yaml_counts_only_whole_field_value(string yaml, string expected) =>
            Assert.AreEqual(expected, Keys(yaml, "Assets/A.asset"));

        // ------------------------------------------------------------ префиксы

        [TestCase("Tr(\"alchemy.why.\" + id);", false, "alchemy.why.", "")]
        [TestCase("Tr(\"hp_\" + id);", false, "hp_", "")]
        [TestCase("Tr(\"a-\" + id);", false, "a-", "")]
        [TestCase("Tr(\"zzz.\" + id);", false, "", "zzz.")]
        [TestCase("Tr(\"ui.menu.\" + id);", true, "ui.menu.", "")]
        [TestCase("Tr(\"zzz.\" + id);", true, "", "")]
        [TestCase("Tr(\"ui.menu.play\"); Tr(\"zzz\");", true, "", "ui.menu.play")]
        public void Prefix_counts_only_when_a_known_key_starts_with_it(string code, bool knownOnly, string prefixes, string keys)
        {
            var pattern = Pattern(Tr, knownOnly);
            Assert.AreEqual(prefixes, Prefixes(code, "Assets/A.cs", pattern));
            Assert.AreEqual(keys, Keys(code, "Assets/A.cs", pattern));
        }

        [Test]
        public void Same_place_found_by_two_patterns_counts_once()
        {
            var call = Pattern(Tr);
            var literal = Pattern(@"""(?<key>[a-z]+\.[a-z.]+)""", knownOnly: true);
            var usages = Scan("Tr(\"ui.menu.play\"); var k = \"ui.menu.quit\";", "Assets/A.cs", call, literal);
            Assert.AreEqual("ui.menu.play,ui.menu.quit", string.Join(",", usages.Select(u => u.Key)));
        }

        [Test]
        public void Identifier_pattern_maps_to_key()
        {
            var pattern = Pattern(@"Keys\.(?<key>\w+)", replace: LocUsageReplace.Identifier);
            var code = "Use(Keys." + LocEditor.IdentifierOf("ui.menu.play") + ");";
            Assert.AreEqual("ui.menu.play", Keys(code, "Assets/A.cs", pattern));
        }

        // ------------------------------------------------------------ употреблён / неизвестен

        [Test]
        public void Used_unknown_and_unused_follow_one_rule()
        {
            var table = LocFormat.ParseLenient("@language ru\n\nUSED\n    ru: а\n\nalchemy.why.fire\n    ru: б\n\nIDLE\n    ru: в\n\n" +
                                               "APPCORE_TEST\n    ru: г\n").Table;
            var index = new LocUsageIndex();
            index.Add(new LocUsage { Key = "USED", File = "Assets/A.cs" });
            index.Add(new LocUsage { Key = "MISSING", File = "Assets/A.cs" });
            index.Add(new LocUsage { Key = "APPCORE_OTHER", File = "Assets/A.cs" });
            index.AddPrefix("alchemy.why.");

            CollectionAssert.AreEqual(new[] { "MISSING" }, index.Unknown(table, null));
            CollectionAssert.AreEqual(new[] { "IDLE" }, index.Unused(table, null));
            Assert.IsTrue(index.IsUsed("alchemy.why.fire", null));
            Assert.IsTrue(index.IsAssembled("alchemy.why.fire", null));
            Assert.IsFalse(index.IsUsed("IDLE", null));
            Assert.IsTrue(index.Complete);
        }

        // ------------------------------------------------------------ переименование

        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        private static byte[] Utf8(string text, bool bom) =>
            bom ? Bom.Concat(Encoding.UTF8.GetBytes(text)).ToArray() : Encoding.UTF8.GetBytes(text);

        [TestCase(true)]
        [TestCase(false)]
        public void Rename_keeps_bom_line_breaks_and_comments(bool bom)
        {
            var spec = LocUsages.Spec("MENU_PLAY", "MENU_START", Setup());
            var plan = new List<LocUsages.RenameChange>();
            var errors = new List<string>();
            var output = LocUsages.RenameInBytes(Utf8("var a = \"^MENU_PLAY\"; // \"^MENU_PLAY\"\r\nUse(L.MENU_PLAY);\r\n", bom),
                                                 "Assets/A.cs", spec, plan, errors, new List<string>());

            Assert.IsNotNull(output);
            Assert.AreEqual(bom, output.Take(3).SequenceEqual(Bom));
            var text = Encoding.UTF8.GetString(output, bom ? 3 : 0, output.Length - (bom ? 3 : 0));
            Assert.AreEqual("var a = \"^MENU_START\"; // \"^MENU_PLAY\"\r\nUse(L.MENU_START);\r\n", text);
            Assert.AreEqual(2, plan.Count);
            CollectionAssert.IsEmpty(errors);
        }

        [Test]
        public void Rename_skips_ambiguous_constant_and_identifier_pattern()
        {
            var name = LocEditor.IdentifierOf("a.b");
            var pattern = Pattern(@"Keys\.(?<key>\w+)", replace: LocUsageReplace.Identifier);
            var spec = LocUsages.Spec("a.b", "c.d", Setup(pattern));
            var errors = new List<string>();
            var output = LocUsages.RenameInBytes(Utf8($"Use(L.{name}); Use(Keys.{name});", false), "Assets/A.cs", spec,
                                                 new List<LocUsages.RenameChange>(), errors, null);

            Assert.IsNull(output);
            Assert.AreEqual(2, errors.Count);
        }

        [Test]
        public void Rename_warns_about_key_assembled_by_prefix()
        {
            var spec = LocUsages.Spec("alchemy.why.fire", "alchemy.why.flame", Setup(Pattern(Tr)));
            var warnings = new List<string>();
            LocUsages.RenameInBytes(Utf8("Tr(\"alchemy.why.\" + id);", false), "Assets/A.cs", spec, new List<LocUsages.RenameChange>(),
                                    new List<string>(), warnings);

            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains("«alchemy.why.»", warnings[0]);
        }

        [Test]
        public void Rename_leaves_non_utf8_and_binary_assets()
        {
            var spec = LocUsages.Spec("MENU_PLAY", "MENU_START", Setup());
            var errors = new List<string>();
            Assert.IsNull(LocUsages.RenameInBytes(new byte[] { 0x22, 0x5E, 0xFF, 0x22 }, "Assets/A.cs", spec, null, errors, null));
            Assert.AreEqual(1, errors.Count);
            Assert.IsNull(LocUsages.RenameInBytes(Utf8("text: ^MENU_PLAY\n", false), "Assets/A.asset", spec, null, errors, null));
        }

        [TestCase("  ^MENU_PLAY ", "MENU_PLAY")]
        [TestCase("^^ui.menu play", "ui.menu_play")]
        public void Normalize_key(string input, string expected) => Assert.AreEqual(expected, LocUsages.NormalizeKey(input));
    }
}
