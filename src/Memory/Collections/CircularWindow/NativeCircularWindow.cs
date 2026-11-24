using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Collections.CircularWindow;

/// <summary>
/// Pure unmanaged implementation of a high-performance, cache-line aligned circular rewind window.
/// Retains the latest N items, overwriting the oldest entries when capacity is saturated.
/// Protects concurrent readers against writer wrap-around tearing via a per-slot seqlock protocol without locks.
/// </summary>
/// <typeparam name="T">The unmanaged element type stored in the rewind window.</typeparam>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct NativeCircularWindow<T>
    where T : unmanaged
{
    /// <summary>Maximum allowed capacity in slots (1 billion).</summary>
    public const ulong MaxAllowedCapacity = 0x40000000UL; // 1 Billion slots
    private const int _cacheLineAlignment = 64;

    private readonly int _capacity;
    private readonly int _mask;
    private readonly CircularWindowSlot<T>* _slots;

    // Cache-line isolated write cursor embedded directly in the native control block
    private PaddedSequence _writeCursor;

    #region State & Metrics

    /// <summary>
    /// Gets the maximum item capacity of the window.
    /// </summary>
    public readonly int Capacity => _capacity;

    /// <summary>
    /// Gets the bitmask used for fast power-of-two slot indexing.
    /// </summary>
    public readonly int Mask => _mask;

    /// <summary>
    /// Gets the raw unmanaged pointer to the slot array.
    /// </summary>
    public readonly CircularWindowSlot<T>* SlotsPtr => _slots;

    /// <summary>
    /// Gets the current monotonic writing cursor.
    /// </summary>
    public long WriteCursor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _writeCursor.Value);
    }

    /// <summary>
    /// Gets an unmanaged pointer to the writing cursor counter.
    /// Stable across GC compacting collections.
    /// </summary>
    public readonly long* CursorPtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            fixed (long* ptr = &_writeCursor.Value)
            {
                return ptr;
            }
        }
    }

    #endregion


    #region Allocation & Teardown

    /// <summary>
    /// Allocates an unmanaged <see cref="NativeCircularWindow{T}"/> and its backing slot buffer on the native heap.
    /// Control block and slot buffers are aligned to 64-byte cache-line boundaries.
    /// </summary>
    public static NativeCircularWindow<T>* Allocate(int capacity = 16384)
    {
        if (capacity < 64 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of two >= 64.", nameof(capacity));
        }

        if ((ulong)capacity > MaxAllowedCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                $"Capacity exceeds structural limit of {MaxAllowedCapacity} elements."
            );
        }

        // Guard against 32-bit signed integer overflow when multiplying capacity by slot stride
        long totalBytesNeeded = (long)capacity * sizeof(CircularWindowSlot<T>);
        if (totalBytesNeeded > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                $"Requested capacity requires {totalBytesNeeded:N0} bytes, which exceeds the allocator limit of {int.MaxValue:N0} bytes."
            );
        }

        int slotsByteSize = (int)totalBytesNeeded;
        var slots = (CircularWindowSlot<T>*)
            HeapAllocator.Allocate(slotsByteSize, _cacheLineAlignment);
        if (slots == null)
        {
            throw new OutOfMemoryException(
                $"[Native Crash] Failed to allocate native slot buffer for {nameof(NativeCircularWindow<T>)}."
            );
        }

        NativeMemoryBufferUtility.ZeroMemory(slots, slotsByteSize);

        for (int i = 0; i < capacity; i++)
        {
            slots[i].Sequence = -1L;
        }

        var window = (NativeCircularWindow<T>*)
            HeapAllocator.Allocate(sizeof(NativeCircularWindow<T>), _cacheLineAlignment);

        if (window == null)
        {
            HeapAllocator.Free(slots);
            throw new OutOfMemoryException(
                $"[Native Crash] Failed to allocate control block for {nameof(NativeCircularWindow<T>)}."
            );
        }

        *window = new NativeCircularWindow<T>(capacity, slots);
        return window;
    }

    private NativeCircularWindow(int capacity, CircularWindowSlot<T>* slots)
    {
        _capacity = capacity;
        _mask = capacity - 1;
        _slots = slots;
        _writeCursor.Value = 0;
    }

    /// <summary>
    /// Frees the unmanaged backing buffer and the control block back to the native heap.
    /// </summary>
    public static void Free(NativeCircularWindow<T>* window)
    {
        if (window == null)
            return;

        if (window->_slots != null)
        {
            HeapAllocator.Free(window->_slots);
        }

        HeapAllocator.Free(window);
    }

    #endregion

    #region Append Operations (Lossy Overwrite)

    /// <summary>
    /// Appends a single item into the circular window, overwriting the oldest entry if saturated.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(in T item)
    {
        fixed (T* ptr = &item)
        {
            AppendBatch(ptr, 1);
        }
    }

    /// <summary>
    /// Appends a contiguous batch of items into the circular window using the per-slot seqlock protocol.
    /// </summary>
    /// <param name="items">Pointer to the start of the source item array.</param>
    /// <param name="count">Number of items to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendBatch(T* items, int count)
    {
        if (count <= 0)
            return;

        long cursor = _writeCursor.Value;

        for (int i = 0; i < count; i++)
        {
            long currentSeq = cursor + i;
            CircularWindowSlot<T>* slot = _slots + (currentSeq & _mask);

            // 1. Invalidate slot: mark dirty to notify concurrent readers
            slot->Sequence = -1L;
            Thread.MemoryBarrier();

            // 2. Blit payload
            slot->Value = items[i];

            // 3. Publish valid monotonic ticket with store-release semantics
            Volatile.Write(ref slot->Sequence, currentSeq);
        }

        // Commit global write cursor
        Volatile.Write(ref _writeCursor.Value, cursor + count);
    }

    #endregion

    #region Static Raw IPC Helpers

    /// <summary>
    /// Writes a contiguous buffer of items directly into raw circular window slots,
    /// publishing monotonic sequence stamps with Store-Release barriers for lock-free IPC readers.
    /// </summary>
    /// <param name="slots">Pointer to the start of the CircularWindowSlot array.</param>
    /// <param name="writeCursorPtr">Pointer to the 64-bit monotonic write cursor header.</param>
    /// <param name="mask">Power-of-two capacity mask (Capacity - 1).</param>
    /// <param name="items">Pointer to the source items to copy.</param>
    /// <param name="count">Number of items to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AppendRaw(
        CircularWindowSlot<T>* slots,
        long* writeCursorPtr,
        int mask,
        T* items,
        int count
    )
    {
        if (count <= 0 || items == null || slots == null || writeCursorPtr == null)
            return;

        long currentCursor = *writeCursorPtr;

        for (int i = 0; i < count; i++)
        {
            long seq = currentCursor + i;
            int slotIdx = (int)(seq & mask);

            ref var slot = ref slots[slotIdx];
            slot.Value = items[i];

            // Store-Release barrier: ensures slot.Value is retired to cache
            // BEFORE slot.Sequence becomes visible to EventStreamReader
            Volatile.Write(ref slot.Sequence, seq);
        }

        // Advance partition write head
        Volatile.Write(ref *writeCursorPtr, currentCursor + count);
    }

    #endregion

    #region Non-Destructive Inspection & Queries

    /// <summary>
    /// Reads up to <paramref name="maxCount"/> latest valid items chronologically into destination memory.
    /// Protects against writer wrap-around tearing without blocking the writer.
    /// </summary>
    /// <param name="dest">Pointer to the destination memory.</param>
    /// <param name="maxCount">Maximum number of items to copy.</param>
    /// <returns>Actual number of non-torn items copied.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadLatest(T* dest, int maxCount)
    {
        long cursor = Volatile.Read(ref _writeCursor.Value);
        if (cursor <= 0 || maxCount <= 0)
            return 0;

        long available = Math.Min(cursor, _capacity);
        int requested = (int)Math.Min(available, maxCount);
        long startSeq = cursor - requested;
        int readCount = 0;

        for (int i = 0; i < requested; i++)
        {
            long expectedSeq = startSeq + i;
            CircularWindowSlot<T>* slot = _slots + (expectedSeq & _mask);

            // Seqlock check #1: Pre-read ticket validation
            long seq1 = Volatile.Read(ref slot->Sequence);
            if (seq1 != expectedSeq)
                continue;

            T val = slot->Value;
            Thread.MemoryBarrier();

            // Seqlock check #2: Post-read verification (ensures writer didn't stomp slot mid-copy)
            if (Volatile.Read(ref slot->Sequence) == seq1)
            {
                dest[readCount++] = val;
            }
        }

        return readCount;
    }

    /// <summary>
    /// Scans backwards from newest to oldest elements, applying an unmanaged C-ABI predicate filter.
    /// Reverses matching results in-place so destination memory is ordered chronologically.
    /// </summary>
    /// <param name="dest">Pointer to the destination memory.</param>
    /// <param name="maxCount">Maximum number of matching items to return.</param>
    /// <param name="filterContext">Opaque context pointer passed directly to the predicate.</param>
    /// <param name="predicate">
    /// Function pointer taking (filterContext, itemPtr) and returning:
    ///  1 = Match (include in output),
    ///  0 = Mismatch (skip item),
    /// -1 = Terminal Cutoff (halt backwards traversal early, e.g., target timestamp reached).
    /// </param>
    /// <returns>Actual count of matching items copied into destination.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int QueryFiltered(
        T* dest,
        int maxCount,
        void* filterContext,
        delegate* <void*, T*, int> predicate
    )
    {
        long cursor = Volatile.Read(ref _writeCursor.Value);
        if (cursor <= 0 || maxCount <= 0)
            return 0;

        long available = Math.Min(cursor, _capacity);
        int matchedCount = 0;

        for (long i = 0; i < available && matchedCount < maxCount; i++)
        {
            long targetSeq = cursor - 1 - i;
            CircularWindowSlot<T>* slot = _slots + (targetSeq & _mask);

            long seq1 = Volatile.Read(ref slot->Sequence);
            if (seq1 != targetSeq)
                continue;

            T item = slot->Value;
            Thread.MemoryBarrier();

            if (Volatile.Read(ref slot->Sequence) != seq1)
                continue;

            int status = predicate(filterContext, &item);
            if (status < 0)
            {
                // Terminal cutoff: stop searching further into history
                break;
            }

            if (status == 1)
            {
                dest[matchedCount++] = item;
            }
        }

        // Reverse matched items to restore chronological ordering
        for (int left = 0, right = matchedCount - 1; left < right; left++, right--)
        {
            (dest[right], dest[left]) = (dest[left], dest[right]);
        }

        return matchedCount;
    }

    #endregion
}
