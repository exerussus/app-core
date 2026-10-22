using System.Runtime.CompilerServices;

namespace Exerussus.AppCore.Audio
{
    /// <summary>
    /// Ручка звукового события: номер записи каталога, взятый по ключу один раз (<see cref="AppSound.Get"/>).
    /// <c>default</c> — пустая ручка, <see cref="Play"/> на ней молчит.
    /// </summary>
    public readonly struct SoundHandle
    {
        // номер + 1: ноль — пустая ручка, чтобы default(SoundHandle) ничего не играл
        private readonly int _slot;

        internal SoundHandle(int id) => _slot = id + 1;

        /// <summary>Номер события (как константа <c>AppSounds.…_S</c>) или -1.</summary>
        public int Id => _slot - 1;

        public bool IsValid => _slot > 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Play() => AppSound.Play(_slot - 1);

        public static implicit operator int(SoundHandle handle) => handle._slot - 1;
    }
}
