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

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->Count : 0;
    }

    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->Capacity : 0;
    }

    public int MaxCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null ? _nativeTable->MaxCount : 0;
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable == null || _nativeTable->IsEmpty;
    }

    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeTable != null && _nativeTable->IsFull;
    }

    #endregion

    public MonotonicFlatMap(int targetKeyCapacity = 1024)
    {
        _nativeTable = NativeMonotonicFlatMap<TKey, TValue>.Allocate(targetKeyCapacity);
        _isDisposed = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeMonotonicFlatMap<TKey, TValue>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable;
    }

    #region Operations

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out TValue value, out ulong sequence)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->TryGet(key, out value, out sequence);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->ContainsKey(key);
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeTable->Remove(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeTable->Clear();
    }

    #endregion

    #region Enumeration

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        var backing = _nativeTable->BackingMapPtr;
        return new Enumerator(backing->EntriesPtr, backing->Capacity);
    }

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

        public readonly ref readonly FlatMapEntry<TKey, MonotonicEntry<TValue>> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _entries[_index];
        }
    }

    #endregion

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
