using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Collections.MirroredMpsc;
using Memory.Utilities;

namespace Memory.Collections.MirroredSpsc;

/// <summary>
/// Unmanaged implementation of a zero-allocation, variable-length
/// Single-Producer Single-Consumer (SPSC) circular ring buffer backed by
/// mirrored virtual memory addresses.
/// </summary>
public unsafe struct NativeMirroredSpscRing
{
    private const ulong _windowsAllocationGranularity = 64 * 1024; // 64 kb hardware boundary

    public const ulong MaxAllowedCapacity = 0x40000000UL; // 1 GB limit

    private readonly long _bufferSize;
    private readonly long _bufferMask;
    private readonly byte* _bufferStart;
    private readonly void* _nativeHandle;

    private PaddedSequence _head;
    private PaddedSequence _tail;

    private PaddedSequence _cachedHead;
    private PaddedSequence _cachedTail;

    private long _reservedTail;

    #region State & Metrics

    /// <summary>
    /// Gets the total byte capacity of the ring buffer.
    /// </summary>
    public readonly long Capacity => _bufferSize;

    /// <summary>
    /// Gets the current number of unconsumed bytes in the ring buffer (including headers and alignment padding).
    /// </summary>
    public long OccupiedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long head = Volatile.Read(ref _head.Value);
            long tail = Volatile.Read(ref _tail.Value);
            long count = tail - head;
            return count < 0 ? 0 : count;
        }
    }

    /// <summary>
    /// Gets the remaining free bytes available for writes before backpressure occurs.
    /// </summary>
    public long FreeBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long free = _bufferSize - OccupiedBytes;
            return free < 0 ? 0 : free;
        }
    }

    /// <summary>
    /// Lock-free check if the ring buffer currently contains no unconsumed data.
    /// </summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _head.Value) >= Volatile.Read(ref _tail.Value);
    }

    /// <summary>
    /// Gets the active consumer head sequence counter.
    /// </summary>
    public long HeadSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _head.Value);
    }

    /// <summary>
    /// Gets the active producer tail sequence counter.
    /// </summary>
    public long TailSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _tail.Value);
    }

    #endregion

    #region Allocation & Teardown

    /// <summary>
    /// Allocates a new instance of <see cref="NativeMirroredSpscRing"/> and maps a mirrored virtual memory buffer.
    /// The control block is 64-byte cache-line aligned; the mirrored buffer is aligned to OS allocation granularity.
    /// </summary>
    public static NativeMirroredSpscRing* Allocate(long byteCapacity = 2 * 1024 * 1024)
    {
        if (byteCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(byteCapacity),
                "Capacity must be positive."
            );

        ulong scaledCapacity = BitOperation.RoundUpToPowerOf2((ulong)byteCapacity);
        ulong minAlignment = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? _windowsAllocationGranularity
            : (ulong)PageAllocator.SystemPageSize;

        if (scaledCapacity < minAlignment)
            scaledCapacity = minAlignment;

        if (scaledCapacity > MaxAllowedCapacity)
            throw new ArgumentOutOfRangeException(
                nameof(byteCapacity),
                $"Capacity exceeds system limit of {MaxAllowedCapacity} bytes."
            );

        byte* bufferStart = PageAllocator.CreateMirroredBuffer(
            (int)scaledCapacity,
            out void* nativeHandle
        );
        if (bufferStart == null)
            throw new OutOfMemoryException(
                "[Kernel Fault] Virtual memory mirror map assignment failed."
            );

        var ring = (NativeMirroredSpscRing*)
            HeapAllocator.Allocate(sizeof(NativeMirroredSpscRing), 64);
        if (ring == null)
        {
            PageAllocator.FreeMirroredBuffer(bufferStart, (long)scaledCapacity, nativeHandle);
            throw new OutOfMemoryException(
                "[Memory Crash] Failed to allocate control block for NativeMirroredSpscRing."
            );
        }

        *ring = new NativeMirroredSpscRing((long)scaledCapacity, bufferStart, nativeHandle);
        return ring;
    }

    private NativeMirroredSpscRing(long bufferSize, byte* bufferStart, void* nativeHandle)
    {
        _bufferSize = bufferSize;
        _bufferMask = bufferSize - 1;
        _bufferStart = bufferStart;
        _nativeHandle = nativeHandle;

        _head.Value = 0;
        _tail.Value = 0;
        _cachedHead.Value = 0;
        _cachedTail.Value = 0;
        _reservedTail = 0;
    }

    #endregion

    public static void Free(NativeMirroredSpscRing* ring)
    {
        if (ring == null)
            return;

        if (ring->_bufferStart != null)
        {
            PageAllocator.FreeMirroredBuffer(
                ring->_bufferStart,
                ring->_bufferSize,
                ring->_nativeHandle
            );
        }

        HeapAllocator.Free(ring);
    }

    #region Value-Copy Writes

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
        Debug.Assert(segment1Length >= 0 && segment2Length >= 0);
        Debug.Assert(segment1Length == 0 || segment1Src != null);
        Debug.Assert(segment2Length == 0 || segment2Src != null);

        int headerSize = sizeof(SpscBlockHeader);
        int totalPayloadLength = segment1Length + segment2Length;
        int totalBytesNeeded = MemoryLayout.AlignTo8Bytes(headerSize + totalPayloadLength);

        long currentTail = _tail.Value;
        long cachedHead = _cachedHead.Value;

        if (currentTail + totalBytesNeeded - cachedHead > _bufferSize)
        {
            cachedHead = Volatile.Read(ref _head.Value);
            _cachedHead.Value = cachedHead;

            if (currentTail + totalBytesNeeded - cachedHead > _bufferSize)
                return false;
        }

        byte* writeAddress = _bufferStart + (currentTail & _bufferMask);

        var header = (SpscBlockHeader*)writeAddress;
        header->PayloadLength = totalPayloadLength;
        header->AlignedTotalSize = totalBytesNeeded;

        byte* writeCursor = writeAddress + headerSize;

        if (segment1Length > 0)
        {
            Buffer.MemoryCopy(segment1Src, writeCursor, segment1Length, segment1Length);
            writeCursor += segment1Length;
        }

        if (segment2Length > 0)
        {
            Buffer.MemoryCopy(segment2Src, writeCursor, segment2Length, segment2Length);
        }

        // Clear any abandoned reservation and issue the Store-Release commit
        _reservedTail = 0;

        // Single Store-Release commit: No CAS, no CommitSequence stamp
        Volatile.Write(ref _tail.Value, currentTail + totalBytesNeeded);
        return true;
    }

    /// <summary>
    /// Writes a Span slice directly into the ring buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(ReadOnlySpan<byte> payload)
    {
        fixed (byte* pPayload = payload)
        {
            return TryWrite(pPayload, payload.Length);
        }
    }

    /// <summary>
    /// Writes an unmanaged struct directly by reference without intermediate stackalloc or byte casts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite<T>(in T value)
        where T : unmanaged
    {
        fixed (T* pValue = &value)
        {
            return TryWrite((byte*)pValue, sizeof(T));
        }
    }

    #endregion

    #region Two-Phase In-Place Zero-Copy Write (Reservation)

    /// <summary>
    /// Reserves contiguous payload memory directly inside the mirrored buffer without committing it.
    /// Caller writes payload directly to <paramref name="payloadDestination"/>, then calls <see cref="CommitWrite"/>.
    /// If write is abandoned, simply omit calling <see cref="CommitWrite"/> (safe no-op).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReserve(
        int payloadLength,
        out byte* payloadDestination,
        out SpscWriteTicket ticket
    )
    {
        Debug.Assert(payloadLength >= 0);

        int headerSize = sizeof(SpscBlockHeader);
        int totalBytesNeeded = MemoryLayout.AlignTo8Bytes(headerSize + payloadLength);

        long currentTail = _tail.Value;
        long cachedHead = _cachedHead.Value;

        if (currentTail + totalBytesNeeded - cachedHead > _bufferSize)
        {
            cachedHead = Volatile.Read(ref _head.Value);
            _cachedHead.Value = cachedHead;

            if (currentTail + totalBytesNeeded - cachedHead > _bufferSize)
            {
                payloadDestination = null;
                ticket = default;
                return false;
            }
        }

        byte* writeAddress = _bufferStart + (currentTail & _bufferMask);

        var header = (SpscBlockHeader*)writeAddress;
        header->PayloadLength = payloadLength;
        header->AlignedTotalSize = totalBytesNeeded;

        payloadDestination = writeAddress + headerSize;
        long newTail = currentTail + totalBytesNeeded;

        // Arm the active reservation on the heap
        _reservedTail = newTail;
        ticket = new SpscWriteTicket(newTail);
        return true;
    }

    /// <summary>
    /// Publishes a previously reserved block, advancing the producer sequence counter with a single Store-Release barrier.
    /// Idempotent and safe against aborted reservations or redundant Dispose calls.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitWrite(in SpscWriteTicket ticket)
    {
        if (_reservedTail == ticket.NewTail)
        {
            _reservedTail = 0;
            Volatile.Write(ref _tail.Value, ticket.NewTail);
        }
    }

    /// <summary>
    /// Publishes a reserved block with an adjusted (smaller) payload length.
    /// Rewrites SpscBlockHeader and advances _tail by the actual aligned size.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitWriteAdjusted(
        in SpscWriteTicket ticket,
        int originalPayloadLength,
        int actualPayloadLength
    )
    {
        Debug.Assert(actualPayloadLength <= originalPayloadLength);

        if (_reservedTail == ticket.NewTail)
        {
            int headerSize = sizeof(SpscBlockHeader);
            int originalTotalNeeded = MemoryLayout.AlignTo8Bytes(
                headerSize + originalPayloadLength
            );
            int actualTotalNeeded = MemoryLayout.AlignTo8Bytes(headerSize + actualPayloadLength);

            long blockStartTail = ticket.NewTail - originalTotalNeeded;
            byte* writeAddress = _bufferStart + (blockStartTail & _bufferMask);

            var header = (SpscBlockHeader*)writeAddress;
            header->PayloadLength = actualPayloadLength;
            header->AlignedTotalSize = actualTotalNeeded;

            _reservedTail = 0;

            Volatile.Write(ref _tail.Value, blockStartTail + actualTotalNeeded);
        }
    }

    /// <summary>
    /// Disarms an active reservation, preventing any later CommitWrite from publishing the slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AbortReserve(in SpscWriteTicket ticket)
    {
        if (_reservedTail == ticket.NewTail)
        {
            _reservedTail = 0;
        }
    }

    #endregion

    #region Amortized Batch Writes (No Immediate Store-Release)

    /// <summary>
    /// Writes a block into the mirrored buffer updating a caller's local working tail register.
    /// Does NOT issue a volatile memory barrier to <see cref="_tail"/>. Must be committed via <see cref="CommitBatch"/>.
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
        int headerSize = sizeof(SpscBlockHeader);
        int totalPayloadLength = segment1Length + segment2Length;
        int totalBytesNeeded = MemoryLayout.AlignTo8Bytes(headerSize + totalPayloadLength);

        long cachedHead = _cachedHead.Value;

        if (workingTail + totalBytesNeeded - cachedHead > _bufferSize)
        {
            cachedHead = Volatile.Read(ref _head.Value);
            _cachedHead.Value = cachedHead;

            if (workingTail + totalBytesNeeded - cachedHead > _bufferSize)
                return false;
        }

        byte* writeAddress = _bufferStart + (workingTail & _bufferMask);

        var header = (SpscBlockHeader*)writeAddress;
        header->PayloadLength = totalPayloadLength;
        header->AlignedTotalSize = totalBytesNeeded;

        byte* writeCursor = writeAddress + headerSize;

        if (segment1Length > 0)
        {
            Buffer.MemoryCopy(segment1Src, writeCursor, segment1Length, segment1Length);
            writeCursor += segment1Length;
        }

        if (segment2Length > 0)
        {
            Buffer.MemoryCopy(segment2Src, writeCursor, segment2Length, segment2Length);
        }

        workingTail += totalBytesNeeded;
        return true;
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
        int headerSize = sizeof(SpscBlockHeader);
        int totalBytesNeeded = MemoryLayout.AlignTo8Bytes(headerSize + payloadLength);

        long cachedHead = _cachedHead.Value;

        if (workingTail + totalBytesNeeded - cachedHead > _bufferSize)
        {
            cachedHead = Volatile.Read(ref _head.Value);
            _cachedHead.Value = cachedHead;

            if (workingTail + totalBytesNeeded - cachedHead > _bufferSize)
            {
                payloadDestination = null;
                return false;
            }
        }

        byte* writeAddress = _bufferStart + (workingTail & _bufferMask);

        var header = (SpscBlockHeader*)writeAddress;
        header->PayloadLength = payloadLength;
        header->AlignedTotalSize = totalBytesNeeded;

        payloadDestination = writeAddress + headerSize;
        workingTail += totalBytesNeeded;
        return true;
    }

    /// <summary>
    /// Publishes the accumulated batch tail with a single Store-Release memory barrier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitBatch(long newTail)
    {
        Volatile.Write(ref _tail.Value, newTail);
    }

    /// <summary>
    /// Gets the current producer tail for initializing a batch write session.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long GetProducerTail() => _tail.Value;

    #endregion

    #region Single Read & Peeking


    /// <summary>
    /// Peeks at the active block at the head of the ring buffer without consuming it.
    /// Must be paired with <see cref="AdvanceConsumer(in BlockView)"/> or <see cref="CommitRead(long)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out BlockView view)
    {
        view = default;

        long currentHead = _head.Value;
        long cachedTail = _cachedTail.Value;

        if (currentHead >= cachedTail)
        {
            cachedTail = Volatile.Read(ref _tail.Value);
            _cachedTail.Value = cachedTail;

            if (currentHead >= cachedTail)
                return false;
        }

        byte* readAddress = _bufferStart + (currentHead & _bufferMask);
        var header = (SpscBlockHeader*)readAddress;

        view = new BlockView(
            payload: readAddress + sizeof(SpscBlockHeader),
            length: header->PayloadLength,
            alignedTotalSize: header->AlignedTotalSize
        );

        return true;
    }

    /// <summary>
    /// Advances the consumer head counter past the given <see cref="BlockView"/>, releasing the memory back to the producer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceConsumer(in BlockView view)
    {
        Debug.Assert(view.AlignedTotalSize > 0);
        Volatile.Write(ref _head.Value, _head.Value + view.AlignedTotalSize);
    }

    /// <summary>
    /// Advances the consumer head counter directly by the specified byte count with a Store-Release barrier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead(long bytesToAdvance)
    {
        Debug.Assert(bytesToAdvance > 0);
        Volatile.Write(ref _head.Value, _head.Value + bytesToAdvance);
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
        long currentHead = _head.Value;
        long cachedTail = _cachedTail.Value;

        if (currentHead >= cachedTail)
        {
            cachedTail = Volatile.Read(ref _tail.Value);
            _cachedTail.Value = cachedTail;

            if (currentHead >= cachedTail)
                return 0;
        }

        int readCount = 0;
        long localCursor = currentHead;

        while (readCount < maxBatchSize && localCursor < cachedTail)
        {
            byte* readAddress = _bufferStart + (localCursor & _bufferMask);
            var header = (SpscBlockHeader*)readAddress;

            byte* payload = readAddress + sizeof(SpscBlockHeader);
            int length = header->PayloadLength;
            int alignedSize = header->AlignedTotalSize;

            if (!consumer.Consume(payload, length))
                break;

            localCursor += alignedSize;
            readCount++;
        }

        if (readCount > 0)
        {
            Volatile.Write(ref _head.Value, localCursor);
        }

        return readCount;
    }

    /// <summary>
    /// Opens an RAII batch reader scope that iterates up to <paramref name="maxBatchSize"/> frames
    /// with zero allocations and commits the total advanced bytes in a single Store-Release barrier upon disposal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BatchReader BeginBatch(int maxBatchSize = 32)
    {
        long currentHead = _head.Value;
        long cachedTail = _cachedTail.Value;

        if (currentHead >= cachedTail)
        {
            cachedTail = Volatile.Read(ref _tail.Value);
            _cachedTail.Value = cachedTail;
        }

        fixed (NativeMirroredSpscRing* ptr = &this)
        {
            return new BatchReader(ptr, currentHead, cachedTail, maxBatchSize);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BatchReader BeginBatch(NativeMirroredSpscRing* ring, int maxBatchSize = 32)
    {
        long currentHead = ring->_head.Value;
        long cachedTail = ring->_cachedTail.Value;

        if (currentHead >= cachedTail)
        {
            cachedTail = Volatile.Read(ref ring->_tail.Value);
            ring->_cachedTail.Value = cachedTail;
        }

        return new BatchReader(ring, currentHead, cachedTail, maxBatchSize);
    }

    /// <summary>
    /// Stack-allocated RAII batch reader for iterating variable-length frames with a single amortized commit.
    /// </summary>
    public ref struct BatchReader
    {
        private readonly NativeMirroredSpscRing* _ring;
        private readonly long _currentTail;
        private readonly int _maxBatchSize;
        private long _localHead;
        private int _readCount;
        private bool _isAborted;

        public readonly int ReadCount => _readCount;
        public readonly long LocalHead => _localHead;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BatchReader(
            NativeMirroredSpscRing* ring,
            long currentHead,
            long currentTail,
            int maxBatchSize
        )
        {
            _ring = ring;
            _currentTail = currentTail;
            _maxBatchSize = maxBatchSize;
            _localHead = currentHead;
            _readCount = 0;
            _isAborted = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadNext(out BlockView view)
        {
            if (_readCount >= _maxBatchSize || _localHead >= _currentTail)
            {
                view = default;
                return false;
            }

            byte* readAddress = _ring->_bufferStart + (_localHead & _ring->_bufferMask);
            var header = (SpscBlockHeader*)readAddress;

            view = new BlockView(
                payload: readAddress + sizeof(SpscBlockHeader),
                length: header->PayloadLength,
                alignedTotalSize: header->AlignedTotalSize
            );

            _localHead += header->AlignedTotalSize;
            _readCount++;
            return true;
        }

        /// <summary>
        /// Prevents the reader from committing the read position on disposal (turns the session into a Peek pass).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Abort()
        {
            _isAborted = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Dispose()
        {
            if (!_isAborted && _readCount > 0)
            {
                Volatile.Write(ref _ring->_head.Value, _localHead);
            }
        }
    }

    #endregion

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte* DangerousGetBufferStart() => _bufferStart;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long DangerousGetBufferMask() => _bufferMask;
}
