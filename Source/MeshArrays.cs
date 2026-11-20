using System;
using System.Collections.Generic;
using UnhollowerBaseLib;

namespace Redline;

internal static class MeshArrays
{
    // Match Il2CppStructArray's layout in the loader. These mesh channels are blittable structs.
    private static readonly int HeaderBytes = 4 * IntPtr.Size;

    public static unsafe void Read<T>(Il2CppStructArray<T> source, ref T[] buffer) where T : unmanaged
    {
        int count = source.Length;
        if (buffer == null || buffer.Length != count)
            buffer = new T[count];
        if (count == 0)
            return;
        long bytes = (long)count * sizeof(T);
        fixed (T* destination = buffer)
            Buffer.MemoryCopy((byte*)source.Pointer + HeaderBytes, destination, bytes, bytes);
        GC.KeepAlive(source);
    }

    public static void Write<T>(List<T> values, ref T[] staging, ref Il2CppStructArray<T> target) where T : unmanaged
    {
        int count = values.Count;
        if (staging == null || staging.Length < count)
        {
            int capacity = Math.Max(count, staging == null ? 256 : staging.Length * 2);
            staging = new T[capacity];
            target = new Il2CppStructArray<T>(capacity);
        }
        values.CopyTo(staging);
        Write(staging, target, count, staging.Length);
    }

    public static void Write<T>(T[] values, Il2CppStructArray<T> target) where T : unmanaged
    {
        int capacity = target.Length;
        if (values.Length > capacity)
            throw new ArgumentException("Mesh data exceeds destination size", nameof(values));
        Write(values, target, values.Length, capacity);
    }

    private static unsafe void Write<T>(T[] values, Il2CppStructArray<T> target, int count, int capacity) where T : unmanaged
    {
        if (count == 0)
            return;
        long bytes = (long)count * sizeof(T);
        fixed (T* source = values)
            Buffer.MemoryCopy(source, (byte*)target.Pointer + HeaderBytes, (long)capacity * sizeof(T), bytes);
        GC.KeepAlive(target);
    }
}
