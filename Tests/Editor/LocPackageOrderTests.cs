using System.Collections.Generic;
using System.Text.RegularExpressions;
using Exerussus.AppCore.Localization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Exerussus.AppCore.Tests
{
    /// <summary>
    /// Пакеты локализации: место группы владельца переживает пустую выдачу, авто-пакеты из настроек
    /// не занимают id слоёв AddLayer и пакетов проекта.
    /// </summary>
    public sealed class LocPackageOrderTests
    {
        private readonly object _owner = new();
        private readonly object _other = new();

        private const string Core = @"@language ru Русский

PKG_HELLO
    ru: Привет

PKG_ONLY_CORE
    ru: Ядро
";

        private const string Mod = @"@language ru Русский

PKG_HELLO
    ru: Здорово
";

        private const string Auto = @"@language ru Русский

PKG_HELLO
    ru: Авто
";

        private LocSettings _settings;
        private LocSettings _previous;
        private readonly List<Object> _created = new();

        [SetUp]
        public void SetUp()
        {
            // свои настройки без авто-пакетов: каталог задаёт тест (прежние — до создания: OnEnable мог бы занять место)
            _previous = LocSettings.Current;
            _settings = ScriptableObject.CreateInstance<LocSettings>();
            _created.Add(_settings);
            _settings.SetPackagesForTests(LocPackageLoading.Auto, new List<LocPackageRef>());
            LocSettings.OverrideForTests(_settings);

            Loc.ResetForTests();
            Loc.OverrideEnabled(true);
            Loc.UseLanguageForTests("ru");
        }

        [TearDown]
        public void TearDown()
        {
            Loc.ResetForTests();
            LocSettings.OverrideForTests(_previous);
            foreach (var obj in _created)
                if (obj != null) Object.DestroyImmediate(obj);
            _created.Clear();
        }

        // ------------------------------------------------------------ место группы

        [Test]
        public void Empty_list_keeps_group_place()
        {
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("a", Core) });
            Loc.SetPackages(_other, new[] { LocPackage.FromText("b", Mod) });
            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"));

            // пустая выдача снимает пакеты, но не место
            Assert.IsTrue(Loc.SetPackages(_owner, new LocPackage[0]));
            Assert.IsFalse(Loc.Has("PKG_ONLY_CORE"));
            Assert.AreEqual(0, Loc.PackagesOf(_owner).Count);

            // повторная выдача встаёт на прежнее место — под группой _other
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("a", Core) });
            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"));
            Assert.AreEqual("Ядро", Loc.Get("^PKG_ONLY_CORE"));
        }

        [Test]
        public void First_empty_list_reserves_place()
        {
            Assert.IsTrue(Loc.SetPackages(_owner, new LocPackage[0]));
            Loc.SetPackages(_other, new[] { LocPackage.FromText("b", Mod) });
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("a", Core) });

            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"), "место занято первой (пустой) выдачей");
        }

        [Test]
        public void Clear_releases_place()
        {
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("a", Core) });
            Loc.SetPackages(_other, new[] { LocPackage.FromText("b", Mod) });

            Assert.IsTrue(Loc.ClearPackages(_owner));
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("a", Core) });

            Assert.AreEqual("Привет", Loc.Get("^PKG_HELLO"), "после ClearPackages группа встаёт в конец");
        }

        [Test]
        public void Clear_of_empty_group_returns_false()
        {
            Loc.SetPackages(_owner, new LocPackage[0]);
            Assert.IsFalse(Loc.ClearPackages(_owner));
        }

        // ------------------------------------------------------------ авто-пакеты: занятые id

        [Test]
        public void Auto_package_with_id_of_layer_is_skipped()
        {
            Assert.IsTrue(Loc.AddLayerFromText("same", Mod, LocSource.Project, _other));

            UseAutoPackage("same");
            LogAssert.Expect(LogType.Warning, new Regex("Авто-пакет «same» пропущен"));
            Loc.Reload();

            Assert.AreEqual(0, Loc.PackagesOf(Loc.AutoPackagesOwner).Count);
            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"));
        }

        [Test]
        public void Auto_package_with_id_of_project_package_is_skipped()
        {
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("same", Mod) });

            UseAutoPackage("same");
            LogAssert.Expect(LogType.Warning, new Regex("Авто-пакет «same» пропущен"));
            Loc.Reload();

            Assert.AreEqual(0, Loc.PackagesOf(Loc.AutoPackagesOwner).Count);
            Assert.AreEqual(1, Loc.PackagesOf(_owner).Count);
            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"));
        }

        [Test]
        public void Auto_package_with_free_id_is_loaded_under_project_packages()
        {
            Loc.SetPackages(_owner, new[] { LocPackage.FromText("project", Mod) });

            UseAutoPackage("auto");
            Loc.Reload();

            Assert.AreEqual(1, Loc.PackagesOf(Loc.AutoPackagesOwner).Count);
            Assert.AreEqual("Здорово", Loc.Get("^PKG_HELLO"), "авто-пакеты — первыми, пакеты проекта поверх");
        }

        private void UseAutoPackage(string id)
        {
            var asset = new TextAsset(Auto);
            _created.Add(asset);
            _settings.SetPackagesForTests(LocPackageLoading.Auto, new List<LocPackageRef> { new LocPackageRef { id = id, asset = asset } });
        }
    }
}
