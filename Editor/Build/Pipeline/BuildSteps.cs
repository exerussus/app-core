// ============================================================
//  BuildSteps.cs — контракт пре-/пост-процессов сборки (AppCore, редактор).
//
//  Шаги выполняет пайплайн сборки (страница App в Nexus): находит все классы с
//  [BuildStep] во ВСЕХ сборках проекта (TypeCache), показывает их в пайплайне каждого
//  профиля, хранит порядок, вкл/выкл и настройки (JSON) в build-config.json.
//  Поэтому шаг может лежать в любом пакете, который ссылается на Exerussus.AppCore.Editor:
//  нет пакета — нет шага.
//
//  Как добавить шаг: класс, унаследованный от BuildStep<TSettings>, с [BuildStep(id, name, stage)].
//  Правила шага:
//  • Execute бросает исключение при ошибке — пайплайн останавливается, в историю пишется,
//    какой шаг упал.
//  • Pre-шаги — после активации профиля, до BuildPlayer. Post-шаги — только после УСПЕШНОГО билда.
//  • Настройки — [Serializable]-класс с публичными полями (JsonUtility). Они ОБЩИЕ
//    (build-config.json, коммитится) — секретов там быть не должно.
//  • Личное (ключи, пути на машине) — в UserSettings: в UI через host.UserConfigPath,
//    при выполнении через context.UserConfigPath.
//  • Состояние между вызовами не хранить: экземпляр создаётся заново.
// ============================================================

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.AppCore.Editor.Build
{
    public enum BuildStage
    {
        Pre  = 0,
        Post = 1,
    }

    /// <summary>Шаг сборки. Id — ключ в build-config.json: менять его нельзя.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class BuildStepAttribute : Attribute
    {
        public string     Id          { get; }
        public string     Name        { get; }
        public BuildStage Stage       { get; }
        public string     Description { get; set; } = "";

        public BuildStepAttribute(string id, string name, BuildStage stage)
        {
            Id    = id;
            Name  = name;
            Stage = stage;
        }
    }

    public interface IBuildStep
    {
        object        LoadSettings(string json);
        string        SaveSettings(object settings);
        VisualElement BuildSettingsUI(object settings, Action changed, IBuildStepHost host);
        void          Execute(BuildStepContext context, object settings);
    }

    /// <summary>База шага с типизированными настройками.</summary>
    public abstract class BuildStep<TSettings> : IBuildStep where TSettings : class, new()
    {
        public object LoadSettings(string json)
        {
            var s = new TSettings();
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, s); }
                catch (Exception ex) { Debug.LogWarning($"[Build] Настройки шага {GetType().Name} не прочитаны: {ex.Message}"); }
            }
            return s;
        }

        public string SaveSettings(object settings) => JsonUtility.ToJson(settings);

        public VisualElement BuildSettingsUI(object settings, Action changed, IBuildStepHost host)
            => BuildSettingsUI((TSettings)settings, changed, host);

        public void Execute(BuildStepContext context, object settings)
            => Execute(context, (TSettings)settings);

        /// <summary>UI настроек (null — настроек нет). После правки вызвать changed().</summary>
        protected virtual VisualElement BuildSettingsUI(TSettings settings, Action changed, IBuildStepHost host) => null;

        protected abstract void Execute(BuildStepContext context, TSettings settings);
    }

    /// <summary>Тон сообщения хоста.</summary>
    public enum BuildStepStatus
    {
        Info    = 0,
        Ok      = 1,
        Warning = 2,
        Error   = 3,
    }

    /// <summary>
    /// То, что пайплайн даёт UI настроек шага: личные файлы, статус и элементы в стиле окна,
    /// где показан пайплайн. Шаг не знает, кто его показывает.
    /// </summary>
    public interface IBuildStepHost
    {
        /// <summary>Личный файл (UserSettings, git-ignored) — для ключей и путей этой машины.</summary>
        string UserConfigPath(string file);

        /// <summary>Сообщение пользователю (строка статуса окна).</summary>
        void SetStatus(string text, BuildStepStatus status = BuildStepStatus.Info);

        /// <summary>Поле ввода. password — скрытый ввод. Значение уходит в onChanged по завершении ввода.</summary>
        TextField Field(string value, Action<string> onChanged, bool password = false);

        Toggle Toggle(string label, bool value, Action<bool> onChanged);

        Button Button(string text, Action onClick, bool primary = false);

        /// <summary>Строка «подпись — поле».</summary>
        VisualElement Labeled(string label, VisualElement field);

        /// <summary>Пояснение мелким текстом.</summary>
        Label Hint(string text);

        /// <summary>Заголовок группы настроек.</summary>
        Label Section(string text);
    }

    /// <summary>Всё, что шаг знает о текущей сборке.</summary>
    public sealed class BuildStepContext
    {
        public BuildStage  Stage;
        public string      ProfileName     = "";
        public BuildTarget Target;
        public string      ProductName     = "";
        public string      Version         = "";
        public string      CommitHash      = "";   // пусто — git не найден
        public bool        IsDirty;
        public bool        Development;

        /// <summary>locationPathName билда: файл (.exe/.apk/.app) или папка (WebGL/iOS).</summary>
        public string OutputPath = "";

        /// <summary>Папка сборки (корень, который упаковывается/публикуется).</summary>
        public string OutputDirectory = "";

        /// <summary>Билд — один самодостаточный файл (.apk/.aab). Для Windows .exe — false.</summary>
        public bool OutputIsSingleFile;

        /// <summary>Папка личных настроек пайплайна (UserSettings) — для ключей и путей машины.</summary>
        public string UserConfigDir = "";

        public string UserConfigPath(string file) => System.IO.Path.Combine(UserConfigDir ?? "", file);

        /// <summary>Отчёт Unity; есть только у Post-шагов.</summary>
        public BuildReport Report;

        /// <summary>Файлы, созданные шагами (zip и т.п.) — попадают в историю.</summary>
        public readonly List<string> Artifacts = new List<string>();

        /// <summary>Id шагов, уже выполненных в этой сборке, по порядку.</summary>
        public readonly List<string> CompletedSteps = new List<string>();

        public readonly List<string> LogLines = new List<string>();

        public void Log(string message)
        {
            LogLines.Add(message);
            Debug.Log($"[Build] {message}");
        }

        /// <summary>Последний артефакт с расширением (".zip") или null.</summary>
        public string LastArtifact(string extension)
            => Artifacts.FindLast(a => a.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Токены в шаблонах имён и путей: {product} {version} {hash} {profile} {platform} {date}.
    /// Пустое значение (нет git → пустой {hash}) не оставляет висящих разделителей:
    /// «{product}_{version}_{hash}» без git даёт «Game_1.0», а не «Game_1.0_».
    /// Свои токены шаг подставляет сам до вызова (например {zip}).
    /// </summary>
    public static class BuildTokens
    {
        public const string Help = "Токены: {product} {version} {hash} {profile} {platform} {date}";

        public static string Expand(string template, string product, string version, string hash,
                                    string profile, string platform, DateTime now)
        {
            const string Empty = "\u0000";   // метка пустого токена — вычищается вместе с разделителем
            var s = (template ?? "")
                .Replace("{product}",  Or(product))
                .Replace("{version}",  Or(version))
                .Replace("{hash}",     Or(hash))
                .Replace("{profile}",  Or(profile))
                .Replace("{platform}", Or(platform))
                .Replace("{date}",     now.ToString("yyyyMMdd-HHmm"));

            // пустой токен: «a_<пусто>_b» → «a_b», «a_<пусто>» → «a», «<пусто>_b» → «b»
            for (var i = s.IndexOf(Empty, StringComparison.Ordinal); i >= 0; i = s.IndexOf(Empty, StringComparison.Ordinal))
            {
                var left  = i > 0 && IsSeparator(s[i - 1]);
                var right = i + 1 < s.Length && IsSeparator(s[i + 1]);
                if (left && right)                  s = s.Remove(i, 2);
                else if (left && i + 1 == s.Length) s = s.Remove(i - 1, 2);
                else if (right && i == 0)           s = s.Remove(i, 2);
                else                                s = s.Remove(i, 1);
            }
            return s;

            string Or(string v) => string.IsNullOrEmpty(v) ? Empty : v;
        }

        public static string Expand(string template, BuildStepContext ctx, DateTime now)
            => Expand(template, ctx.ProductName, ctx.Version, ctx.CommitHash, ctx.ProfileName, ctx.Target.ToString(), now);

        /// <summary>Предпросмотр в UI: текущие продукт, версия и платформа, хэш — для примера.</summary>
        public static string Preview(string template)
            => Expand(template, PlayerSettings.productName, PlayerSettings.bundleVersion, "1a2b3c4", "<профиль>",
                      EditorUserBuildSettings.activeBuildTarget.ToString(), DateTime.Now);

        private static bool IsSeparator(char c) => c == '_' || c == '-' || c == '.' || c == '/' || c == ' ';
    }
}
