using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;
using Exerussus.AppCore.Localization;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>
    /// Файл <c>.loc</c> импортируется как <see cref="TextAsset"/> — его можно назначить в
    /// LocalizationSettings и прочитать в сборке. Ошибки формата видны в консоли сразу при импорте,
    /// с номерами строк.
    /// </summary>
    [ScriptedImporter(1, "loc")]
    public sealed class LocImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var text = File.ReadAllText(ctx.assetPath);
            var asset = new TextAsset(text);

            ctx.AddObjectToAsset("text", asset);
            ctx.SetMainObject(asset);

            var parsed = LocFormat.Parse(text);
            foreach (var error in parsed.Errors)
                ctx.LogImportError($"[Loc] {ctx.assetPath}, {error}");
        }
    }
}
