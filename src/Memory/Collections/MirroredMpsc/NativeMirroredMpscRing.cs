using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Collections.MirroredMpsc;

/// <summary>
/// Unmanaged implementation of a lock-free, zero-allocation Multi-Producer Single-Consumer (MPSC)
/// circular ring buffer backed by mirrored virtual memory addresses.
/// </summary>
public unsafe struct NativeMirroredMpscRing
{
    private const ulong _windowsAllocationGranularity = 64 * 1024; // 64 KB hardware boundary
    public const ulong MaxAllowedCapacity = 0x40000000UL; // 1 GB limit

    private readonly long _bufferSize;
    private readonly long _bufferMask;
    private readonly byte* _bufferStart;
    private readonly void* _nativeHandle;

    private PaddedSequence _head;
    private PaddedSequence _tail;
    private PaddedSequence _cachedTail;

    #region State & Metrics

    /// <summary>
    /// Gets the total byte capacity of the ring buffer.
    /// </summary>
    public readonly long Capacity => _bufferSize;

    /// <summary>
    /// Gets the current number of unconsumed bytes in the ring buffer.
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
    /// Lock-free check if the ring buffer currently contains no unconsumed or in-flight data.
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
    /// Allocates a new instance of <see cref="NativeMirroredMpscRing"/> and maps a mirrored virtual memory buffer.
    /// The control block is 64-byte cache-line aligned; the mirrored buffer is aligned to OS allocation granularity.
    /// </summary>
    public static NativeMirroredMpscRing* Allocate(long byteCapacity = 2 * 1024 * 1024)
    {
        if (byteCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCapacity),
                "Capacity must be positive."
            );
        }

        ulong scaledCapacity = BitOperation.RoundUpToPowerOf2((ulong)byteCapacity);
        ulong minAlignment = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? _windowsAllocationGranularity
            : (ulong)PageAllocator.SystemPageSize;

        if (scaledCapacity < minAlignment)
        {
            scaledCapacity = minAlignment;
        }

        if (scaledCapacity > MaxAllowedCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCapacity),
                $"Capacity exceeds system limit of {MaxAllowedCapacity} bytes."
            );
        }

        byte* bufferStart = PageAllocator.CreateMirroredBuffer(
            (int)scaledCapacity,
            out void* nativeHandle
        );

        if (bufferStart == null)
        {
            throw new OutOfMemoryException(
                "[Kernel Fault] Virtual memory mirror map assignment failed."
            );
        }

        var ring = (NativeMirroredMpscRing*)
            HeapAllocator.Allocate(sizeof(NativeMirroredMpscRing), 64);

        if (ring == null)
        {
            PageAllocator.FreeMirroredBuffer(bufferStart, (long)scaledCapacity, nativeHandle);
            throw new OutOfMemoryException(
                "[Memory Crash] Failed to allocate control block for NativeMirroredMpscRing."
            );
        }

        *ring = new NativeMirroredMpscRing((long)scaledCapacity, bufferStart, nativeHandle);
        return ring;
    }

    private NativeMirroredMpscRing(long bufferSize, byte* bufferStart, void* nativeHandle)
    {
        _bufferSize = bufferSize;
        _bufferMask = bufferSize - 1;
        _bufferStart = bufferStart;
        _nativeHandle = nativeHandle;

        // Sequence starts at 1 so uninitialized zeroed memory (0) on newly mapped pages
        // never matches a valid commit sequence.
        _head.Value = 1;
        _tail.Value = 1;
        _cachedTail.Value = 1;
    }

    public static void Free(NativeMirroredMpscRing* ring)
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

    #endregion

    #region Multi-Producer Concurrent Writes

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

        int headerSize = sizeof(BlockHeader);
        int totalPayloadLength = segment1Length + segment2Length;
        int totalBytesNeeded = MemoryLayout.AlignTo8Bytes(headerSize + totalPayloadLength);

        long currentTail;
        long currentHead;

        // Multi-producer reservation loop
        do
        {
            currentTail = Volatile.Read(ref _tail.Value);
            currentHead = Volatile.Read(ref _head.Value);

            if (currentTail + totalBytesNeeded - currentHead > _bufferSize)
                return false;
        } while (
            Interlocked.CompareExchange(
                ref _tail.Value,
                currentTail + totalBytesNeeded,
                currentTail
            ) != currentTail
        );

        byte* writeAddress = _bufferStart + (currentTail & _bufferMask);
        var header = (BlockHeader*)writeAddress;
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

        // Release store: guarantees payload stores commit to cache before CommitSequence is visible to consumer
        Volatile.Write(ref header->CommitSequence, currentTail);
        return true;
    }

    /// <summary>
    /// Writes a Span slice concurrently into the MPSC ring buffer.
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
    /// Writes an unmanaged struct directly by reference without intermediate allocations.
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

    #region


    /// <summary>
    /// Peeks at the active block at the head of the ring buffer without consuming it.
    /// Returns false if the buffer is empty or if the producer is currently copying payload data.
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
        var header = (BlockHeader*)readAddress;

        // Sequence equality check: in-flight producer has not committed this block yet
        if (Volatile.Read(ref header->CommitSequence) != currentHead)
            return false;

        view = new BlockView(
            payload: readAddress + sizeof(BlockHeader),
            length: header->PayloadLength,
            alignedTotalSize: header->AlignedTotalSize
        );

        return true;
    }

    /// <summary>
    /// Advances the consumer head counter past the given <see cref="BlockView"/>, releasing space to producers.
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
    /// Drains up to maxBatchSize committed blocks directly into an inlined struct consumer with zero virtual dispatch.
    /// Advances the head sequence exactly once per batch.
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
            var header = (BlockHeader*)readAddress;

            // Stop at the first uncommitted in-flight slot
            if (Volatile.Read(ref header->CommitSequence) != localCursor)
                break;

            byte* payload = readAddress + sizeof(BlockHeader);
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
    /// Opens an RAII batch reader scope that iterates up to <paramref name="maxBatchSize"/> committed frames
    /// and issues a single Store-Release barrier to the head sequence upon disposal.
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

        fixed (NativeMirroredMpscRing* ptr = &this)
        {
            return new BatchReader(ptr, currentHead, cachedTail, maxBatchSize);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BatchReader BeginBatch(NativeMirroredMpscRing* ring, int maxBatchSize = 32)
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

    public ref struct BatchReader
    {
        private readonly NativeMirroredMpscRing* _ring;
        private readonly long _currentTail;
        private readonly int _maxBatchSize;
        private long _localHead;
        private int _readCount;
        private bool _isAborted;

        public readonly int ReadCount => _readCount;
        public readonly long LocalHead => _localHead;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BatchReader(
            NativeMirroredMpscRing* ring,
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
            var header = (BlockHeader*)readAddress;

            if (Volatile.Read(ref header->CommitSequence) != _localHead)
            {
                view = default;
                return false;
            }

            view = new BlockView(
                payload: readAddress + sizeof(BlockHeader),
                length: header->PayloadLength,
                alignedTotalSize: header->AlignedTotalSize
            );

            _localHead += header->AlignedTotalSize;
            _readCount++;
            return true;
        }

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
