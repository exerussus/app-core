using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Exerussus.AppCore.Analytics.Reports
{
    public enum ReportIssueLevel : byte { Info, Warning, Error }

    public readonly struct ReportIssue
    {
        public readonly ReportIssueLevel Level;
        /// <summary>Ключ события, id представления («view:…») или профиля («profile:…»).</summary>
        public readonly string Key;
        public readonly string Text;

        public ReportIssue(ReportIssueLevel level, string key, string text)
        {
            Level = level;
            Key = key;
            Text = text;
        }
    }

    /// <summary>
    /// Проверка файла представлений против каталога событий: каждое событие классифицировано (входит в
    /// представления или «только сырые»), привязки ссылаются на существующие представления, роли подходят типу,
    /// поля существуют и нужного типа, выражения и агрегаты разбираются.
    /// </summary>
    public static class ReportValidation
    {
        private static readonly Regex IdPattern = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$");
        private static readonly HashSet<string> Formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "html", "csv", "json", "jsonl" };

        public static List<ReportIssue> Validate(ReportConfig config, IList<ReportEventInfo> events)
        {
            var issues = new List<ReportIssue>();
            config ??= new ReportConfig();
            var byKey = new Dictionary<string, ReportEventInfo>(StringComparer.Ordinal);
            foreach (var e in events)
                if (!string.IsNullOrEmpty(e.Key)) byKey[e.Key] = e;
            foreach (var s in ReportCatalog.SystemEvents) byKey[s.Key] = s;

            var views = config.EffectiveViews();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in config.Views)
            {
                var key = "view:" + v.Id;
                if (string.IsNullOrEmpty(v.Id) || !IdPattern.IsMatch(v.Id))
                    issues.Add(new ReportIssue(ReportIssueLevel.Error, key, "id представления: латиница, цифры, «_», до 64 символов"));
                else if (!ids.Add(v.Id))
                    issues.Add(new ReportIssue(ReportIssueLevel.Error, key, "id представления повторяется"));
                ValidateView(v, issues);
            }

            ReportView Find(string id)
            {
                foreach (var v in views)
                    if (v.Id == id) return v;
                return config.FindView(id);
            }

            // классификация событий каталога
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in events)
            {
                if (string.IsNullOrEmpty(e.Key)) continue;
                var cls = config.FindEvent(e.Key);
                var bindings = config.BindingsOf(e.Key, views);
                if (bindings.Count == 0 && (cls == null || !cls.Raw))
                    issues.Add(new ReportIssue(ReportIssueLevel.Error, e.Key, "событие не классифицировано: привяжите к представлению или отметьте «только сырые данные»"));
                if (cls != null && cls.Raw && cls.Bindings.Count > 0)
                    issues.Add(new ReportIssue(ReportIssueLevel.Warning, e.Key, "отмечено «только сырые», но есть привязки — привязки действуют"));
            }

            // привязки (явные и по маскам)
            foreach (var cls in config.Events)
            {
                if (string.IsNullOrEmpty(cls.Key)) continue;
                byKey.TryGetValue(cls.Key, out var info);
                if (info == null)
                    issues.Add(new ReportIssue(ReportIssueLevel.Warning, cls.Key, "события нет в каталоге (переименовано или удалено?) — классификация не используется"));
                foreach (var b in cls.Bindings)
                {
                    if (b.BadRole != null)
                    {
                        issues.Add(new ReportIssue(ReportIssueLevel.Error, cls.Key, $"роль «{b.BadRole}» неизвестна: point, begin, end, step, done, value, a, b, x, y"));
                        continue;
                    }

                    if (b.BadTone != null) issues.Add(new ReportIssue(ReportIssueLevel.Error, cls.Key, $"окраска «{b.BadTone}»: neutral, positive, negative, gray"));
                    ValidateBinding(cls.Key, info, b, Find(b.View), issues, used);
                }
            }

            foreach (var v in views)
            {
                if (v.Keys.Count == 0 || v.Bind == null) continue;
                var any = false;
                var all = new List<ReportEventInfo>(events);
                all.AddRange(ReportCatalog.SystemEvents);
                foreach (var e in all)
                {
                    if (!ReportConfig.MatchesAny(v.Keys, e.Key)) continue;
                    any = true;
                    var b = v.Bind.Clone();
                    b.View = v.Id;
                    ValidateBinding(e.Key, e, b, v, issues, used);
                }

                if (!any) issues.Add(new ReportIssue(ReportIssueLevel.Info, "view:" + v.Id, "маски ключей не совпали ни с одним событием каталога"));
            }

            // представления без событий
            foreach (var v in config.Views)
            {
                if (v.Hidden || string.IsNullOrEmpty(v.Id)) continue;
                var info = ViewTypes.Of(v.Type);
                if (info.Required.Length == 0) continue;
                if (!used.ContainsKey(v.Id) && !(v.Type == ViewType.Outcome && !string.IsNullOrEmpty(v.Value)))
                    issues.Add(new ReportIssue(ReportIssueLevel.Warning, "view:" + v.Id, "в представление не входит ни одно событие"));
            }

            foreach (var v in config.Views)
            {
                if (string.IsNullOrEmpty(v.Id) || v.Hidden) continue;
                foreach (var role in ViewTypes.Of(v.Type).Required)
                {
                    if (!used.ContainsKey(v.Id)) break;
                    if (!used.ContainsKey(v.Id + "\u0001" + ViewTypes.Name(role)))
                        issues.Add(new ReportIssue(ReportIssueLevel.Warning, "view:" + v.Id, $"нет события с ролью «{ViewTypes.Title(role)}» — представление будет пустым"));
                }
            }

            // профили экспорта
            var profileIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in config.Profiles)
            {
                var key = "profile:" + p.Id;
                if (string.IsNullOrEmpty(p.Id) || !IdPattern.IsMatch(p.Id)) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, "id профиля: латиница, цифры, «_»"));
                else if (!profileIds.Add(p.Id)) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, "id профиля повторяется"));
                foreach (var id in p.Views)
                    if (Find(id) == null) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"нет представления «{id}»"));
                foreach (var f in p.Formats)
                    if (!Formats.Contains(f)) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"формат «{f}»: html, csv, json, jsonl"));
                if (!string.IsNullOrEmpty(p.TimeZone))
                {
                    ReportTime.Parse(p.TimeZone, out var w);
                    if (w != null) issues.Add(new ReportIssue(ReportIssueLevel.Warning, key, w));
                }
            }

            if (!string.IsNullOrEmpty(config.TimeZone))
            {
                ReportTime.Parse(config.TimeZone, out var w);
                if (w != null) issues.Add(new ReportIssue(ReportIssueLevel.Warning, "timezone", w));
            }

            return issues;
        }

        private static void ValidateView(ReportView v, List<ReportIssue> issues)
        {
            var key = "view:" + v.Id;
            if (v.BadType != null)
            {
                var names = new List<string>();
                foreach (var i in ViewTypes.List) names.Add(i.Name);
                issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"тип «{v.BadType}» неизвестен: {string.Join(", ", names)}"));
            }

            if (v.BadUnit != null) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"единица «{v.BadUnit}»: events, runs, sessions, profiles"));
            foreach (var o in v.Outcomes)
                if (o.BadTone != null) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"класс «{o.Value}»: окраска «{o.BadTone}» — neutral, positive, negative, gray"));
            if (v.Bind != null && v.Bind.BadRole != null) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"роль по маске «{v.Bind.BadRole}» неизвестна"));
            foreach (var c in v.Computed)
            {
                if (string.IsNullOrWhiteSpace(c.Name) || !IdPattern.IsMatch(c.Name)) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"вычисляемое поле «{c.Name}»: имя — латиница, цифры, «_»"));
                if (!ReportExpr.TryCompile(c.Expr, out _, out var error)) issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"вычисляемое поле «{c.Name}»: {error}"));
            }

            if (!string.IsNullOrWhiteSpace(v.Where) && !ReportExpr.TryCompile(v.Where, out _, out var whereError))
                issues.Add(new ReportIssue(ReportIssueLevel.Error, key, "фильтр: " + whereError));

            foreach (var a in v.Aggs)
            {
                try
                {
                    ReportViews.ValidateAgg(a);
                }
                catch (FormatException e)
                {
                    issues.Add(new ReportIssue(ReportIssueLevel.Error, key, e.Message));
                }
            }

            foreach (var d in v.GroupBy) CheckDim(key, d, issues);
            CheckDim(key, v.Compare, issues);
            CheckDim(key, v.X, issues);
            CheckDim(key, v.Y, issues);

            if ((v.Type == ViewType.Share || v.Type == ViewType.Conversion) && v.Unit == CountUnit.Events)
                issues.Add(new ReportIssue(ReportIssueLevel.Error, key, "доля и конверсия считаются по прогонам, сессиям или игрокам"));
            if (!string.IsNullOrEmpty(v.Bucket) && v.Bucket != "hour" && v.Bucket != "day" && v.Bucket != "week")
                issues.Add(new ReportIssue(ReportIssueLevel.Error, key, "шаг ряда: hour, day или week"));
            if (!string.IsNullOrEmpty(v.Clock) && v.Clock != "game" && v.Clock != "real")
                issues.Add(new ReportIssue(ReportIssueLevel.Error, key, "часы: game или real"));
            if (v.Type == ViewType.Outcome && !string.IsNullOrEmpty(v.Value) && v.Value.StartsWith("@", StringComparison.Ordinal) && v.Value != "@result" && !v.Value.StartsWith("@prop.", StringComparison.Ordinal))
                CheckDim(key, v.Value, issues);
        }

        private static void CheckDim(string key, string dim, List<ReportIssue> issues)
        {
            if (string.IsNullOrEmpty(dim) || dim[0] != '@') return;
            if (dim.StartsWith("@prop.", StringComparison.Ordinal) && dim.Length > 6) return;
            if (Array.IndexOf(ViewTypes.Dimensions, dim) < 0)
                issues.Add(new ReportIssue(ReportIssueLevel.Error, key, $"измерение «{dim}»: {string.Join(", ", ViewTypes.Dimensions)}, @prop.<имя>"));
        }

        private static void ValidateBinding(string eventKey, ReportEventInfo info, ViewBinding b, ReportView view, List<ReportIssue> issues, Dictionary<string, int> used)
        {
            if (view == null)
            {
                issues.Add(new ReportIssue(ReportIssueLevel.Error, eventKey, $"привязка к несуществующему представлению «{b.View}»"));
                return;
            }

            used[view.Id] = 1;
            used[view.Id + "\u0001" + ViewTypes.Name(b.Role)] = 1;
            var where = $"«{view.DisplayTitle}»";
            if (!ViewTypes.Allows(view.Type, b.Role))
            {
                var allowed = new List<string>();
                foreach (var r in ViewTypes.Of(view.Type).Roles) allowed.Add(ViewTypes.Title(r));
                issues.Add(new ReportIssue(ReportIssueLevel.Error, eventKey, $"{where}: роль «{ViewTypes.Title(b.Role)}» не подходит типу «{ViewTypes.Of(view.Type).Title}» ({(allowed.Count > 0 ? string.Join(", ", allowed) : "привязки не нужны")})"));
            }

            if (ViewTypes.NeedsSubject(view.Type, b.Role) && b.Subject == null)
                issues.Add(new ReportIssue(ReportIssueLevel.Error, eventKey, $"{where}: для роли «{ViewTypes.Title(b.Role)}» нужен субъект (поле с именем шага)"));
            if (ViewTypes.NeedsMeasure(view.Type, b.Role) && b.Measure == null)
                issues.Add(new ReportIssue(ReportIssueLevel.Error, eventKey, $"{where}: нужна мера (числовое поле)"));
            if (ViewTypes.NeedsValue(view.Type, b.Role) && b.Value == null)
                issues.Add(new ReportIssue(ReportIssueLevel.Error, eventKey, $"{where}: нужно поле значения"));

            if (info == null) return;
            CheckField(eventKey, info, b.Subject, "субъект", false, where, issues);
            CheckField(eventKey, info, b.Measure, "мера", true, where, issues);
            CheckField(eventKey, info, b.Value, "значение", false, where, issues);
        }

        private static void CheckField(string eventKey, ReportEventInfo info, string name, string what, bool numeric, string where, List<ReportIssue> issues)
        {
            if (string.IsNullOrEmpty(name)) return;
            var f = info.Field(name);
            if (f == null)
            {
                issues.Add(new ReportIssue(ReportIssueLevel.Error, eventKey, $"{where}: {what} «{name}» — у события нет такого поля"));
                return;
            }

            if (numeric && !f.IsNumber)
                issues.Add(new ReportIssue(ReportIssueLevel.Error, eventKey, $"{where}: {what} «{name}» должна быть числовым полем"));
        }
    }
}
