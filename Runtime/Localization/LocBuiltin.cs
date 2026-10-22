namespace Exerussus.AppCore.Localization
{
    /// <summary>
    /// Встроенные строки AppCore — кнопки и подписи код-фолбэков скринов. Живут в слое
    /// <c>appcore</c>, проект переопределяет их в своей таблице тем же ключом.
    /// </summary>
    /// <remarks>
    /// Языки встроенный слой не объявляет: какие языки есть, решает таблица проекта. Значение
    /// на языке, которого в проекте нет, просто не используется; на языке без своего значения
    /// показывается английское.
    /// </remarks>
    public static class LocBuiltin
    {
        public const string Restart = "APPCORE_RESTART";
        public const string Quit = "APPCORE_QUIT";
        public const string Close = "APPCORE_CLOSE";
        public const string CriticalError = "APPCORE_CRITICAL_ERROR";

        // ключ, ru, en
        private static readonly string[,] Strings =
        {
            { Restart, "Перезапустить", "Restart" },
            { Quit, "Выйти", "Quit" },
            { Close, "Закрыть", "Close" },
            { CriticalError, "Критическая ошибка", "Critical error" },
        };

        /// <summary>
        /// Текст для элемента: при включённой локализации — ключ с <c>^</c> (его подхватит
        /// <see cref="LocBinder"/>), при выключенной — русская строка как была до локализации.
        /// </summary>
        public static string Text(string key) => Loc.Enabled ? Loc.Prefix + key : Fallback(key);

        /// <summary>Сразу переведённая строка. Выключено — русская строка.</summary>
        public static string Resolve(string key) => Loc.Enabled ? Loc.Get(Loc.Prefix + key) : Fallback(key);

        private static string Fallback(string key)
        {
            for (var i = 0; i < Strings.GetLength(0); i++)
                if (Strings[i, 0] == key) return Strings[i, 1];
            return key;
        }

        internal static LocTable CreateTable()
        {
            var table = new LocTable();
            for (var i = 0; i < Strings.GetLength(0); i++)
            {
                table.Set(Strings[i, 0], "ru", Strings[i, 1]);
                table.Set(Strings[i, 0], "en", Strings[i, 2]);
            }

            return table;
        }
    }
}
