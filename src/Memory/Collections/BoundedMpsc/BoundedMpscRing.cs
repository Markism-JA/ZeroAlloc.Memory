using System.Runtime.CompilerServices;

namespace Memory.Collections.BoundedMpsc;

/// <summary>
/// Managed RAII lifecycle wrapper over <see cref="NativeBoundedMpscRing{T}"/>.
/// Exposes a high-performance, non-mirrored, bounded lock-free MPSC queue for fixed-size unmanaged types.
/// </summary>
/// <typeparam name="T">The unmanaged payload type.</typeparam>
public sealed unsafe class BoundedMpscRing<T> : IDisposable
    where T : unmanaged
{
    private NativeBoundedMpscRing<T>* _nativeRing;
    private int _isDisposed;

    #region State & Metrics

    /// <summary>Gets the total element capacity of the ring buffer.</summary>
    public long Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->Capacity : 0;
    }

    /// <summary>Gets the approximate number of unconsumed items currently in the ring.</summary>
    public long OccupiedCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->OccupiedCount : 0;
    }

    /// <summary>Gets the remaining free element slots available before backpressure occurs.</summary>
    public long FreeCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->FreeCount : 0;
    }

    /// <summary>Returns <c>true</c> if the ring buffer contains no unconsumed items or is disposed.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing == null || _nativeRing->IsEmpty;
    }

    /// <summary>Gets the current consumer head sequence counter.</summary>
    public long HeadSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->HeadSequence : 0;
    }

    /// <summary>Gets the current producer tail sequence counter.</summary>
    public long TailSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->TailSequence : 0;
    }

    #endregion

    /// <summary>Initializes a new <see cref="BoundedMpscRing{T}"/> with at least <paramref name="elementCapacity"/> slots (rounded up to a power of two).</summary>
    public BoundedMpscRing(int elementCapacity = 16384)
    {
        _nativeRing = NativeBoundedMpscRing<T>.Allocate(elementCapacity);
        _isDisposed = 0;
    }

    /// <summary>
    /// Returns the raw pointer to the underlying native control block for C-ABI interop and unmanaged embedding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeBoundedMpscRing<T>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing;
    }

    #region Enqueue & Dequeue Operations

    /// <summary>Attempts to enqueue <paramref name="item"/> concurrently from any producer thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in T item)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing->TryWrite(in item);
    }

    /// <summary>Attempts to dequeue the next committed item on the single consumer thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out T item)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing->TryRead(out item);
    }

    /// <summary>Drains up to <paramref name="maxBatchSize"/> committed items into <paramref name="consumer"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainBatch<TConsumer>(ref TConsumer consumer, int maxBatchSize = 32)
        where TConsumer : struct, IValueConsumer<T>
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing->DrainBatch(ref consumer, maxBatchSize);
    }

    /// <summary>Begins a ref-struct batch reader over up to <paramref name="maxBatchSize"/> committed items.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeBoundedMpscRing<T>.BatchReader BeginBatch(int maxBatchSize = 32)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return NativeBoundedMpscRing<T>.BeginBatch(_nativeRing, maxBatchSize);
    }

    #endregion

    /// <summary>Releases the underlying native ring control block and buffer.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        NativeBoundedMpscRing<T>* ring = _nativeRing;
        _nativeRing = null;

        if (ring != null)
        {
            NativeBoundedMpscRing<T>.Free(ring);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(BoundedMpscRing<T>));
    }
}
