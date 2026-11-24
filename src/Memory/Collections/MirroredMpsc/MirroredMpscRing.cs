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

    /// <summary>Gets the total byte capacity of the mirrored ring buffer.</summary>
    public long Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->Capacity : 0;
    }

    /// <summary>Gets the number of unconsumed bytes currently in the ring buffer.</summary>
    public long OccupiedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->OccupiedBytes : 0;
    }

    /// <summary>Gets the remaining free bytes available before backpressure occurs.</summary>
    public long FreeBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->FreeBytes : 0;
    }

    /// <summary>Returns <c>true</c> if the ring buffer contains no unconsumed data or is disposed.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing == null || _nativeRing->IsEmpty;
    }

    /// <summary>Gets the active consumer head sequence counter.</summary>
    public long HeadSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->HeadSequence : 0;
    }

    /// <summary>Gets the active producer tail sequence counter.</summary>
    public long TailSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->TailSequence : 0;
    }

    #endregion

    /// <summary>Initializes a new <see cref="MirroredMpscRing"/> backed by mirrored virtual memory.</summary>
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

    /// <summary>Attempts to write a contiguous unmanaged payload concurrently from any producer thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(byte* payload, int length)
    {
        return _nativeRing->TryWrite(payload, length, null, 0);
    }

    /// <summary>Attempts to write up to two unmanaged segments as a single contiguous frame concurrently.</summary>
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

    /// <summary>Attempts to write a span payload concurrently from any producer thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(ReadOnlySpan<byte> payload)
    {
        return _nativeRing->TryWrite(payload);
    }

    /// <summary>Attempts to write an unmanaged struct <typeparamref name="T"/> concurrently.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite<T>(in T value)
        where T : unmanaged
    {
        return _nativeRing->TryWrite(in value);
    }

    #endregion

    #region Single Consumer Read & Peeking

    /// <summary>Peeks at the next committed frame on the single consumer thread without advancing the head.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out BlockView view)
    {
        return _nativeRing->TryRead(out view);
    }

    /// <summary>Advances the consumer head past the frame described by <paramref name="view"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceConsumer(in BlockView view)
    {
        _nativeRing->AdvanceConsumer(in view);
    }

    /// <summary>Advances the consumer head by <paramref name="bytesToAdvance"/> aligned bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead(long bytesToAdvance)
    {
        _nativeRing->CommitRead(bytesToAdvance);
    }

    #endregion

    #region Batch Drain & Iteration

    /// <summary>Drains up to <paramref name="maxBatchSize"/> committed frames into <paramref name="consumer"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainBatch<TConsumer>(ref TConsumer consumer, int maxBatchSize = 32)
        where TConsumer : struct, IBlockConsumer
    {
        return _nativeRing->DrainBatch(ref consumer, maxBatchSize);
    }

    /// <summary>Begins a ref-struct batch reader over up to <paramref name="maxBatchSize"/> committed frames.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeMirroredMpscRing.BatchReader BeginBatch(int maxBatchSize = 32)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return NativeMirroredMpscRing.BeginBatch(_nativeRing, maxBatchSize);
    }

    #endregion

    /// <summary>Releases the mirrored virtual memory buffer and native control block.</summary>
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
