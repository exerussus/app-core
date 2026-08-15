using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Exerussus.AppCore.Views
{
    // Лёгкий идентификатор фрагмента: long-хэш из строки. Форма и правила — как у PageId/PopupId:
    // сравнение по id (один long-компэр), default(FragmentId) == "пустой" (Id 0),
    // валидный Id всегда > 0.
    public readonly struct FragmentId : IEquatable<FragmentId>
    {
        public readonly long Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FragmentId(string name)
        {
            Id = Hash(name);
#if UNITY_EDITOR
            RegisterName(Id, name);
#endif
        }

        public static readonly FragmentId None = default;   // Id == 0

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEmpty() => Id == 0;

        // ---- equality ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(FragmentId other) => Id == other.Id;

        public override bool Equals(object obj) => obj is FragmentId other && Id == other.Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => Id.GetHashCode();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(FragmentId a, FragmentId b) => a.Id == b.Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(FragmentId a, FragmentId b) => a.Id != b.Id;

        // ---- hashing (FNV-1a 64) ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long Hash(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;   // пустая/null → None (невалид)
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < key.Length; i++)
                {
                    h ^= key[i];
                    h *= 1099511628211UL;
                }
                h &= 0x7FFFFFFFFFFFFFFFUL;   // сбрасываем знаковый бит → значение всегда > 0
                if (h == 0) h = 1;           // страховка от коллизии с None (0)
                return (long)h;
            }
        }

        // ---- ToString ----

        public override string ToString()
        {
#if UNITY_EDITOR
            return Names.TryGetValue(Id, out var name)
                ? $"FragmentId(\"{name}\", 0x{Id:X16})"
                : $"FragmentId(0x{Id:X16})";
#else
            return $"FragmentId(0x{Id:X16})";
#endif
        }

        // =====================================================================
        //  EDITOR-ONLY: Id → строка, для читаемого ToString. В билде вырезается.
        // =====================================================================
#if UNITY_EDITOR
        private static readonly Dictionary<long, string> Names = new();

        private static void RegisterName(long id, string name)
        {
            if (Names.TryGetValue(id, out var existing))
            {
                if (!string.Equals(existing, name, StringComparison.Ordinal))
                    Debug.LogWarning($"[FragmentId] id collision: \"{existing}\" и \"{name}\" → 0x{id:X16}");
                return;
            }
            Names[id] = name;
        }
#endif
    }
}
