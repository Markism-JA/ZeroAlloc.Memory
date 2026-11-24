using System.Runtime.CompilerServices;
using Memory.Collections.FlatMap;

namespace Memory.Collections.MonotonicTable;

// NOTE: Use this when the maximum count is under 4kb.

/// <summary>
/// Managed RAII lifecycle wrapper over <see cref="NativeMonotonicFlatMap{TKey, TValue}"/>.
/// Zero-allocation, open-addressing table governed by monotonic sequence versioning.
/// Backed by a fixed contiguous allocation on the native heap.
/// </summary>
public sealed unsafe class MonotonicFlatMap<TKey, TValue> : IDisposable
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    private NativeMonotonicFlatMap<TKey, TValue>* _nativeTable;
    private int _isDisposed;

    #region State & Metrics

    /// <summary>Gets the number of active entries stored in the table.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->Count : 0;
    }

    /// <summary>Gets the total slot capacity of the backing flat map.</summary>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->Capacity : 0;
    }

    /// <summary>Gets the maximum usable key count before load-factor saturation.</summary>
    public int MaxCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->MaxCount : 0;
    }

    /// <summary>Returns <c>true</c> if the table contains zero entries or is disposed.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable == null || _nativeTable->IsEmpty;
    }

    /// <summary>Returns <c>true</c> if the table has reached <see cref="MaxCount"/>.</summary>
    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null && _nativeTable->IsFull;
    }

    #endregion

    /// <summary>Initializes a new <see cref="MonotonicFlatMap{TKey, TValue}"/> with at least <paramref name="targetKeyCapacity"/> key capacity.</summary>
    public MonotonicFlatMap(int targetKeyCapacity = 1024)
    {
        _nativeTable = NativeMonotonicFlatMap<TKey, TValue>.Allocate(targetKeyCapacity);
        _isDisposed = 0;
    }

    /// <summary>Returns the raw pointer to the underlying native control block for C-ABI interop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeMonotonicFlatMap<TKey, TValue>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable;
    }

    #region Operations

    /// <summary>Attempts to retrieve the value and monotonic sequence associated with <paramref name="key"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out TValue value, out ulong sequence)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->TryGet(key, out value, out sequence);
    }

    /// <summary>Determines whether <paramref name="key"/> exists in the table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->ContainsKey(key);
    }

    /// <summary>Attempts to upsert <paramref name="key"/> if <paramref name="sequence"/> dominates the stored sequence.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MonotonicResult TryPut(
        TKey key,
        in TValue value,
        ulong sequence,
        bool allowEqualSequence = true
    )
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->TryPut(key, in value, sequence, allowEqualSequence);
    }

    /// <summary>Removes <paramref name="key"/> from the table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->Remove(key);
    }

    /// <summary>Clears all entries from the table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeTable->Clear();
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
        return new Enumerator(backing->EntriesPtr, backing->Capacity);
    }

    /// <summary>Zero-allocation enumerator over occupied <see cref="MonotonicEntry{TValue}"/> slots.</summary>
    public ref struct Enumerator
    {
        private readonly FlatMapEntry<TKey, MonotonicEntry<TValue>>* _entries;
        private readonly int _capacity;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(FlatMapEntry<TKey, MonotonicEntry<TValue>>* entries, int capacity)
        {
            _entries = entries;
            _capacity = capacity;
            _index = -1;
        }

        /// <summary>Advances the enumerator to the next occupied slot.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (++_index < _capacity)
            {
                if (_entries[_index].State != 0)
                    return true;
            }
            return false;
        }

        /// <summary>Gets a readonly reference to the current occupied entry.</summary>
        public readonly ref readonly FlatMapEntry<TKey, MonotonicEntry<TValue>> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _entries[_index];
        }
    }

    #endregion

    /// <summary>Releases the native heap buffer and control block.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        NativeMonotonicFlatMap<TKey, TValue>* table = _nativeTable;
        _nativeTable = null;

        if (table != null)
        {
            NativeMonotonicFlatMap<TKey, TValue>.Free(table);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(MonotonicFlatMap<,>));
    }
}
