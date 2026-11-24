using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Collections.VirtualMap;

namespace Memory.Collections.MonotonicTable;

/// <summary>
/// Pure unmanaged hash table enforcing monotonic sequence versioning per key.
/// Internally drives an unmanaged <see cref="NativeVirtualMap{TKey, TValue}"/> over reserved virtual memory.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct NativeMonotonicVirtualMap<TKey, TValue>
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    private const int _cacheLineAlignment = 64;

    private NativeVirtualMap<TKey, MonotonicEntry<TValue>>* _map;

    #region State & Metrics

    /// <summary>Gets the number of active entries stored in the table.</summary>
    public readonly int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map != null ? _map->Count : 0;
    }

    /// <summary>Gets the current committed slot capacity of the backing virtual map.</summary>
    public readonly int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map != null ? _map->Capacity : 0;
    }

    /// <summary>Gets the maximum key capacity before resizing.</summary>
    public readonly int KeyCapacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map != null ? _map->KeyCapacity : 0;
    }

    /// <summary>Gets the number of physical bytes currently committed by the OS.</summary>
    public readonly int CommittedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map != null ? _map->CommittedBytes : 0;
    }

    /// <summary>Returns <c>true</c> if the table contains zero entries.</summary>
    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map == null || _map->IsEmpty;
    }

    /// <summary>Gets the raw unmanaged pointer to the backing <see cref="NativeVirtualMap{TKey, TValue}"/>.</summary>
    public readonly NativeVirtualMap<TKey, MonotonicEntry<TValue>>* BackingMapPtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _map;
    }

    #endregion

    #region Allocation & Teardown

    /// <summary>
    /// Allocates an unmanaged <see cref="NativeMonotonicVirtualMap{TKey,TValue}"/> control block and its underlying virtual map.
    /// </summary>
    public static NativeMonotonicVirtualMap<TKey, TValue>* Allocate(
        int initialKeyCapacity =
            NativeVirtualMap<TKey, MonotonicEntry<TValue>>.DefaultMinKeyCapacity
    )
    {
        NativeVirtualMap<TKey, MonotonicEntry<TValue>>* nativeMap = NativeVirtualMap<
            TKey,
            MonotonicEntry<TValue>
        >.Allocate(initialKeyCapacity);

        var table = (NativeMonotonicVirtualMap<TKey, TValue>*)
            HeapAllocator.Allocate(
                sizeof(NativeMonotonicVirtualMap<TKey, TValue>),
                _cacheLineAlignment
            );

        if (table == null)
        {
            NativeVirtualMap<TKey, MonotonicEntry<TValue>>.Free(nativeMap);
            throw new OutOfMemoryException(
                "[Native Crash] Failed to allocate control block for NativeMonotonicTable."
            );
        }

        table->_map = nativeMap;
        return table;
    }

    /// <summary>
    /// Frees the backing unmanaged virtual map and the native control block.
    /// </summary>
    public static void Free(NativeMonotonicVirtualMap<TKey, TValue>* table)
    {
        if (table == null)
            return;

        if (table->_map != null)
        {
            NativeVirtualMap<TKey, MonotonicEntry<TValue>>.Free(table->_map);
            table->_map = null;
        }

        HeapAllocator.Free(table);
    }

    #endregion

    #region Operations

    /// <summary>
    /// Attempts to retrieve the value and recorded sequence associated with the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGet(in TKey key, out TValue value, out ulong sequence)
    {
        if (_map->TryGetValue(in key, out MonotonicEntry<TValue> entry))
        {
            value = entry.Value;
            sequence = entry.Sequence;
            return true;
        }

        value = default;
        sequence = 0;
        return false;
    }

    /// <summary>
    /// Determines whether an entry exists for the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ContainsKey(in TKey key) => _map->ContainsKey(in key);

    /// <summary>
    /// Attempts to upsert a value under a candidate sequence number.
    /// Writes are rejected if the candidate sequence is older than the currently stored sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MonotonicResult TryPut(
        in TKey key,
        in TValue value,
        ulong sequence,
        bool allowEqualSequence = true
    )
    {
        ref MonotonicEntry<TValue> slot = ref _map->GetValueRefOrAddDefault(
            in key,
            out bool exists,
            out bool success
        );

        if (!success)
            return MonotonicResult.Stale;

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

    /// <summary>
    /// Removes the entry associated with the key and circularly repairs probe chains.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(in TKey key) => _map->Remove(in key);

    /// <summary>
    /// Resets all entries without decommitting physical pages.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => _map->Clear();

    /// <summary>
    /// Decommits excess physical pages back to the kernel.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PassiveDecommitReclaim() => _map->PassiveDecommitReclaim();

    #endregion
}
