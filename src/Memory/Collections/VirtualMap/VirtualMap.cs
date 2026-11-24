using System.Runtime.CompilerServices;

namespace Memory.Collections.VirtualMap;

/// <summary>
/// Managed RAII lifecycle wrapper over <see cref="NativeVirtualMap{TKey, TValue}"/>.
/// High-performance, zero-allocation open-addressing hash table backed by demand-committed virtual memory.
/// </summary>
/// <typeparam name="TKey">The unmanaged key type implementing <see cref="IEquatable{TKey}"/>.</typeparam>
/// <typeparam name="TValue">The unmanaged value type stored in table entries.</typeparam>
public sealed unsafe class VirtualMap<TKey, TValue> : IDisposable
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    private NativeVirtualMap<TKey, TValue>* _nativeMap;
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

    public int KeyCapacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->KeyCapacity : 0;
    }

    public int MaxSlotCapacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->MaxSlotCapacity : 0;
    }

    public int CommittedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeMap != null ? _nativeMap->CommittedBytes : 0;
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

    public VirtualMap(int initialKeyCapacity = NativeVirtualMap<TKey, TValue>.DefaultMinKeyCapacity)
    {
        _nativeMap = NativeVirtualMap<TKey, TValue>.Allocate(initialKeyCapacity);
        _isDisposed = 0;
    }

    /// <summary>
    /// Returns the raw pointer to the underlying native control block for C-ABI interop and unmanaged embedding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeVirtualMap<TKey, TValue>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap;
    }

    #region Operations

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(in TKey key, out TValue value)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->TryGetValue(in key, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(in TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->ContainsKey(in key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(in TKey key, in TValue value)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->TryAdd(in key, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue GetValueRefOrAddDefault(in TKey key, out bool exists, out bool success)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return ref _nativeMap->GetValueRefOrAddDefault(in key, out exists, out success);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(in TKey key)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeMap->Remove(in key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeMap->Clear();
    }

    /// <summary>
    /// Purges excess physical memory backing to the OS kernel without releasing the virtual address reservation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PassiveDecommitReclaim()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeMap->PassiveDecommitReclaim();
    }

    #endregion

    #region Enumeration

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return new Enumerator(_nativeMap->SlotsPtr, _nativeMap->Capacity);
    }

    public ref struct Enumerator
    {
        private readonly VirtualMapEntry<TKey, TValue>* _slots;
        private readonly int _capacity;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(VirtualMapEntry<TKey, TValue>* slots, int capacity)
        {
            _slots = slots;
            _capacity = capacity;
            _index = -1;
        }

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

        public readonly ref readonly VirtualMapEntry<TKey, TValue> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _slots[_index];
        }
    }

    #endregion

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        NativeVirtualMap<TKey, TValue>* map = _nativeMap;
        _nativeMap = null;

        if (map != null)
        {
            NativeVirtualMap<TKey, TValue>.Free(map);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(VirtualMap<TKey, TValue>));
    }
}
