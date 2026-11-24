using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Collections.BoundedMpsc;

/// <summary>
/// Unmanaged implementation of a high-throughput, bounded Multi-Producer Single-Consumer (MPSC)
/// circular queue based on Dmitry Vyukov's lock-free sequence array algorithm.
/// Optimized for uniform, fixed-size unmanaged structures with zero framing headers and no OS virtual memory mapping.
/// </summary>
/// <typeparam name="T">The unmanaged payload type (e.g., EventEnvelope).</typeparam>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct NativeBoundedMpscRing<T>
    where T : unmanaged
{
    /// <summary>Maximum allowed capacity in slots (1 billion).</summary>
    public const ulong MaxAllowedCapacity = 0x40000000UL; // 1 Billion slots

    private readonly long _capacity;
    private readonly long _mask;
    private readonly BoundedMpscSlot<T>* _slots;

    private PaddedSequence _head;
    private PaddedSequence _tail;

    #region State & Metrics

    /// <summary>
    /// Gets the total slot capacity of the queue.
    /// </summary>
    public readonly long Capacity => _capacity;

    /// <summary>
    /// Gets the approximate number of occupied, unconsumed slots in the queue.
    /// </summary>
    public long OccupiedCount
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
    /// Gets the remaining number of free slots available before backpressure occurs.
    /// </summary>
    public long FreeCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long free = _capacity - OccupiedCount;
            return free < 0 ? 0 : free;
        }
    }

    /// <summary>
    /// Lock-free check if the queue currently contains no unconsumed or in-flight data.
    /// </summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _head.Value) >= Volatile.Read(ref _tail.Value);
    }

    /// <summary>
    /// Gets the current consumer head counter.
    /// </summary>
    public long HeadSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _head.Value);
    }

    /// <summary>
    /// Gets the current producer tail counter.
    /// </summary>
    public long TailSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _tail.Value);
    }

    #endregion

    #region Allocation & Teardown

    /// <summary>
    /// Allocates an unmanaged <see cref="NativeBoundedMpscRing{T}"/> and its backing slot buffer on the native heap.
    /// Control block and slot buffers are aligned to 64-byte cache-line boundaries.
    /// </summary>
    public static NativeBoundedMpscRing<T>* Allocate(int elementCapacity = 16384)
    {
        if (elementCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementCapacity),
                "Capacity must be strictly positive."
            );
        }

        ulong scaledCapacity = BitOperation.RoundUpToPowerOf2((ulong)elementCapacity);
        if (scaledCapacity < 2)
        {
            scaledCapacity = 2;
        }

        if (scaledCapacity > MaxAllowedCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementCapacity),
                $"Capacity exceeds structural limit of {MaxAllowedCapacity} elements."
            );
        }

        // Protect against 32-bit signed integer overflow when multiplying capacity by slot stride
        long totalBytesNeeded = (long)scaledCapacity * sizeof(BoundedMpscSlot<T>);
        if (totalBytesNeeded > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementCapacity),
                $"Requested capacity requires {totalBytesNeeded:N0} bytes, which exceeds the allocator limit of {int.MaxValue:N0} bytes."
            );
        }

        int slotsByteSize = (int)totalBytesNeeded;
        var slots = (BoundedMpscSlot<T>*)HeapAllocator.Allocate(slotsByteSize, 64);
        if (slots == null)
        {
            throw new OutOfMemoryException(
                "[Native Crash] Failed to allocate native slot buffer for NativeBoundedMpscRing."
            );
        }

        // Initialize turnstile tickets: slot[i].Sequence = i
        for (long i = 0; i < (long)scaledCapacity; i++)
        {
            slots[i].Sequence = i;
        }

        var ring = (NativeBoundedMpscRing<T>*)
            HeapAllocator.Allocate(sizeof(NativeBoundedMpscRing<T>), 64);

        if (ring == null)
        {
            HeapAllocator.Free(slots);
            throw new OutOfMemoryException(
                "[Native Crash] Failed to allocate control block for NativeBoundedMpscRing."
            );
        }

        *ring = new NativeBoundedMpscRing<T>((long)scaledCapacity, slots);
        return ring;
    }

    private NativeBoundedMpscRing(long capacity, BoundedMpscSlot<T>* slots)
    {
        _capacity = capacity;
        _mask = capacity - 1;
        _slots = slots;

        _head.Value = 0;
        _tail.Value = 0;
    }

    /// <summary>
    /// Deallocates the native slot buffer and the control block on the native heap.
    /// </summary>
    public static void Free(NativeBoundedMpscRing<T>* ring)
    {
        if (ring == null)
            return;

        if (ring->_slots != null)
        {
            HeapAllocator.Free(ring->_slots);
        }

        HeapAllocator.Free(ring);
    }

    #endregion

    #region Multi-Producer Enqueue (TryWrite)

    /// <summary>
    /// Concurrently claims a slot using an atomic CAS loop and commits the value with a single Store-Release barrier.
    /// Returns false immediately if the buffer is full (backpressure).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in T item)
    {
        long currentTail = Volatile.Read(ref _tail.Value);

        while (true)
        {
            BoundedMpscSlot<T>* slot = _slots + (currentTail & _mask);
            long seq = Volatile.Read(ref slot->Sequence);
            long diff = seq - currentTail;

            if (diff == 0)
            {
                // Slot is empty for the current generation; attempt atomic claim
                if (
                    Interlocked.CompareExchange(ref _tail.Value, currentTail + 1, currentTail)
                    == currentTail
                )
                {
                    slot->Value = item;
                    // Release store: marks slot as committed to the consumer for generation (currentTail + 1)
                    Volatile.Write(ref slot->Sequence, currentTail + 1);
                    return true;
                }

                // Lost CAS race to another producer: refresh tail register and retry
                currentTail = Volatile.Read(ref _tail.Value);
            }
            else if (diff < 0)
            {
                // Slot has not yet been drained by the consumer from the previous cycle
                long actualTail = Volatile.Read(ref _tail.Value);
                if (actualTail == currentTail)
                {
                    // Tail has not moved: buffer is genuinely saturated
                    return false;
                }

                currentTail = actualTail;
            }
            else
            {
                // diff > 0: currentTail is lagging behind an advanced generation
                currentTail = Volatile.Read(ref _tail.Value);
            }
        }
    }

    #endregion

    #region Single Consumer Dequeue & Draining

    /// <summary>
    /// Dequeues a single committed value, turning the turnstile generation ticket to free the slot for the next cycle.
    /// Returns false if the queue is empty or if the producer is currently copying data into the slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out T item)
    {
        long currentHead = _head.Value;
        BoundedMpscSlot<T>* slot = _slots + (currentHead & _mask);
        long seq = Volatile.Read(ref slot->Sequence);
        long diff = seq - (currentHead + 1);

        if (diff == 0)
        {
            item = slot->Value;
            // Advance turnstile sequence by full capacity to signal producers on the next generation
            Volatile.Write(ref slot->Sequence, currentHead + _capacity);
            _head.Value = currentHead + 1;
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Drains up to <paramref name="maxBatchSize"/> committed items directly into a devirtualized struct consumer.
    /// Advances the consumer head counter with a single commit at the end of the batch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainBatch<TConsumer>(ref TConsumer consumer, int maxBatchSize = 32)
        where TConsumer : struct, IValueConsumer<T>
    {
        long currentHead = _head.Value;
        int readCount = 0;

        while (readCount < maxBatchSize)
        {
            long targetPos = currentHead + readCount;
            BoundedMpscSlot<T>* slot = _slots + (targetPos & _mask);
            long seq = Volatile.Read(ref slot->Sequence);
            long diff = seq - (targetPos + 1);

            if (diff != 0)
            {
                // Reached an uncommitted producer slot or empty queue boundary
                break;
            }

            if (!consumer.Consume(in slot->Value))
            {
                // Consumer applied backpressure: leave this slot unconsumed
                break;
            }

            // Free slot for the next capacity cycle
            Volatile.Write(ref slot->Sequence, targetPos + _capacity);
            readCount++;
        }

        if (readCount > 0)
        {
            _head.Value = currentHead + readCount;
        }

        return readCount;
    }

    /// <summary>
    /// Opens an RAII batch reader scope for iterating up to <paramref name="maxBatchSize"/> committed items.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BatchReader BeginBatch(int maxBatchSize = 32)
    {
        fixed (NativeBoundedMpscRing<T>* ptr = &this)
        {
            return new BatchReader(ptr, maxBatchSize);
        }
    }

    /// <summary>Begins a zero-allocation batch reader over up to <paramref name="maxBatchSize"/> committed items.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BatchReader BeginBatch(NativeBoundedMpscRing<T>* ring, int maxBatchSize = 32)
    {
        return new BatchReader(ring, maxBatchSize);
    }

    /// <summary>Stack-allocated ref-struct reader that amortizes consumer head publication across a batch.</summary>
    public ref struct BatchReader
    {
        private readonly NativeBoundedMpscRing<T>* _ring;
        private readonly int _maxBatchSize;
        private long _localHead;
        private int _readCount;

        /// <summary>Gets the number of items read so far in this batch.</summary>
        public readonly int ReadCount => _readCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BatchReader(NativeBoundedMpscRing<T>* ring, int maxBatchSize)
        {
            _ring = ring;
            _maxBatchSize = maxBatchSize;
            _localHead = ring->_head.Value;
            _readCount = 0;
        }

        /// <summary>Attempts to read the next committed item in the batch.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadNext(out T item)
        {
            if (_readCount >= _maxBatchSize)
            {
                item = default;
                return false;
            }

            BoundedMpscSlot<T>* slot = _ring->_slots + (_localHead & _ring->_mask);
            long seq = Volatile.Read(ref slot->Sequence);
            long diff = seq - (_localHead + 1);

            if (diff != 0)
            {
                item = default;
                return false;
            }

            item = slot->Value;
            Volatile.Write(ref slot->Sequence, _localHead + _ring->_capacity);
            _localHead++;
            _readCount++;
            return true;
        }

        /// <summary>Publishes the accumulated consumer head sequence.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Dispose()
        {
            if (_readCount > 0)
            {
                _ring->_head.Value = _localHead;
            }
        }
    }

    #endregion

    /// <summary>Returns the raw unmanaged pointer to the backing slot array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly BoundedMpscSlot<T>* DangerousGetSlots() => _slots;
}
