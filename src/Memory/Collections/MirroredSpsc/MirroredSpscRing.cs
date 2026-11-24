using System.Runtime.CompilerServices;
using Memory.Collections.MirroredMpsc;

namespace Memory.Collections.MirroredSpsc;

/// <summary>
/// Managed RAII lifecycle wrapper over <see cref="NativeMirroredSpscRing"/>.
/// Exposes variable-length, zero-allocation SPSC transport buffers with virtual memory mirroring.
/// </summary>
public sealed unsafe class MirroredSpscRing : IDisposable
{
    private NativeMirroredSpscRing* _nativeRing;
    private int _isDisposed;

    #region State & Metrics

    /// <summary>
    /// Gets the total byte capacity of the ring buffer.
    /// </summary>
    public long Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->Capacity : 0;
    }

    /// <summary>
    /// Gets the current number of unconsumed bytes in the ring buffer (including headers and alignment padding).
    /// </summary>
    public long OccupiedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->OccupiedBytes : 0;
    }

    /// <summary>
    /// Gets the remaining free bytes available for writes before backpressure occurs.
    /// </summary>
    public long FreeBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->FreeBytes : 0;
    }

    /// <summary>
    /// Lock-free check if the ring buffer currently contains no unconsumed data.
    /// </summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing == null || _nativeRing->IsEmpty;
    }

    /// <summary>
    /// Gets the active consumer head sequence counter.
    /// </summary>
    public long HeadSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->HeadSequence : 0;
    }

    /// <summary>
    /// Gets the active producer tail sequence counter.
    /// </summary>
    public long TailSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->TailSequence : 0;
    }

    #endregion

    public MirroredSpscRing(long byteCapacity = 2 * 1024 * 1024)
    {
        _nativeRing = NativeMirroredSpscRing.Allocate(byteCapacity);
        _isDisposed = 0;
    }

    /// <summary>
    /// Returns the raw pointer to the underlying native control block.
    /// Enables zero-trampoline unmanaged C-ABI sink dispatch without GCHandle roots.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeMirroredSpscRing* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeRing;
    }

    #region Value-Copy Writes

    /// <summary>
    /// Writes a single contiguous memory buffer into the ring with an automatic Store-Release commit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(byte* payload, int length)
    {
        return _nativeRing->TryWrite(payload, length, null, 0);
    }

    /// <summary>
    /// Writes up to two independent memory segments sequentially into a single contiguous block
    /// with an automatic Store-Release commit.
    /// </summary>
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

    /// <summary>
    /// Writes a Span slice directly into the ring buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(ReadOnlySpan<byte> payload)
    {
        return _nativeRing->TryWrite(payload);
    }

    /// <summary>
    /// Writes an unmanaged struct directly by reference without intermediate stackalloc or byte casts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite<T>(in T value)
        where T : unmanaged
    {
        return _nativeRing->TryWrite(in value);
    }

    #endregion

    #region Two-Phase In-Place Zero-Copy Write (Reservation)

    /// <summary>
    /// Reserves contiguous memory in the ring for in-place writing via an RAII scope.
    /// Auto-commits header, alignment, and tail sequence upon disposing the returned scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReserve(int payloadLength, out SpscWriteScope scope)
    {
        if (_nativeRing->TryReserve(payloadLength, out byte* dest, out SpscWriteTicket ticket))
        {
            scope = new SpscWriteScope(_nativeRing, dest, payloadLength, ticket);
            return true;
        }

        scope = default;
        return false;
    }

    /// <summary>
    /// Reserves contiguous payload memory directly inside the mirrored buffer without committing it.
    /// Caller writes payload directly to <paramref name="payloadDestination"/>, then calls <see cref="CommitWrite"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReserve(
        int payloadLength,
        out byte* payloadDestination,
        out SpscWriteTicket ticket
    )
    {
        return _nativeRing->TryReserve(payloadLength, out payloadDestination, out ticket);
    }

    /// <summary>
    /// Publishes a previously reserved block, advancing the producer sequence counter with a single Store-Release barrier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitWrite(in SpscWriteTicket ticket)
    {
        _nativeRing->CommitWrite(in ticket);
    }

    /// <summary>
    /// Explicit documentation helper for an aborted reservation. Safe no-op.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AbortReserve(in SpscWriteTicket ticket)
    {
        _nativeRing->AbortReserve(in ticket);
    }

    #endregion

    #region Amortized Batch Writes (No Immediate Store-Release)

    /// <summary>
    /// Writes a block into the mirrored buffer updating a caller's local working tail register.
    /// Does NOT issue a volatile memory barrier to the tail sequence. Must be committed via <see cref="CommitBatch"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteUncommitted(
        byte* segment1Src,
        int segment1Length,
        byte* segment2Src,
        int segment2Length,
        ref long workingTail
    )
    {
        return _nativeRing->TryWriteUncommitted(
            segment1Src,
            segment1Length,
            segment2Src,
            segment2Length,
            ref workingTail
        );
    }

    /// <summary>
    /// Reserves contiguous payload memory against a local working tail register without publishing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReserveUncommitted(
        int payloadLength,
        ref long workingTail,
        out byte* payloadDestination
    )
    {
        return _nativeRing->TryReserveUncommitted(
            payloadLength,
            ref workingTail,
            out payloadDestination
        );
    }

    /// <summary>
    /// Publishes the accumulated batch tail with a single Store-Release memory barrier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitBatch(long newTail)
    {
        _nativeRing->CommitBatch(newTail);
    }

    /// <summary>
    /// Gets the current producer tail for initializing a batch write session.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetProducerTail() => _nativeRing->GetProducerTail();

    #endregion

    #region Single Read & Peeking

    /// <summary>
    /// Peeks at the active block at the head of the ring buffer without consuming it.
    /// Must be paired with <see cref="AdvanceConsumer(in BlockView)"/> or <see cref="CommitRead(long)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out BlockView view)
    {
        return _nativeRing->TryRead(out view);
    }

    /// <summary>
    /// Advances the consumer head counter past the given <see cref="BlockView"/>, releasing the memory back to the producer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceConsumer(in BlockView view)
    {
        _nativeRing->AdvanceConsumer(in view);
    }

    /// <summary>
    /// Advances the consumer head counter directly by the specified byte count with a Store-Release barrier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead(long bytesToAdvance)
    {
        _nativeRing->CommitRead(bytesToAdvance);
    }

    #endregion

    #region Batch Drain & Iteration

    /// <summary>
    /// Drains up to maxBatchSize committed blocks directly into an inlined struct consumer with ZERO virtual call overhead.
    /// Inlines consumer execution and issues a single Store-Release barrier to _head for the entire batch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainBatch<TConsumer>(ref TConsumer consumer, int maxBatchSize = 32)
        where TConsumer : struct, IBlockConsumer
    {
        return _nativeRing->DrainBatch(ref consumer, maxBatchSize);
    }

    /// <summary>
    /// Opens an RAII batch reader scope that iterates up to <paramref name="maxBatchSize"/> frames
    /// with zero allocations and commits the total advanced bytes in a single Store-Release barrier upon disposal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeMirroredSpscRing.BatchReader BeginBatch(int maxBatchSize = 32)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return NativeMirroredSpscRing.BeginBatch(_nativeRing, maxBatchSize);
    }

    #endregion

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        NativeMirroredSpscRing* ring = _nativeRing;
        _nativeRing = null;

        if (ring != null)
        {
            NativeMirroredSpscRing.Free(ring);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(MirroredSpscRing));
    }
}
