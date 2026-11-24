using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;

namespace Memory.Arena;

/// <summary>
/// Represents an unmanaged, thread-safe linear arena allocator operating over a contiguous block of native memory for type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The unmanaged element type stored in the arena.</typeparam>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct UnmanagedLinearArena<T>
    where T : unmanaged
{
    private byte* _bufferStart;
    private int _capacityInBytes;
    private int _allocateBytes;
    private AllocationType _allocationType;

    /// <summary>
    /// Creates an unmanaged linear arena over an existing block of native memory.
    /// </summary>
    /// <param name="memoryPointer">The base pointer to the native memory block. Must not be <c>null</c>.</param>
    /// <param name="totalBytes">The total available byte capacity of the memory block. Must be strictly greater than zero.</param>
    /// <param name="allocType">The backing allocation source type (default is <see cref="AllocationType.UserSpaceHeap"/>).</param>
    /// <returns>An initialized <see cref="UnmanagedLinearArena{T}"/> instance wrapping the target memory segment.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="totalBytes"/> is less than or equal to zero.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="memoryPointer"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="memoryPointer"/> does not meet the structural alignment requirements for type <typeparamref name="T"/>.</exception>
    public static UnmanagedLinearArena<T> CreateOverPointer(
        void* memoryPointer,
        int totalBytes,
        AllocationType allocType = AllocationType.UserSpaceHeap
    )
    {
        if (totalBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(totalBytes),
                totalBytes,
                "Total capacity in bytes must be strictly greater than zero."
            );

        if (memoryPointer == null)
            throw new ArgumentNullException(
                nameof(memoryPointer),
                "Memory pointer cannot be null."
            );

        int typeAlignment = sizeof(T) >= 8 ? 8 : 4;
        if (((long)memoryPointer & (typeAlignment - 1)) != 0)
        {
            throw new ArgumentException(
                $"[Alignment Fault] Base address 0x{(long)memoryPointer:X} is unaligned for structural elements of type {typeof(T).Name}.",
                nameof(memoryPointer)
            );
        }

        UnmanagedLinearArena<T> arena;
        arena._bufferStart = (byte*)memoryPointer;
        arena._capacityInBytes = totalBytes;
        arena._allocateBytes = 0;
        arena._allocationType = allocType;
        return arena;
    }

    /// <summary>
    /// Gets a value indicating whether this arena handle has been initialized with a non-null buffer pointer.
    /// </summary>
    public readonly bool IsCreated => _bufferStart != null;

    /// <summary>
    /// Gets the total capacity of the arena in bytes.
    /// </summary>
    public readonly int CapacityInBytes => _capacityInBytes;

    /// <summary>
    /// Gets the raw unmanaged pointer to the start of the underlying buffer.
    /// </summary>
    public readonly byte* RawBufferStart => _bufferStart;

    /// <summary>
    /// Gets the total number of bytes currently allocated from the arena.
    /// Thread-safe via atomic volatile read.
    /// </summary>
    public readonly int AllocatedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            fixed (int* ptr = &_allocateBytes)
            {
                return Volatile.Read(ref *ptr);
            }
        }
    }

    /// <summary>
    /// Gets the current utilization ratio of the arena as a floating-point value between 0.0 and 1.0.
    /// </summary>
    public readonly float UtilizationRatio =>
        _capacityInBytes > 0 ? (float)AllocatedBytes / _capacityInBytes : 0f;

    /// <summary>
    /// Leases a contiguous block of elements using a thread-safe atomic offset mutation loop.
    /// </summary>
    /// <param name="count">The number of elements of type <typeparamref name="T"/> to allocate. Must be strictly greater than zero.</param>
    /// <returns>A pointer of type <typeparamref name="T"/> pointing to the start of the leased element block.</returns>
    /// <exception cref="OutOfMemoryException">Thrown if requested allocation exceeds available arena capacity.</exception>
    /// <remarks>
    /// <para>
    /// <b>Performance &amp; Safety Note:</b> Preconditions (<paramref name="count"/> &gt; 0 and creation state) are checked via assertions in <c>DEBUG</c> builds only.
    /// Runtime parameter exception checks are omitted in <c>RELEASE</c> builds for zero-overhead performance.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T* Lease(int count)
    {
        Debug.Assert(_bufferStart != null, "Arena is uninitialized or null.");
        Debug.Assert(count > 0, "Lease count must be strictly greater than zero.");

        int bytesRequested = count * sizeof(T);

        while (true)
        {
            int currentAllocated = Volatile.Read(ref _allocateBytes);
            int nextAllocated = currentAllocated + bytesRequested;

            if (nextAllocated > _capacityInBytes)
            {
                return LogAndTriggerExhaustionState(bytesRequested);
            }

            // High-performance user-space atomic tracking gate
            if (
                Interlocked.CompareExchange(ref _allocateBytes, nextAllocated, currentAllocated)
                == currentAllocated
            )
            {
                return (T*)(_bufferStart + currentAllocated);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly T* LogAndTriggerExhaustionState(int failedBytes)
    {
#if DEBUG
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(
            $"[CRITICAL EXHAUSTION] Arena<{typeof(T).Name}> lacked space for {failedBytes} bytes! Max capacity: {_capacityInBytes}."
        );
        Console.ResetColor();
#endif
        throw new OutOfMemoryException(
            $"[Memory Crash] Space exhaustion inside unmanaged arena track for type: {typeof(T).Name}."
        );
    }

    /// <summary>
    /// Resets the allocation offset back to zero, effectively clearing the arena for reuse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Volatile.Write(ref _allocateBytes, 0);
    }

    /// <summary>
    /// Instructs the OS Kernel to reclaim any unused physical RAM sheets
    /// inside this specific arena buffer. Requires that the arena was allocated using Kernel Virtual Pages,
    /// otherwise this is a no-op.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TrimBackingMemory()
    {
        if (_bufferStart == null || _allocationType == AllocationType.UserSpaceHeap)
            return;

        int currentAllocated = Volatile.Read(ref _allocateBytes);
        byte* rawUnusedStart = _bufferStart + currentAllocated;

        ulong mask = PageAllocator.PageAlignmentMask;
        int systemPageSize = PageAllocator.SystemPageSize;

        ulong rawAddress = (ulong)rawUnusedStart;

        ulong alignedAddress = (rawAddress + mask) & ~mask;
        byte* pageAlignedStart = (byte*)alignedAddress;

        long structuralLoss = pageAlignedStart - rawUnusedStart;
        long remainingUnusedBytes = _capacityInBytes - currentAllocated - structuralLoss;

        long pageAlignedBytes = remainingUnusedBytes & ~(long)mask;

        if (pageAlignedBytes >= systemPageSize)
        {
            PageAllocator.Decommit(pageAlignedStart, (int)pageAlignedBytes);
        }
    }
}
