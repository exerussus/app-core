using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Exerussus.AppCore.Editor.Localization;
using Exerussus.AppCore.Localization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// Пакеты локализации 6.2: нестрогий разбор, SetPackages (порядок, замена, снятие, занятые id),
    /// рабочая копия редактора (куда ложатся правки, перенос, переименование, конфликты), маски.
    /// </summary>
    public sealed class LocPackagesTests
    {
        private readonly object _owner = new();
        private readonly object _other = new();

        private const string Core = @"@language ru Русский
@language en English

PKG_HELLO
    ru: Привет
    en: Hello

PKG_ONLY_CORE
    ru: Ядро
";

        private const string Mod = @"@language ru Русский

PKG_HELLO
    ru: Здорово
";

        [SetUp]
        public void SetUp()
        {
            Loc.ResetForTests();
            Loc.OverrideEnabled(true);
            Loc.UseLanguageForTests("ru");
        }

        [TearDown]
        public void TearDown() => Loc.ResetForTests();

        // ------------------------------------------------------------ разбор

        [Test]
        public void Lenient_parse_keeps_good_lines_and_drops_empty_values()
        {
            var result = LocFormat.ParseLenient("@language ru\n\nA\n    ru: да\n\nB C\n\nD\n    ru:\n");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("да", result.Table.GetText("A", "ru"));
            Assert.IsFalse(result.Table.Contains("B"));
            Assert.IsNull(result.Table.Get("D")?.Find("ru"));
        }

        [Test]
        public void Strict_package_from_text_with_errors_is_null()
        {
            LogAssert.Expect(LogType.Error, new Regex("не принят"));
            Assert.IsNull(LocPackage.FromText("bad", "A B\n", LocSource.Project, lenient: false));
        }

        // ------------------------------------------------------------ SetPackages

        [Test]
        public void Later_package_wins_and_layers_stay_on_top()
        {
            Assert.IsTrue(Loc.SetPackages(_owner, new[]
            {
                LocPackage.FromText("core", Core),
                LocPackage.FromText("mod", Mod, LocSource.Mod),
            }));

            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"));
            Assert.AreEqual("Ядро", Loc.Get("^PKG_ONLY_CORE"));

            Assert.IsTrue(Loc.AddLayerFromText("patch", "PKG_HELLO\n    ru: Слой\n", LocSource.Project, _other));
            Assert.AreEqual("Слой", Loc.Get("^PKG_HELLO"), "слой AddLayer поверх пакетов");

            // Пакеты заменили — слой по-прежнему сверху.
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("core", Core) });
            Assert.AreEqual("Слой", Loc.Get("^PKG_HELLO"));

            Loc.RemoveLayers(_other);
            Assert.AreEqual("Привет", Loc.Get("^PKG_HELLO"));
        }

        [Test]
        public void SetPackages_replaces_whole_list_and_clear_removes()
        {
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("core", Core), LocPackage.FromText("mod", Mod) });
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("core", Core) });
            Assert.AreEqual("Привет", Loc.Get("^PKG_HELLO"));
            Assert.AreEqual(1, Loc.PackagesOf(_owner).Count);

            Assert.IsTrue(Loc.ClearPackages(_owner));
            Assert.IsFalse(Loc.Has("PKG_ONLY_CORE"));
        }

        [Test]
        public void Owner_groups_keep_first_position()
        {
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("a", Core) });
            Loc.SetPackages(_other, new[] { LocPackage.FromText("b", Mod) });
            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"));

            // Перевыдача первой группы не переносит её наверх.
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("a", Core) });
            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"));
        }

        [Test]
        public void Package_id_taken_by_layer_is_skipped()
        {
            Loc.AddLayerFromText("same", "PKG_X\n    ru: x\n", LocSource.Project, _other);
            LogAssert.Expect(LogType.Error, new Regex("занят"));
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("same", Core) });
            Assert.IsFalse(Loc.Has("PKG_ONLY_CORE"));
        }

        [Test]
        public void Packages_are_listed_as_package_layers()
        {
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("core", Core) });
            var found = false;
            foreach (var layer in Loc.Layers)
                if (layer.Id == "core") found = layer.IsPackage;
            Assert.IsTrue(found);
        }

        // ------------------------------------------------------------ рабочая копия

        private string _dir;

        private LocWorkspace Workspace(params (string name, string text)[] files)
        {
            _dir = LocPackageCatalog.ProjectRoot + "/Temp/LocPackagesTests";
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            Directory.CreateDirectory(_dir);

            var list = new List<LocPackageFile>();
            foreach (var (name, text) in files)
            {
                File.WriteAllText(_dir + "/" + name, text);
                list.Add(LocPackageFile.Create("Temp/LocPackagesTests/" + name));
            }

            return LocWorkspace.Load(list);
        }

        private string Read(string name) => File.ReadAllText(_dir + "/" + name);

        [Test]
        public void Edit_goes_to_file_of_effective_value()
        {
            var ws = Workspace(("core.loc", Core), ("mod.loc", Mod));
            Assert.AreEqual("Здорово", ws.Merged.GetText("PKG_HELLO", "ru"));
            Assert.AreEqual("mod.loc", Path.GetFileName(ws.OriginOf("PKG_HELLO", "ru").Info.Path));
            Assert.IsTrue(ws.IsOverridden("PKG_HELLO"));

            ws.Merged.Set("PKG_HELLO", "ru", "Хай");
            ws.Merged.Set("PKG_HELLO", "en", "Hi");
            Assert.IsTrue(ws.Save().Ok);

            StringAssert.Contains("ru: Хай", Read("mod.loc"));
            StringAssert.Contains("ru: Привет", Read("core.loc"));
            StringAssert.Contains("en: Hi", Read("core.loc"));
            StringAssert.DoesNotContain("en:", Read("mod.loc"));
        }

        [Test]
        public void New_key_goes_to_chosen_package_and_cleared_value_leaves_all()
        {
            var ws = Workspace(("core.loc", Core), ("mod.loc", Mod));
            var mod = ws.FindFile("Temp/LocPackagesTests/mod.loc");
            Assert.IsTrue(ws.AddKey("PKG_NEW", mod));
            ws.Merged.Set("PKG_NEW", "ru", "Новое");
            ws.Merged.Clear("PKG_HELLO", "ru");
            Assert.IsTrue(ws.Save().Ok);

            StringAssert.Contains("PKG_NEW", Read("mod.loc"));
            StringAssert.DoesNotContain("PKG_NEW", Read("core.loc"));
            StringAssert.DoesNotContain("Привет", Read("core.loc"));
            StringAssert.DoesNotContain("Здорово", Read("mod.loc"));
        }

        [Test]
        public void Rename_delete_and_move_apply_to_all_writable_files()
        {
            var ws = Workspace(("core.loc", Core), ("mod.loc", Mod));
            Assert.IsTrue(ws.RenameKey("PKG_HELLO", "PKG_HI"));
            Assert.IsTrue(ws.DeleteKey("PKG_ONLY_CORE"));
            Assert.IsTrue(ws.Save().Ok);

            StringAssert.Contains("PKG_HI", Read("core.loc"));
            StringAssert.Contains("PKG_HI", Read("mod.loc"));
            StringAssert.DoesNotContain("PKG_HELLO", Read("core.loc"));
            StringAssert.DoesNotContain("PKG_ONLY_CORE", Read("core.loc"));

            ws = LocWorkspace.Load(new List<LocPackageFile>
            {
                LocPackageFile.Create("Temp/LocPackagesTests/core.loc"), LocPackageFile.Create("Temp/LocPackagesTests/mod.loc"),
            });
            Assert.IsTrue(ws.MoveKey("PKG_HI", ws.FindFile("Temp/LocPackagesTests/core.loc")));
            Assert.IsTrue(ws.Save().Ok);
            StringAssert.Contains("ru: Здорово", Read("core.loc"));
            StringAssert.DoesNotContain("PKG_HI", Read("mod.loc"));
        }

        [Test]
        public void File_changed_on_disk_is_a_conflict()
        {
            var ws = Workspace(("core.loc", Core));
            File.WriteAllText(_dir + "/core.loc", Core + "\nPKG_EXTERNAL\n    ru: снаружи\n");
            ws.Merged.Set("PKG_HELLO", "ru", "Изнутри");

            var result = ws.Save();
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Conflicts.Count);
            StringAssert.Contains("снаружи", Read("core.loc"));
        }

        [Test]
        public void Read_only_file_is_never_written()
        {
            var ws = Workspace(("mod.loc", Mod), ("core.loc", Core), ("top.loc", "@language ru\n"));
            ws.Files[0].Info.ReadOnly = true;
            ws.Files[1].Info.ReadOnly = true;
            ws.Merged.Set("PKG_HELLO", "ru", "Перекрытие");

            var result = ws.Save();
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Warnings.Count);
            StringAssert.Contains("ru: Здорово", Read("mod.loc"));
            StringAssert.Contains("ru: Привет", Read("core.loc"));
            StringAssert.Contains("ru: Перекрытие", Read("top.loc"));
        }

        [Test]
        public void Edit_under_read_only_file_warns()
        {
            var ws = Workspace(("core.loc", Core), ("mod.loc", Mod));
            ws.Files[1].Info.ReadOnly = true;
            ws.Merged.Set("PKG_HELLO", "ru", "Под модом");

            var result = ws.Save();
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, result.Warnings.Count);
            StringAssert.Contains("ru: Здорово", Read("mod.loc"));
        }

        // ------------------------------------------------------------ маски

        [Test]
        public void Glob_masks()
        {
            var any = LocPackageCatalog.GlobRegex("Assets/**/Localization/*.loc");
            Assert.IsTrue(any.IsMatch("Assets/Localization/a.loc"));
            Assert.IsTrue(any.IsMatch("Assets/Game/Inv/Localization/inv.loc"));
            Assert.IsFalse(any.IsMatch("Assets/Game/Inv/Localization/sub/inv.loc"));

            var pack = LocPackageCatalog.GlobRegex("Assets/StreamingAssets/Core/*/phrases/*.loc");
            Assert.IsTrue(pack.IsMatch("Assets/StreamingAssets/Core/core.ui/phrases/core.ui.loc"));
            Assert.IsFalse(pack.IsMatch("Assets/StreamingAssets/Core/a/b/phrases/x.loc"));
        }
    }
}
