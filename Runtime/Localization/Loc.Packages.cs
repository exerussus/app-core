using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Exerussus.AppCore.Localization
{
    // Пакеты: таблицы модулей поверх базовой. Порядок слоёв: appcore → base → пакеты (группы владельцев
    // в порядке первой выдачи, внутри — порядок списка) → слои AddLayer.
    public static partial class Loc
    {
        /// <summary>Владелец пакетов, которые AppCore кладёт сам (<see cref="LocPackageLoading.Auto"/>).</summary>
        public static readonly object AutoPackagesOwner = new AutoOwner();

        private sealed class AutoOwner
        {
            public override string ToString() => "appcore.packages";
        }

        private sealed class PackageGroup
        {
            public object Owner;
            public readonly List<Layer> Layers = new();
        }

        private static readonly List<PackageGroup> PackageGroups = new();

        /// <summary>
        /// Заменяет все пакеты владельца новым списком — одной пересборкой и одним <see cref="Changed"/>.
        /// Порядок списка — порядок применения (поздний главнее). Место группы среди других владельцев
        /// сохраняется с первой выдачи. Пустой список — то же, что <see cref="ClearPackages"/>.
        /// </summary>
        /// <param name="owner">Владелец (загрузчик паков). Обязателен.</param>
        /// <param name="packages">Пакеты; <c>null</c>-элементы пропускаются (не прочитанный файл).</param>
        /// <returns>False — локализация выключена или нет владельца. Пакет с занятым id пропускается с ошибкой в логе.</returns>
        public static bool SetPackages(object owner, IReadOnlyList<LocPackage> packages)
        {
            if (owner == null)
            {
                Debug.LogError("[Loc] SetPackages: нужен владелец — по нему пакеты заменяются и снимаются.");
                return false;
            }

            if (!EnsureInitialized()) return false;

            var group = PackageGroups.Find(g => ReferenceEquals(g.Owner, owner));
            if (group == null)
            {
                if (packages == null || packages.Count == 0) return true;

                group = new PackageGroup { Owner = owner };
                PackageGroups.Add(group);
            }

            group.Layers.Clear();

            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (packages != null)
            {
                for (var i = 0; i < packages.Count; i++)
                {
                    var package = packages[i];
                    if (package == null || package.Table == null) continue;

                    if (string.IsNullOrWhiteSpace(package.Id))
                    {
                        Debug.LogError("[Loc] Пакет без id пропущен — по id видно в истории, кто что изменил.");
                        continue;
                    }

                    if (!ids.Add(package.Id) || IsLayerIdTaken(package.Id, group))
                    {
                        Debug.LogError($"[Loc] Пакет «{package.Id}» пропущен: id занят другим слоем или пакетом.");
                        continue;
                    }

                    if (package.Source == LocSource.AppCore || package.Source == LocSource.Base)
                    {
                        Debug.LogError($"[Loc] Пакет «{package.Id}»: источник {package.Source} зарезервирован, используйте Project или Mod.");
                        continue;
                    }

                    group.Layers.Add(new Layer
                    {
                        Id = package.Id, Source = package.Source, Owner = owner, Table = package.Table,
                        Path = package.Path, Package = package,
                    });
                }
            }

            if (group.Layers.Count == 0) PackageGroups.Remove(group);

            Invalidate();
            return true;
        }

        /// <summary>Снимает все пакеты владельца. False — их не было.</summary>
        public static bool ClearPackages(object owner)
        {
            if (!_initialized || owner == null) return false;

            var removed = PackageGroups.RemoveAll(g => ReferenceEquals(g.Owner, owner));
            if (removed == 0) return false;

            Invalidate();
            return true;
        }

        /// <summary>Пакеты владельца в порядке применения. Пусто — нет.</summary>
        public static List<LocPackage> PackagesOf(object owner)
        {
            var list = new List<LocPackage>();
            var group = PackageGroups.Find(g => ReferenceEquals(g.Owner, owner));
            if (group == null) return list;

            foreach (var layer in group.Layers)
                if (layer.Package != null) list.Add(layer.Package);
            return list;
        }

        private static bool IsLayerIdTaken(string id, PackageGroup except)
        {
            if (id == AppCoreLayerId || id == BaseLayerId) return true;
            if (External.Exists(l => l.Id == id)) return true;

            foreach (var group in PackageGroups)
            {
                if (group == except) continue;
                if (group.Layers.Exists(l => l.Id == id)) return true;
            }

            return false;
        }

        private static IEnumerable<Layer> PackageLayers()
        {
            foreach (var group in PackageGroups)
            foreach (var layer in group.Layers)
                yield return layer;
        }

        /// <summary>Перечитывает пакеты, у которых есть файл (горячая перезагрузка).</summary>
        private static void ReloadPackages()
        {
            foreach (var layer in PackageLayers())
            {
                if (layer.Package == null || string.IsNullOrEmpty(layer.Package.Path)) continue;
                if (layer.Package.Read()) layer.Table = layer.Package.Table;
            }
        }

        // ================================================================ Auto: пакеты из настроек

        /// <summary>Кладёт пакеты, запечённые в настройки, если их грузит AppCore (<see cref="LocPackageLoading.Auto"/>).</summary>
        private static void LoadAutoPackages(bool rebuild)
        {
            var settings = Settings;
            var group = PackageGroups.Find(g => ReferenceEquals(g.Owner, AutoPackagesOwner));
            if (group != null) PackageGroups.Remove(group);

            if (settings == null || settings.PackageLoading != LocPackageLoading.Auto || settings.Packages.Count == 0)
            {
                if (group != null && rebuild) Invalidate();
                return;
            }

            group = new PackageGroup { Owner = AutoPackagesOwner };
            // Авто-пакеты — первыми среди пакетов: пакеты проекта (если есть) кладутся поверх.
            PackageGroups.Insert(0, group);

            var lenient = settings.LenientPackages;
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var reference in settings.Packages)
            {
                if (reference == null || string.IsNullOrEmpty(reference.id) || !ids.Add(reference.id)) continue;

                LocPackage package = null;
                if (reference.asset != null)
                {
                    package = LocPackage.FromText(reference.id, reference.asset.text, LocSource.Project, lenient);
                }
                else if (!string.IsNullOrEmpty(reference.streamingPath))
                {
                    var path = Path.Combine(Application.streamingAssetsPath, reference.streamingPath);
                    if (!CanReadStreamingAssets())
                    {
                        Debug.LogError($"[Loc] Пакет «{reference.id}» лежит в StreamingAssets, а на этой платформе их нельзя читать с диска синхронно. " +
                                       "Перенесите .loc из StreamingAssets или грузите пакеты проектом (LocalizationSettings → Package Loading → Project).");
                        continue;
                    }

                    package = LocPackage.FromFile(reference.id, path, LocSource.Project, lenient);
                }

                if (package == null) continue;

                group.Layers.Add(new Layer
                {
                    Id = package.Id, Source = package.Source, Owner = AutoPackagesOwner, Table = package.Table,
                    Path = package.Path, Package = package,
                });
            }

            if (rebuild) Invalidate();
        }

        private static bool CanReadStreamingAssets() =>
            Application.platform != RuntimePlatform.Android && Application.platform != RuntimePlatform.WebGLPlayer;
    }
}
