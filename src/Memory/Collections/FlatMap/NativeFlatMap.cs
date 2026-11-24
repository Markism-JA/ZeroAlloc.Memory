using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Collections.FlatMap;

/// <summary>
/// Pure unmanaged open-addressing hash table with circular backward-shift deletion.
/// Enforces a strict 50% load factor to guarantee less than or equal 1.5 average probe
/// lengths without tombstones.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct NativeFlatMap<TKey, TValue>
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    /// <summary>Maximum allowed physical slot capacity (536M slots).</summary>
    public const ulong MaxAllowedCapacity = 0x20000000UL; // 536M entries
    private const int CacheLineAlignment = 64;

    private readonly int _capacity;
    private readonly int _mask;
    private readonly int _maxCount; // Strict 50% load threshold: _capacity >> 1
    private int _count;
    private readonly FlatMapEntry<TKey, TValue>* _entries;

    #region State & Metrics

    /// <summary>Gets the number of active entries stored in the map.</summary>
    public readonly int Count => _count;

    /// <summary>Gets the total slot capacity of the hash table.</summary>
    public readonly int Capacity => _capacity;

    /// <summary>Gets the maximum key count at the 50% load factor threshold.</summary>
    public readonly int MaxCount => _maxCount;

    /// <summary>Returns <c>true</c> if the map contains zero entries.</summary>
    public readonly bool IsEmpty => _count == 0;

    /// <summary>Returns <c>true</c> if the map has reached <see cref="MaxCount"/>.</summary>
    public readonly bool IsFull => _count >= _maxCount;

    /// <summary>Gets the raw unmanaged pointer to the backing entry array.</summary>
    public readonly FlatMapEntry<TKey, TValue>* EntriesPtr => _entries;

    #endregion

    #region Allocation & Lifecycle

    /// <summary>Allocates a new <see cref="NativeFlatMap{TKey, TValue}"/> sized for at least <paramref name="targetKeyCapacity"/> keys.</summary>
    public static NativeFlatMap<TKey, TValue>* Allocate(int targetKeyCapacity = 1024)
    {
        if (targetKeyCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetKeyCapacity),
                "Capacity must be strictly positive."
            );
        }

        // 50% max load factor: target capacity needs 2x physical slot headroom
        ulong pow2 = BitOperation.RoundUpToPowerOf2((ulong)targetKeyCapacity);
        if (pow2 < 32)
            pow2 = 32;

        ulong totalSlots = pow2 * 2; // Capacity is doubled for 50% load factor
        if (totalSlots > MaxAllowedCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetKeyCapacity),
                $"Requested capacity exceeds maximum structural limit of {MaxAllowedCapacity} slots."
            );
        }

        long totalBytesNeeded = (long)totalSlots * sizeof(FlatMapEntry<TKey, TValue>);
        if (totalBytesNeeded > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetKeyCapacity),
                $"Allocation requires {totalBytesNeeded:N0} bytes, which exceeds the allocator limit of {int.MaxValue:N0} bytes."
            );
        }

        int slotsByteSize = (int)totalBytesNeeded;
        var entries = (FlatMapEntry<TKey, TValue>*)
            HeapAllocator.Allocate(slotsByteSize, CacheLineAlignment);

        if (entries == null)
        {
            throw new OutOfMemoryException(
                $"[Native Crash] Failed to allocate native slot buffer for {nameof(NativeFlatMap<TKey, TValue>)}."
            );
        }

        NativeMemoryBufferUtility.ZeroMemory(entries, slotsByteSize);

        var map = (NativeFlatMap<TKey, TValue>*)
            HeapAllocator.Allocate(sizeof(NativeFlatMap<TKey, TValue>), CacheLineAlignment);

        if (map == null)
        {
            HeapAllocator.Free(entries);
            throw new OutOfMemoryException(
                $"[Native Crash] Failed to allocate control block for {nameof(NativeFlatMap<TKey, TValue>)}."
            );
        }

        *map = new NativeFlatMap<TKey, TValue>((int)totalSlots, entries);
        return map;
    }

    private NativeFlatMap(int capacity, FlatMapEntry<TKey, TValue>* entries)
    {
        _capacity = capacity;
        _mask = capacity - 1;
        _maxCount = capacity >> 1; // 50% load factor limit
        _count = 0;
        _entries = entries;
    }

    /// <summary>Releases the native entry buffer and control block.</summary>
    public static void Free(NativeFlatMap<TKey, TValue>* map)
    {
        if (map == null)
            return;

        if (map->_entries != null)
        {
            HeapAllocator.Free(map->_entries);
        }

        HeapAllocator.Free(map);
    }

    /// <summary>Clears all entries in the map by zeroing the entry buffer.</summary>
    public void Clear()
    {
        if (_count == 0)
            return;

        int totalBytes = _capacity * sizeof(FlatMapEntry<TKey, TValue>);
        NativeMemoryBufferUtility.ZeroMemory(_entries, totalBytes);
        _count = 0;
    }

    #endregion

    #region Fast Lookups & Insertions

    /// <summary>Attempts to retrieve the value associated with <paramref name="key"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGetValue(TKey key, out TValue value)
    {
        int slot = FindSlot(key, out bool exists);
        if (exists)
        {
            value = _entries[slot].Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Determines whether <paramref name="key"/> exists in the map.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ContainsKey(TKey key)
    {
        FindSlot(key, out bool exists);
        return exists;
    }

    /// <summary>Attempts to insert <paramref name="key"/> and <paramref name="value"/> without overwriting existing keys.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, in TValue value)
    {
        int slot = FindSlot(key, out bool exists);
        if (exists)
            return false;

        // Strict 50% load factor enforcement: reject write if threshold reached
        if (_count >= _maxCount)
            return false;

        _count++;
        _entries[slot].Key = key;
        _entries[slot].Value = value;
        _entries[slot].State = 1;
        return true;
    }

    /// <summary>Returns a reference to an existing entry's value, or inserts a default entry and returns a reference to it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue GetValueRefOrAddDefault(TKey key, out bool exists, out bool success)
    {
        int slot = FindSlot(key, out exists);
        if (exists)
        {
            success = true;
            return ref _entries[slot].Value;
        }

        if (_count >= _maxCount)
        {
            success = false;
            return ref *(TValue*)null;
        }

        _count++;
        _entries[slot].Key = key;
        _entries[slot].State = 1;
        _entries[slot].Value = default;
        success = true;
        return ref _entries[slot].Value;
    }

    #endregion

    #region Backward-Shift Deletion (Robin Hood / Knuth)

    /// <summary>Removes <paramref name="key"/> using circular backward-shift deletion.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key)
    {
        int slot = FindSlot(key, out bool exists);
        if (!exists)
            return false;

        _count--;

        int mask = _mask;
        int i = slot;

        while (true)
        {
            _entries[i].State = 0;
            _entries[i].Key = default;
            _entries[i].Value = default;

            int j = i;

            while (true)
            {
                j = (j + 1) & mask;
                if (_entries[j].State == 0)
                    return true;

                int r = (int)(HashKey(_entries[j].Key) & (uint)mask);

                // Circular distance check: entry at j can shift to i if i lies on probe path from r to j
                if (((i - r) & mask) < ((j - r) & mask))
                    break;
            }

            _entries[i] = _entries[j];
            i = j;
        }
    }

    #endregion

    #region Internal Probing & Hashing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int FindSlot(TKey key, out bool exists)
    {
        uint hash = HashKey(key);
        int mask = _mask;
        int slot = (int)(hash & (uint)mask);

        while (_entries[slot].State != 0)
        {
            if (_entries[slot].Key.Equals(key))
            {
                exists = true;
                return slot;
            }
            slot = (slot + 1) & mask;
        }

        exists = false;
        return slot;
    }

    /// <summary>Computes a SplitMix64-mixed 32-bit hash for an unmanaged <paramref name="key"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashKey(TKey key)
    {
        ulong k;
        int size = sizeof(TKey);

        if (size == 8)
        {
            k = *(ulong*)&key;
        }
        else if (size == 4)
        {
            k = *(uint*)&key;
        }
        else if (size == 2)
        {
            k = *(ushort*)&key;
        }
        else if (size == 1)
        {
            k = *(byte*)&key;
        }
        else
        {
            // Fallback for multi-word structs (e.g. 16B UUID / Vector) using FNV-1a basis
            byte* p = (byte*)&key;
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < size; i++)
            {
                hash ^= p[i];
                hash *= 1099511628211UL;
            }
            k = hash;
        }

        // High-entropy 64-bit SplitMix64 finalizer mixer
        k ^= k >> 30;
        k *= 0xBF58476D1CE4E5B9UL;
        k ^= k >> 27;
        k *= 0x94D049BB133111EBUL;
        k ^= k >> 31;
        return (uint)k;
    }

    #endregion
}
