using System;

namespace Exerussus.AppCore.Deck
{
    /// <summary>Откуда пришла команда.</summary>
    public enum CommandSource : byte
    {
        /// <summary>Введена в консоли.</summary>
        Console = 0,

        /// <summary>Нажата кнопка во вкладке Actions.</summary>
        Action = 1,

        /// <summary>Вызвана кодом (<see cref="AppDeck.Execute"/>).</summary>
        Code = 2,
    }

    /// <summary>Обработчик команды. Синхронный: всё, что дольше кадра, команда запускает сама и возвращается.</summary>
    public delegate void CommandHandler(CommandContext ctx);

    /// <summary>
    /// Вызов команды: разобранные аргументы, текущая цель и вывод в консоль.
    /// </summary>
    /// <remarks>
    /// Контекст ПЕРЕИСПОЛЬЗУЕТСЯ: он живёт только на время обработчика. Сохранять ссылку на него
    /// или читать аргументы после возврата нельзя — значения уже будут чужие. Нужно дольше —
    /// скопируйте нужные значения.
    /// </remarks>
    public sealed class CommandContext
    {
        private ArgValue[] _values = new ArgValue[8];
        private string _line;
        private int _start;
        private int _end;
        private string _statement;

        internal CommandContext() { }

        public CommandSpec Spec { get; private set; }

        public string Name => Spec?.Name;

        public CommandSource Source { get; private set; }

        /// <summary>Цель, выбранная в AppDeck на момент вызова.</summary>
        public DeckTarget Target { get; private set; }

        /// <summary>Обработчик сообщил об ошибке (<see cref="Error"/>).</summary>
        public bool Failed { get; private set; }

        /// <summary>Текст этой команды целиком, как её написали. Строится по первому запросу.</summary>
        public string Text => _statement ??= _line.Substring(_start, _end - _start).Trim();

        public int ArgCount => Spec.Args.Length;

        internal ArgValue[] Values => _values;

        internal void Begin(CommandSpec spec, CommandSource source, DeckTarget target, string line, int start, int end)
        {
            Spec = spec;
            Source = source;
            Target = target;
            Failed = false;
            _line = line;
            _start = start;
            _end = end;
            _statement = null;

            int n = spec.Args.Length;
            if (_values.Length < n) _values = new ArgValue[Math.Max(n, _values.Length * 2)];
            for (var i = 0; i < n; i++) _values[i] = default;
        }

        internal void End()
        {
            // не держим чужие объекты дольше вызова: цель и строки могут уйти из мира
            int n = Spec?.Args.Length ?? 0;
            for (var i = 0; i < n; i++) _values[i] = default;

            Spec = null;
            Target = default;
            _line = null;
            _statement = null;
        }

        // ---------------------------------------------------------------- аргументы

        /// <summary>Индекс аргумента по имени, -1 — нет такого.</summary>
        public int IndexOf(string argName)
        {
            ArgSpec[] args = Spec.Args;
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i].Name, argName, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return -1;
        }

        /// <summary>Аргумент указан (или у него есть значение по умолчанию).</summary>
        public bool Has(int index) => index >= 0 && index < Spec.Args.Length && _values[index].Present;

        public bool Has(string argName) => Has(IndexOf(argName));

        public long GetLong(int index, long fallback = 0) => Has(index) ? _values[index].Int : fallback;

        public int GetInt(int index, int fallback = 0) => Has(index) ? (int)_values[index].Int : fallback;

        public double GetDouble(int index, double fallback = 0) => Has(index) ? _values[index].Float : fallback;

        public float GetFloat(int index, float fallback = 0) => Has(index) ? (float)_values[index].Float : fallback;

        public bool GetBool(int index, bool fallback = false) => Has(index) ? _values[index].Bool : fallback;

        public string GetString(int index, string fallback = null) => Has(index) ? _values[index].Text : fallback;

        /// <summary>Цель из аргумента <see cref="ArgKind.Target"/>: выбранная или пусто (тогда смотрите <see cref="GetString(int,string)"/> — там слово, которое написали вместо неё).</summary>
        public DeckTarget GetTarget(int index) => Has(index) ? _values[index].Target : default;

        public long GetLong(string argName, long fallback = 0) => GetLong(IndexOf(argName), fallback);

        public int GetInt(string argName, int fallback = 0) => GetInt(IndexOf(argName), fallback);

        public double GetDouble(string argName, double fallback = 0) => GetDouble(IndexOf(argName), fallback);

        public float GetFloat(string argName, float fallback = 0) => GetFloat(IndexOf(argName), fallback);

        public bool GetBool(string argName, bool fallback = false) => GetBool(IndexOf(argName), fallback);

        public string GetString(string argName, string fallback = null) => GetString(IndexOf(argName), fallback);

        public DeckTarget GetTarget(string argName) => GetTarget(IndexOf(argName));

        // ---------------------------------------------------------------- вывод

        /// <summary>Строка в консоль.</summary>
        public void Print(string message) => AppDeck.Print(message);

        public void Warn(string message) => AppDeck.Print(message, DeckLogType.Warning);

        /// <summary>Ошибка в консоль; команда считается неудавшейся (кнопка мигнёт красным).</summary>
        public void Error(string message)
        {
            Failed = true;
            AppDeck.Print(message, DeckLogType.Error);
        }
    }
}
