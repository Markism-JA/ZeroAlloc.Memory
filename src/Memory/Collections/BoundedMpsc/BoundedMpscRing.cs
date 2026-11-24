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

    public long Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->Capacity : 0;
    }

    public long OccupiedCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->OccupiedCount : 0;
    }

    public long FreeCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->FreeCount : 0;
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing == null || _nativeRing->IsEmpty;
    }

    public long HeadSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->HeadSequence : 0;
    }

    public long TailSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->TailSequence : 0;
    }

    #endregion

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in T item)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing->TryWrite(in item);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out T item)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing->TryRead(out item);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainBatch<TConsumer>(ref TConsumer consumer, int maxBatchSize = 32)
        where TConsumer : struct, IValueConsumer<T>
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing->DrainBatch(ref consumer, maxBatchSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeBoundedMpscRing<T>.BatchReader BeginBatch(int maxBatchSize = 32)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return NativeBoundedMpscRing<T>.BeginBatch(_nativeRing, maxBatchSize);
    }

    #endregion

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
