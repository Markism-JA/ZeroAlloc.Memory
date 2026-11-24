using System.Runtime.CompilerServices;

namespace Memory.Collections.FlatMap;

/// <summary>
/// Managed RAII lifecycle wrapper over <see cref="NativeFlatMap{TKey, TValue}"/>.
/// Zero-allocation, open-addressing hash table backed by native heap memory with circular backward-shift deletion.
/// </summary>
public sealed unsafe class FlatMap<TKey, TValue> : IDisposable
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    private NativeFlatMap<TKey, TValue>* _nativeMap;
    private int _isDisposed;

    #region State & Metrics

    /// <summary>Gets the number of active key-value pairs stored in the map.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->Count : 0;
    }

    /// <summary>Gets the total slot capacity of the backing hash table.</summary>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->Capacity : 0;
    }

    /// <summary>Gets the maximum usable item count before load-factor saturation.</summary>
    public int MaxCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->MaxCount : 0;
    }

    /// <summary>Returns <c>true</c> if the map contains zero entries or is disposed.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap == null || _nativeMap->IsEmpty;
    }

    /// <summary>Returns <c>true</c> if the map has reached <see cref="MaxCount"/>.</summary>
    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null && _nativeMap->IsFull;
    }

    #endregion

    /// <summary>Initializes a new <see cref="FlatMap{TKey, TValue}"/> with at least <paramref name="initialCapacity"/> key capacity.</summary>
    public FlatMap(int initialCapacity = 1024)
    {
        _nativeMap = NativeFlatMap<TKey, TValue>.Allocate(initialCapacity);
        _isDisposed = 0;
    }

    /// <summary>Returns the raw pointer to the underlying native control block for C-ABI interop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeFlatMap<TKey, TValue>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap;
    }

    #region Operations

    /// <summary>Attempts to retrieve the value associated with <paramref name="key"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->TryGetValue(key, out value);
    }

    /// <summary>Determines whether <paramref name="key"/> exists in the map.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->ContainsKey(key);
    }

    /// <summary>Attempts to insert <paramref name="key"/> and <paramref name="value"/> without overwriting existing keys.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, in TValue value)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->TryAdd(key, in value);
    }

    /// <summary>Returns a reference to an existing entry's value, or inserts a default entry and returns a reference to it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue GetValueRefOrAddDefault(TKey key, out bool exists, out bool success)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return ref _nativeMap->GetValueRefOrAddDefault(key, out exists, out success);
    }

    /// <summary>Removes <paramref name="key"/> using circular backward-shift deletion.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->Remove(key);
    }

    /// <summary>Clears all entries from the map.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeMap->Clear();
    }

    #endregion

    #region Enumeration

    /// <summary>Returns a zero-allocation ref-struct enumerator over occupied entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return new Enumerator(_nativeMap->EntriesPtr, _nativeMap->Capacity);
    }

    /// <summary>Zero-allocation enumerator over occupied <see cref="FlatMapEntry{TKey, TValue}"/> slots.</summary>
    public ref struct Enumerator
    {
        private readonly FlatMapEntry<TKey, TValue>* _entries;
        private readonly int _capacity;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(FlatMapEntry<TKey, TValue>* entries, int capacity)
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
        public readonly ref readonly FlatMapEntry<TKey, TValue> Current
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

        NativeFlatMap<TKey, TValue>* map = _nativeMap;
        _nativeMap = null;

        if (map != null)
        {
            NativeFlatMap<TKey, TValue>.Free(map);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(FlatMap<TKey, TValue>));
    }
}
