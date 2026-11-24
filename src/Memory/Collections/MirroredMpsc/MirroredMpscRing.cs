using System.Runtime.CompilerServices;

namespace Memory.Collections.MirroredMpsc;

/// <summary>
/// Managed RAII lifecycle wrapper over <see cref="NativeMirroredMpscRing"/>.
/// Exposes lock-free, zero-allocation MPSC transport buffers with virtual memory mirroring.
/// </summary>
public sealed unsafe class MirroredMpscRing : IDisposable
{
    private NativeMirroredMpscRing* _nativeRing;
    private int _isDisposed;

    #region State & Metrics

    public long Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->Capacity : 0;
    }

    public long OccupiedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->OccupiedBytes : 0;
    }

    public long FreeBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->FreeBytes : 0;
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

    public MirroredMpscRing(long byteCapacity = 2 * 1024 * 1024)
    {
        _nativeRing = NativeMirroredMpscRing.Allocate(byteCapacity);
        _isDisposed = 0;
    }

    /// <summary>
    /// Returns the raw pointer to the underlying native control block.
    /// Enables unmanaged zero-trampoline invocation without GCHandle roots.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeMirroredMpscRing* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing;
    }

    #region Multi-Producer Concurrent Writes

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(byte* payload, int length)
    {
        return _nativeRing->TryWrite(payload, length, null, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(
        byte* segment1Src,
        int segment1Length,
        byte* segment2Src = null,
        int segment2Length = 0
    )
    {
        return _nativeRing->TryWrite(segment1Src, segment1Length, segment2Src, segment2Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(ReadOnlySpan<byte> payload)
    {
        return _nativeRing->TryWrite(payload);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite<T>(in T value)
        where T : unmanaged
    {
        return _nativeRing->TryWrite(in value);
    }

    #endregion

    #region Single Consumer Read & Peeking

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out BlockView view)
    {
        return _nativeRing->TryRead(out view);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceConsumer(in BlockView view)
    {
        _nativeRing->AdvanceConsumer(in view);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead(long bytesToAdvance)
    {
        _nativeRing->CommitRead(bytesToAdvance);
    }

    #endregion

    #region Batch Drain & Iteration

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainBatch<TConsumer>(ref TConsumer consumer, int maxBatchSize = 32)
        where TConsumer : struct, IBlockConsumer
    {
        return _nativeRing->DrainBatch(ref consumer, maxBatchSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeMirroredMpscRing.BatchReader BeginBatch(int maxBatchSize = 32)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return NativeMirroredMpscRing.BeginBatch(_nativeRing, maxBatchSize);
    }

    #endregion

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        NativeMirroredMpscRing* ring = _nativeRing;
        _nativeRing = null;

        if (ring != null)
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(MirroredMpscRing));
    }
}
