using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Exerussus.AppCore.Editor.Localization
{
    /// <summary>Значение проекта, которое можно вставить в перевод (живое значение).</summary>
    public readonly struct LocValueInfo
    {
        /// <summary>Id значения в хранилище проекта.</summary>
        public readonly string Id;

        /// <summary>Вид: подпись для списка («строка», «ресурс», «целое»).</summary>
        public readonly string Kind;

        /// <summary>Пример значения — для предпросмотра строки.</summary>
        public readonly string Sample;

        /// <summary>Пояснение (откуда значение). Может быть пустым.</summary>
        public readonly string Description;

        /// <summary>Значение числовое — к нему применимы форматы.</summary>
        public readonly bool Numeric;

        public LocValueInfo(string id, string kind, string sample, bool numeric, string description = null)
        {
            Id = id;
            Kind = kind;
            Sample = sample;
            Numeric = numeric;
            Description = description;
        }
    }

    /// <summary>
    /// Каталог живых значений проекта для конструктора строк (Nexus → App → Локализация).
    /// Синтаксис токенов — дело проекта (AppCore его не разбирает: это делает ILocTextProcessor),
    /// поэтому каталог сам строит токен и сам подставляет примеры в предпросмотр.
    /// Реализация — класс с конструктором без параметров в любой редакторской сборке:
    /// находится сам.
    /// </summary>
    public interface ILocValueCatalog
    {
        /// <summary>Значения, которые можно вставить.</summary>
        IEnumerable<LocValueInfo> GetValues();

        /// <summary>Форматы для значения: пара «как писать в токене — подпись». Первый — «как есть».</summary>
        IReadOnlyList<(string format, string label)> FormatsFor(LocValueInfo value);

        /// <summary>Токен для вставки в перевод.</summary>
        string MakeToken(LocValueInfo value, bool live, string format);

        /// <summary>Подставляет примеры вместо токенов — для предпросмотра.</summary>
        string Preview(string text);
    }

    /// <summary>Сбор каталогов проекта.</summary>
    public static class LocValueCatalog
    {
        private static List<ILocValueCatalog> _catalogs;

        /// <summary>Все каталоги проекта (кэшируется до перекомпиляции).</summary>
        public static IReadOnlyList<ILocValueCatalog> Catalogs
        {
            get
            {
                if (_catalogs != null) return _catalogs;

                _catalogs = new List<ILocValueCatalog>();
                foreach (var type in TypeCache.GetTypesDerivedFrom<ILocValueCatalog>())
                {
                    if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null) continue;

                    try { _catalogs.Add((ILocValueCatalog)Activator.CreateInstance(type)); }
                    catch (Exception e) { Debug.LogException(e); }
                }

                return _catalogs;
            }
        }

        /// <summary>Предпросмотр строки всеми каталогами.</summary>
        public static string Preview(string text)
        {
            foreach (var catalog in Catalogs)
            {
                try { text = catalog.Preview(text); }
                catch (Exception e) { Debug.LogException(e); }
            }

            return text;
        }
    }
}
