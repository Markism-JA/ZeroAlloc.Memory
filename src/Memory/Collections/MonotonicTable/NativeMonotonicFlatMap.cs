using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Collections.FlatMap;

namespace Memory.Collections.MonotonicTable;

/// <summary>
/// Pure unmanaged open-addressing table enforcing monotonic sequence versioning per key.
/// Backed directly by <see cref="NativeFlatMap{TKey,TValue}"/> on the native C-heap.
/// Suitable for fixed-capacity, high-throughput caches with zero virtual memory reservation overhead.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct NativeMonotonicFlatMap<TKey, TValue>
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    private const int CacheLineAlignment = 64;

    private NativeFlatMap<TKey, MonotonicEntry<TValue>>* _map;

    #region State & Metrics

    public readonly int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map != null ? _map->Count : 0;
    }

    public readonly int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map != null ? _map->Capacity : 0;
    }

    public readonly int MaxCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map != null ? _map->MaxCount : 0;
    }

    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map == null || _map->IsEmpty;
    }

    public readonly bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map != null && _map->IsFull;
    }

    public readonly NativeFlatMap<TKey, MonotonicEntry<TValue>>* BackingMapPtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map;
    }

    #endregion

    #region Allocation & Teardown

    public static NativeMonotonicFlatMap<TKey, TValue>* Allocate(int targetKeyCapacity = 1024)
    {
        var nativeMap = NativeFlatMap<TKey, MonotonicEntry<TValue>>.Allocate(targetKeyCapacity);
        var table = (NativeMonotonicFlatMap<TKey, TValue>*)
            HeapAllocator.Allocate(
                sizeof(NativeMonotonicFlatMap<TKey, TValue>),
                CacheLineAlignment
            );

        if (table == null)
        {
            NativeFlatMap<TKey, MonotonicEntry<TValue>>.Free(nativeMap);
            throw new OutOfMemoryException(
                $"[Native Crash] Failed to allocate control block for {nameof(NativeMonotonicFlatMap<TKey, TValue>)}."
            );
        }

        table->_map = nativeMap;
        return table;
    }

    public static void Free(NativeMonotonicFlatMap<TKey, TValue>* table)
    {
        if (table == null)
            return;

        if (table->_map != null)
        {
            NativeFlatMap<TKey, MonotonicEntry<TValue>>.Free(table->_map);
            table->_map = null;
        }

        HeapAllocator.Free(table);
    }

    #endregion

    #region Operations

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGet(TKey key, out TValue value, out ulong sequence)
    {
        if (_map->TryGetValue(key, out MonotonicEntry<TValue> entry))
        {
            value = entry.Value;
            sequence = entry.Sequence;
            return true;
        }

        value = default;
        sequence = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ContainsKey(TKey key) => _map->ContainsKey(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MonotonicResult TryPut(
        TKey key,
        in TValue value,
        ulong sequence,
        bool allowEqualSequence = true
    )
    {
        ref MonotonicEntry<TValue> slot = ref _map->GetValueRefOrAddDefault(
            key,
            out bool exists,
            out bool success
        );

        if (!success)
            return MonotonicResult.Stale; // Capacity saturated (50% max load factor breached)

        if (!exists)
        {
            slot = new MonotonicEntry<TValue>(in value, sequence);
            return MonotonicResult.Inserted;
        }

        if (sequence > slot.Sequence)
        {
            slot = new MonotonicEntry<TValue>(in value, sequence);
            return MonotonicResult.Advanced;
        }

        if (allowEqualSequence && sequence == slot.Sequence)
        {
            slot = new MonotonicEntry<TValue>(in value, sequence);
            return MonotonicResult.ReplacedAtSameSequence;
        }

        return MonotonicResult.Stale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key) => _map->Remove(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => _map->Clear();

    #endregion
}
