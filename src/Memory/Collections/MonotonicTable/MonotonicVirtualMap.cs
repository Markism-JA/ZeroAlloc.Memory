using System.Runtime.CompilerServices;
using Memory.Collections.VirtualMap;

namespace Memory.Collections.MonotonicTable;

// NOTE: Use this when the maximum count is greater than 4kb. That is the size of a single page.

/// <summary>
/// Managed RAII wrapper over <see cref="NativeMonotonicVirtualMap{TKey,TValue}"/>.
/// High-performance, zero-allocation open-addressing table governed by monotonic sequence versioning.
/// Backed by demand-committed virtual memory.
/// </summary>
public sealed unsafe class MonotonicVirtualMap<TKey, TValue>(
    int initialKeyCapacity = NativeVirtualMap<TKey, MonotonicEntry<TValue>>.DefaultMinKeyCapacity
) : IDisposable
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    private NativeMonotonicVirtualMap<TKey, TValue>* _nativeTable = NativeMonotonicVirtualMap<
        TKey,
        TValue
    >.Allocate(initialKeyCapacity);

    private int _isDisposed = 0;

    #region State & Metrics

    /// <summary>Gets the number of active entries stored in the table.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->Count : 0;
    }

    /// <summary>Gets the current committed slot capacity of the backing virtual map.</summary>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->Capacity : 0;
    }

    /// <summary>Gets the maximum key capacity before resizing.</summary>
    public int KeyCapacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->KeyCapacity : 0;
    }

    /// <summary>Gets the number of physical bytes currently committed by the OS.</summary>
    public int CommittedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->CommittedBytes : 0;
    }

    /// <summary>Returns <c>true</c> if the table contains zero entries or is disposed.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable == null || _nativeTable->IsEmpty;
    }

    #endregion

    /// <summary>Returns the raw pointer to the underlying native control block for C-ABI interop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeMonotonicVirtualMap<TKey, TValue>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable;
    }

    #region Operations

    /// <summary>Attempts to retrieve the value and monotonic sequence associated with <paramref name="key"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(in TKey key, out TValue value, out ulong sequence)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->TryGet(in key, out value, out sequence);
    }

    /// <summary>Determines whether <paramref name="key"/> exists in the table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(in TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->ContainsKey(in key);
    }

    /// <summary>Attempts to upsert <paramref name="key"/> if <paramref name="sequence"/> dominates the stored sequence.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MonotonicResult TryPut(
        in TKey key,
        in TValue value,
        ulong sequence,
        bool allowEqualSequence = true
    )
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->TryPut(in key, in value, sequence, allowEqualSequence);
    }

    /// <summary>Removes <paramref name="key"/> from the table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(in TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->Remove(in key);
    }

    /// <summary>Clears all entries from the table without releasing the virtual reservation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeTable->Clear();
    }

    /// <summary>Purges excess physical memory backing to the OS kernel without releasing the virtual reservation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PassiveDecommitReclaim()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeTable->PassiveDecommitReclaim();
    }

    #endregion

    #region Enumeration

    /// <summary>Returns a zero-allocation ref-struct enumerator over occupied entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        var backing = _nativeTable->BackingMapPtr;
        return new Enumerator(backing->SlotsPtr, backing->Capacity);
    }

    /// <summary>Zero-allocation enumerator over occupied <see cref="MonotonicEntry{TValue}"/> slots.</summary>
    public ref struct Enumerator
    {
        private readonly VirtualMapEntry<TKey, MonotonicEntry<TValue>>* _slots;
        private readonly int _capacity;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(VirtualMapEntry<TKey, MonotonicEntry<TValue>>* slots, int capacity)
        {
            _slots = slots;
            _capacity = capacity;
            _index = -1;
        }

        /// <summary>Advances the enumerator to the next occupied slot.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (++_index < _capacity)
            {
                if (_slots[_index].State != 0)
                    return true;
            }
            return false;
        }

        /// <summary>Gets a readonly reference to the current occupied entry.</summary>
        public readonly ref readonly VirtualMapEntry<TKey, MonotonicEntry<TValue>> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _slots[_index];
        }
    }

    #endregion

    /// <summary>Releases the virtual memory reservation and native control block.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        NativeMonotonicVirtualMap<TKey, TValue>* table = _nativeTable;
        _nativeTable = null;

        if (table != null)
        {
            NativeMonotonicVirtualMap<TKey, TValue>.Free(table);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(MonotonicVirtualMap<TKey, TValue>));
    }
}
