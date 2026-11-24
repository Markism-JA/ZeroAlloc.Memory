using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Layout;

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct VirtualPageHeader
{
    public unsafe byte* DataStart;
    public int AllocateOffset;
    public int CommittedOffset;
    public int TotalSizeInBytes;
}

/// <summary>
/// Represents a high-performance, demand-committed virtual memory page backed by a single 8-byte pointer to an unmanaged header.
/// Designed primarily for single-threaded, thread-local worker scopes or job system scratchpads.
/// <para><b>Design Decisions and Architecture:</b></para>
/// <list type="bullet">
///   <item>
///     <description><b>Single-Threaded:</b> Purposely omitted multithreaded atomic CAS locking overhead to maximize
///     hot-path cache coherence and performance. For multithreaded access, use a thread-local workspace.</description>
///   </item>
///   <item>
///     <description><b>Stateless Struct Handle:</b> Designed as an 8-byte value-type fat pointer pointing to a 64-byte aligned <see cref="VirtualPageHeader"/>.
///     All state resides in native memory, yielding zero managed GC overhead.</description>
///   </item>
///   <item>
///     <description><b>Demand-Paged Virtual Allocation:</b> Reserves massive contiguous virtual address ranges upfront without consuming physical RAM.
///     Physically commits OS page frames lazily on-demand only when allocation thresholds cross uncommitted boundaries.</description>
///   </item>
///   <item>
///     <description><b>OS Reclaim Support:</b> Features native decommit controls (<see cref="PassiveDecommitReclaim"/>) allowing simulation kernels
///     to return physical RAM back to the operating system during low-activity windows.</description>
///   </item>
/// </list>
/// </summary>
public unsafe struct UnmanagedVirtualPage : IUnmanagedPage
{
    private VirtualPageHeader* _header;

    /// <summary>
    /// Gets the total reserved virtual address space capacity in bytes.
    /// </summary>
    public readonly int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _header != null ? _header->TotalSizeInBytes : 0;
    }

    /// <summary>
    /// Gets the number of bytes currently carved from this virtual page.
    /// </summary>
    public readonly int OccupiedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _header != null ? _header->AllocateOffset : 0;
    }

    /// <summary>
    /// Gets the total number of physical OS bytes currently committed to physical RAM.
    /// </summary>
    public readonly int CommittedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _header != null ? _header->CommittedOffset : 0;
    }

    /// <summary>
    /// Reserves a virtual address range of at least <paramref name="byteCapacity"/> bytes (rounded up to the OS page size).
    /// </summary>
    /// <param name="byteCapacity">The minimum virtual capacity in bytes. Must be strictly greater than zero.</param>
    public UnmanagedVirtualPage(int byteCapacity)
    {
        if (byteCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(byteCapacity),
                byteCapacity,
                "Capacity must be strictly greater than zero."
            );

        int pageSizeMask = PageAllocator.SystemPageSize - 1;
        int totalCapacity = (byteCapacity + pageSizeMask) & ~pageSizeMask;

        _header = (VirtualPageHeader*)HeapAllocator.Allocate(sizeof(VirtualPageHeader), 64);

        if (_header == null)
            throw new OutOfMemoryException(
                "[Kernel Fault] Failed to allocate VirtualPageHeader metadata."
            );

        _header->DataStart = (byte*)PageAllocator.ReserveAddressSpace(totalCapacity);

        if (_header->DataStart == null)
        {
            HeapAllocator.Free(_header);
            _header = null;
            throw new OutOfMemoryException(
                "[Kernel Fault] Master virtual memory address space reservation failed."
            );
        }

        _header->TotalSizeInBytes = totalCapacity;
        _header->AllocateOffset = 0;
        _header->CommittedOffset = 0;
    }

    /// <summary>
    /// Slices memory out of reserved virtual address space. Automatically demand-commits physical OS page frames
    /// when the allocation cursor crosses the currently committed boundary.
    /// </summary>
    /// <param name="bytesNeeded">The number of bytes required for the allocation. Must be non-negative.</param>
    /// <param name="alignmentRequirements">The memory alignment requirement in bytes. Must be a power of 2 (default is 64).</param>
    /// <returns>A raw pointer pointing to the start of the carved memory segment.</returns>
    /// <exception cref="OutOfMemoryException">Thrown if virtual address space is exhausted or OS page commitment fails.</exception>
    /// <remarks>
    /// <para>
    /// <b>Performance &amp; Safety Note:</b> Preconditions (alignment, non-negative length, and disposal state) are checked via assertions in <c>DEBUG</c> builds only.
    /// Runtime exception checks for parameters are omitted in <c>RELEASE</c> builds for zero-overhead performance.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void* Carve(int bytesNeeded, int alignmentRequirements = 64)
    {
        Debug.Assert(
            _header != null,
            "Cannot carve from an uninitialized or disposed virtual page."
        );
        Debug.Assert(bytesNeeded >= 0, "Bytes needed cannot be negative.");
        Debug.Assert(
            (alignmentRequirements & (alignmentRequirements - 1)) == 0 && alignmentRequirements > 0,
            "Alignment must be a positive power of 2."
        );

        int alignmentMask = alignmentRequirements - 1;
        int currentPosition = _header->AllocateOffset;
        int alignedOffset = (currentPosition + alignmentMask) & ~alignmentMask;
        int nextAllocatedOffset = alignedOffset + bytesNeeded;

        if (nextAllocatedOffset > _header->TotalSizeInBytes)
            throw new OutOfMemoryException(
                "[Memory Boundary Breached] Virtual address window capacity exhausted."
            );

        // Demand-commit physical page frames from OS kernel when allocation exceeds committed range
        if (nextAllocatedOffset > _header->CommittedOffset)
        {
            int pageSizeMask = PageAllocator.SystemPageSize - 1;
            int bytesToCommit =
                (nextAllocatedOffset - _header->CommittedOffset + pageSizeMask) & ~pageSizeMask;

            if (
                !PageAllocator.CommitVirtualRange(
                    _header->DataStart + _header->CommittedOffset,
                    bytesToCommit
                )
            )
                throw new OutOfMemoryException(
                    "[Kernel Fault] Failed to physically commit page frame range."
                );

            _header->CommittedOffset += bytesToCommit;
        }

        _header->AllocateOffset = nextAllocatedOffset;

        // Data starts directly at DataStart + alignedOffset (offset 0 for first carve)
        return _header->DataStart + alignedOffset;
    }

    /// <summary>
    /// Performs a fast, non-throwing carve for single-threaded execution.
    /// Returns <c>false</c> without throwing if capacity is exceeded, page is disposed, or OS commit fails.
    /// </summary>
    /// <param name="bytesNeeded">The number of bytes required for the allocation. Must be non-negative.</param>
    /// <param name="resultPtr">When this method returns, contains the allocated pointer if successful, or <c>null</c> if failed.</param>
    /// <param name="alignmentRequirements">The memory alignment requirement in bytes. Must be a power of 2 (default is 64).</param>
    /// <returns><c>true</c> if the memory was successfully carved; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Performance &amp; Safety Note:</b> Preconditions are enforced via assertions in <c>DEBUG</c> builds only.
    /// Runtime exception checks are omitted in <c>RELEASE</c> builds for zero-overhead performance.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryCarve(int bytesNeeded, out void* resultPtr, int alignmentRequirements = 64)
    {
        resultPtr = null;
        if (_header == null)
            return false;

        Debug.Assert(bytesNeeded >= 0, "Bytes needed cannot be negative.");
        Debug.Assert(
            (alignmentRequirements & (alignmentRequirements - 1)) == 0 && alignmentRequirements > 0,
            "Alignment must be a positive power of 2."
        );

        int alignmentMask = alignmentRequirements - 1;
        int currentPosition = _header->AllocateOffset;
        int alignedOffset = (currentPosition + alignmentMask) & ~alignmentMask;
        int nextAllocatedOffset = alignedOffset + bytesNeeded;

        if (nextAllocatedOffset > _header->TotalSizeInBytes)
            return false;

        // Demand-commit physical page frames from OS kernel when allocation exceeds committed range
        if (nextAllocatedOffset > _header->CommittedOffset)
        {
            int pageSizeMask = PageAllocator.SystemPageSize - 1;
            int bytesToCommit =
                (nextAllocatedOffset - _header->CommittedOffset + pageSizeMask) & ~pageSizeMask;

            if (
                !PageAllocator.CommitVirtualRange(
                    _header->DataStart + _header->CommittedOffset,
                    bytesToCommit
                )
            )
                return false;

            _header->CommittedOffset += bytesToCommit;
        }

        _header->AllocateOffset = nextAllocatedOffset;
        resultPtr = _header->DataStart + alignedOffset;
        return true;
    }

    /// <summary>
    /// Carves memory and zero-initializes the allocated segment. Essential for state grids and frame buffers
    /// to prevent ghost data retention between reset passes.
    /// </summary>
    /// <param name="bytesNeeded">The number of bytes required. Must be non-negative.</param>
    /// <param name="alignmentRequirements">The memory alignment requirement in bytes (default is 64).</param>
    /// <returns>A pointer to the zero-initialized memory block.</returns>
    /// <exception cref="OutOfMemoryException">Thrown if virtual address space is exhausted or OS page commitment fails.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void* CarveZeroed(int bytesNeeded, int alignmentRequirements = 64)
    {
        void* ptr = Carve(bytesNeeded, alignmentRequirements);
        NativeMemoryBufferUtility.ZeroMemory(ptr, bytesNeeded);
        return ptr;
    }

    /// <summary>
    /// Resets the allocation offset back to zero, recycling the virtual window for a new scratchpad pass
    /// without releasing physical OS commitments.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        if (_header != null)
            _header->AllocateOffset = 0;
    }

    /// <summary>
    /// Reclaims unused physical RAM sheets back to the OS.
    /// Safely preserves active resident data by only decommitting trailing whole pages
    /// beyond the current allocation watermark (<see cref="OccupiedBytes"/>).
    /// </summary>
    public void PassiveDecommitReclaim()
    {
        if (_header == null || _header->CommittedOffset == 0)
            return;

        int pageSize = PageAllocator.SystemPageSize;
        int pageSizeMask = pageSize - 1;

        // Align allocation watermark UP to the nearest system page boundary.
        // Everything before this page contains active resident aggregate entities.
        int activePagesEndOffset = (_header->AllocateOffset + pageSizeMask) & ~pageSizeMask;

        int trailingBytesToDecommit = _header->CommittedOffset - activePagesEndOffset;

        if (trailingBytesToDecommit >= pageSize)
        {
            byte* decommitAddress = _header->DataStart + activePagesEndOffset;
            PageAllocator.Decommit(decommitAddress, trailingBytesToDecommit);
            _header->CommittedOffset = activePagesEndOffset;
        }
    }

    /// <summary>
    /// Full purge: Decommits all physical pages and resets both cursors to 0.
    /// Only safe to use when all resident state has been completely evacuated or torn down.
    /// </summary>
    public void ResetAndPurgeAllPhysicalPages()
    {
        if (_header == null || _header->CommittedOffset == 0)
            return;

        PageAllocator.Decommit(_header->DataStart, _header->CommittedOffset);
        _header->CommittedOffset = 0;
        _header->AllocateOffset = 0;
    }

    /// <summary>
    /// Releases all reserved virtual address space back to the kernel, frees physical commitments,
    /// and destroys the internal header metadata.
    /// </summary>
    public void Dispose()
    {
        if (_header == null)
            return;

        if (_header->DataStart != null)
        {
            PageAllocator.ReleaseVirtual(_header->DataStart, _header->TotalSizeInBytes);
            _header->DataStart = null;
        }

        HeapAllocator.Free(_header);
        _header = null;
    }
}
