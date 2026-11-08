using UnityEngine;

namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Строковое поле — ключ локализации: инспектор показывает текст вместо ключа, ищет по тексту
    /// на любом языке и по ключу, создаёт новый ключ сразу в выбранном пакете.
    /// </summary>
    /// <example><code>
    /// [LocKey] public string title;                      // хранится «^MENU_TITLE» — понимают Loc.Get и LocBinder
    /// [LocKey("greet.", caret: false)] public string hi;  // хранится «greet.hello», поиск сужен до greet.
    /// </code></example>
    public sealed class LocKeyAttribute : PropertyAttribute
    {
        /// <summary>Префикс ключей поля (домен): сужает поиск и подставляется в новый ключ.</summary>
        public readonly string Prefix;

        /// <summary>Хранить ключ с «^» (по умолчанию) или как есть.</summary>
        public readonly bool Caret;

        public LocKeyAttribute(string prefix = null, bool caret = true)
        {
            Prefix = prefix;
            Caret = caret;
        }
    }
}
