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

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->Count : 0;
    }

    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->Capacity : 0;
    }

    public int MaxCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->MaxCount : 0;
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap == null || _nativeMap->IsEmpty;
    }

    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null && _nativeMap->IsFull;
    }

    #endregion

    public FlatMap(int initialCapacity = 1024)
    {
        _nativeMap = NativeFlatMap<TKey, TValue>.Allocate(initialCapacity);
        _isDisposed = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeFlatMap<TKey, TValue>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap;
    }

    #region Operations

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->TryGetValue(key, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->ContainsKey(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, in TValue value)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->TryAdd(key, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue GetValueRefOrAddDefault(TKey key, out bool exists, out bool success)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return ref _nativeMap->GetValueRefOrAddDefault(key, out exists, out success);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->Remove(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeMap->Clear();
    }

    #endregion

    #region Enumeration

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return new Enumerator(_nativeMap->EntriesPtr, _nativeMap->Capacity);
    }

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

        public readonly ref readonly FlatMapEntry<TKey, TValue> Current
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
