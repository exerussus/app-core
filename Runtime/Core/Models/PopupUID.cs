
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if UNITY_EDITOR
using UnityEngine;
#endif

namespace App.Core
{
    // Лёгкий идентификатор страницы: long-хэш из строки.
    // Сравнение — по id (один long-компэр). default(PopupUID) == "пустой" (Id 0).
    public readonly struct PopupUID : IEquatable<PopupUID>
    {
        public readonly long Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PopupUID(string name)
        {
            Id = Hash(name);
#if UNITY_EDITOR
            RegisterName(Id, name);
#endif
        }

        // private — обернуть уже готовый long (например, прочитанный обратно из Payload).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private PopupUID(long id) => Id = id;

        public static readonly PopupUID None = default;   // Id == 0

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PopupUID FromRaw(long id) => new(id);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEmpty() => Id == 0;

        // ---- equality ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(PopupUID other) => Id == other.Id;

        public override bool Equals(object obj) => obj is PopupUID other && Id == other.Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => Id.GetHashCode();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(PopupUID a, PopupUID b) => a.Id == b.Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(PopupUID a, PopupUID b) => a.Id != b.Id;

        // ---- hashing (FNV-1a 64) ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long Hash(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;   // пустая/null → None
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < key.Length; i++)
                {
                    h ^= key[i];
                    h *= 1099511628211UL;
                }
                return (long)h;
            }
        }

        // ---- ToString ----

        public override string ToString()
        {
#if UNITY_EDITOR
            return Names.TryGetValue(Id, out var name)
                ? $"PopupUID(\"{name}\", 0x{Id:X16})"
                : $"PopupUID(0x{Id:X16})";
#else
            return $"PopupUID(0x{Id:X16})";
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
                    Debug.LogWarning($"[PopupUID] id collision: \"{existing}\" и \"{name}\" → 0x{id:X16}");
                return;
            }
            Names[id] = name;
        }
#endif
    }
}