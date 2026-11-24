using System.Diagnostics;
using System.Runtime.CompilerServices;
using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Layout;

/// <summary>
/// Represents a lightweight, high-performance unmanaged bump allocator handle backed by a contiguous native memory page.
/// <para><b>Design Decisions and Architecture:</b></para>
/// <list type="bullet">
///   <item>
///     <description><b>Stateless Struct Handle:</b> Designed as an 8-byte value-type fat pointer wrapping native memory.
///     All allocation state (position offsets and capacity) resides directly in the shared native memory header rather than managed fields,
///     eliminating managed GC overhead and ensuring cache-friendly layouts.</description>
///   </item>
///   <item>
///     <description><b>Contiguous Bump Allocation:</b> Employs extremely fast offset-increment memory carving (<c>Carve</c>, <c>TryCarve</c>)
///     alongside lock-free atomic primitives (<c>TryCarveAtomic</c>) to support both single-threaded determinism and concurrent multithreaded workflows.</description>
///   </item>
///   <item>
///     <description><b>Fail-Fast Safety Diagnostics:</b> Integrates pre-padded cache-line alignment (64-byte boundaries) and trailing memory
///     canaries (tail guards) stripped entirely in Release builds via conditional compilation to catch buffer overruns immediately.</description>
///   </item>
/// </list>
/// </summary>
public unsafe struct UnmanagedPage : IUnmanagedPage, IEquatable<UnmanagedPage>
{
    private byte* _basePtr;
    private const uint _tailGuardMagic = 0xDEADBEEF;
    private const int _dataOffset = 64;

    /// <summary>
    /// Gets the base pointer to the start of the usable data payload area (positioned past the 64-byte aligned header).
    /// </summary>
    /// <returns>
    /// <c>null</c> if the page is uninitialized or has been disposed.
    /// </returns>
    public readonly byte* DataPointer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _basePtr != null ? _basePtr + _dataOffset : null;
    }

    /// <summary>
    /// Gets the number of bytes currently carved and allocated from this page payload area.
    /// Thread-safe via atomic volatile reads against the native memory header.
    /// </summary>
    public readonly int OccupiedBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _basePtr != null ? Volatile.Read(ref *(int*)_basePtr) : 0;
    }

    /// <summary>
    /// Gets the total usable payload capacity in bytes (excluding headers and tail guards).
    /// </summary>
    public readonly int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _basePtr != null ? *(int*)(_basePtr + 4) : 0;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnmanagedPage"/> struct, allocating a 64-byte aligned block
    /// of native memory on the C-heap with a trailing guard canary.
    /// </summary>
    /// <param name="byteCapacity">The target payload capacity in bytes. Will be padded to a 64-byte boundary.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="byteCapacity"/> is less than or equal to zero.</exception>
    /// <exception cref="OutOfMemoryException">Thrown if the underlying native allocator fails to reserve memory.</exception>
    public UnmanagedPage(int byteCapacity)
    {
        if (byteCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(byteCapacity),
                "Capacity must be greater than zero."
            );

        int totalDataSize = (byteCapacity + 63) & ~63;
        int totalAllocationSize = _dataOffset + totalDataSize + sizeof(uint);

        _basePtr = (byte*)HeapAllocator.Allocate(totalAllocationSize, 64);
        if (_basePtr == null)
            throw new OutOfMemoryException(
                "[Memory Crash] Native allocation failed for UnmanagedPage."
            );

        *(int*)(_basePtr + 4) = totalDataSize;

        Reset();
    }

    /// <summary>
    /// Carves a single-threaded slice of unmanaged memory from the page with specified alignment constraints.
    /// </summary>
    /// <param name="bytesNeeded">The number of bytes required for the allocation. Must be non-negative.</param>
    /// <param name="alignmentRequirements">The memory alignment requirement in bytes. Must be a power of 2 (default is 8).</param>
    /// <returns>A raw pointer pointing to the start of the newly carved memory region.</returns>
    /// <exception cref="OutOfMemoryException">Thrown if the requested allocation exceeds page capacity.</exception>
    /// <remarks>
    /// <para>
    /// <b>Performance &amp; Safety Note:</b> Preconditions (alignment and initialization state) are checked via assertions in <c>DEBUG</c> builds only.
    /// In <c>RELEASE</c> builds, runtime parameter exception checks are omitted for zero-overhead performance.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void* Carve(int bytesNeeded, int alignmentRequirements = 8)
    {
        Debug.Assert(_basePtr != null, "Cannot carve from an uninitialized or disposed page.");
        Debug.Assert(bytesNeeded >= 0, "Bytes needed cannot be negative.");
        Debug.Assert(
            (alignmentRequirements & (alignmentRequirements - 1)) == 0 && alignmentRequirements > 0,
            "Alignment must be a positive power of 2."
        );

        int alignmentMask = alignmentRequirements - 1;
        int currentPosition = *(int*)_basePtr;
        int alignedOffset = (currentPosition + alignmentMask) & ~alignmentMask;
        int nextAllocatedOffset = alignedOffset + bytesNeeded;

        int totalSize = *(int*)(_basePtr + 4);

        if (nextAllocatedOffset > totalSize)
            throw new OutOfMemoryException(
                $"[Memory Crash] UnmanagedPage boundary breached! Requested: {bytesNeeded}B, Capacity: {totalSize}B."
            );

        *(int*)_basePtr = nextAllocatedOffset;
        return _basePtr + (_dataOffset + alignedOffset);
    }

    /// <summary>
    /// Performs a fast, non-atomic carve for single-threaded execution.
    /// Returns <c>false</c> without throwing if capacity is exceeded or the page is disposed.
    /// </summary>
    /// <param name="bytesNeeded">The number of bytes required for the allocation. Must be non-negative.</param>
    /// <param name="resultPtr">When this method returns, contains the allocated pointer if successful, or <c>null</c> if failed.</param>
    /// <param name="alignmentRequirements">The memory alignment requirement in bytes. Must be a power of 2 (default is 8).</param>
    /// <returns><c>true</c> if the memory was successfully carved; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Performance &amp; Safety Note:</b> Preconditions are enforced via assertions in <c>DEBUG</c> builds only.
    /// Runtime exception checks are omitted in <c>RELEASE</c> builds for zero-overhead performance.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryCarve(int bytesNeeded, out void* resultPtr, int alignmentRequirements = 8)
    {
        resultPtr = null;
        if (_basePtr == null)
            return false;

        Debug.Assert(bytesNeeded >= 0, "Bytes needed cannot be negative.");
        Debug.Assert(
            (alignmentRequirements & (alignmentRequirements - 1)) == 0 && alignmentRequirements > 0,
            "Alignment must be a positive power of 2."
        );

        int alignmentMask = alignmentRequirements - 1;
        int currentPosition = *(int*)_basePtr;
        int alignedOffset = (currentPosition + alignmentMask) & ~alignmentMask;
        int nextAllocatedOffset = alignedOffset + bytesNeeded;

        int totalCapacity = *(int*)(_basePtr + 4);

        if (nextAllocatedOffset > totalCapacity)
            return false;

        *(int*)_basePtr = nextAllocatedOffset;
        resultPtr = _basePtr + (_dataOffset + alignedOffset);
        return true;
    }

    /// <summary>
    /// Performs a thread-safe, lock-free atomic slice via a Compare-And-Swap (CAS) loop.
    /// Returns <c>false</c> without mutating allocation state if capacity is exceeded or the page is disposed.
    /// </summary>
    /// <param name="bytesNeeded">The number of bytes required for the allocation. Must be non-negative.</param>
    /// <param name="resultPtr">When this method returns, contains the allocated pointer if successful, or <c>null</c> if failed.</param>
    /// <param name="alignmentRequirements">The memory alignment requirement in bytes. Must be a power of 2 (default is 1).</param>
    /// <returns><c>true</c> if atomic allocation succeeded; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Performance &amp; Safety Note:</b> Preconditions are enforced via assertions in <c>DEBUG</c> builds only.
    /// Runtime exception checks are omitted in <c>RELEASE</c> builds for zero-overhead performance.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryCarveAtomic(
        int bytesNeeded,
        out void* resultPtr,
        int alignmentRequirements = 1
    )
    {
        resultPtr = null;
        if (_basePtr == null)
            return false;

        Debug.Assert(bytesNeeded >= 0, "Bytes needed cannot be negative.");
        Debug.Assert(
            (alignmentRequirements & (alignmentRequirements - 1)) == 0 && alignmentRequirements > 0,
            "Alignment must be a positive power of 2."
        );

        int alignmentMask = alignmentRequirements - 1;
        int* offsetPtr = (int*)_basePtr;
        int totalCapacity = *(int*)(_basePtr + 4);

        int currentOffset,
            alignedOffset,
            nextOffset;
        do
        {
            currentOffset = Volatile.Read(ref *offsetPtr);
            alignedOffset = (currentOffset + alignmentMask) & ~alignmentMask;
            nextOffset = alignedOffset + bytesNeeded;

            if (nextOffset > totalCapacity)
                return false;
        } while (
            Interlocked.CompareExchange(ref *offsetPtr, nextOffset, currentOffset) != currentOffset
        );

        resultPtr = _basePtr + (_dataOffset + alignedOffset);
        return true;
    }

    /// <summary>
    /// Carves a memory segment and zero-initializes the allocated block.
    /// </summary>
    /// <param name="bytesNeeded">The number of bytes required. Must be non-negative.</param>
    /// <param name="alignmentRequirements">The memory alignment requirement in bytes (default is 8).</param>
    /// <returns>A pointer to the zero-initialized memory region.</returns>
    /// <exception cref="OutOfMemoryException">Thrown if the requested allocation exceeds page capacity.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void* CarveZeroed(int bytesNeeded, int alignmentRequirements = 8)
    {
        void* ptr = Carve(bytesNeeded, alignmentRequirements);
        NativeMemoryBufferUtility.ZeroMemory(ptr, bytesNeeded);
        return ptr;
    }

    /// <summary>
    /// Resets the page allocation offset back to zero, effectively recycling the page capacity for reuse,
    /// and refreshes the trailing memory protection canary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        if (_basePtr != null)
        {
            *(int*)_basePtr = 0;

            int totalSize = *(int*)(_basePtr + 4);
            uint* tailGuardAddress = (uint*)(_basePtr + _dataOffset + totalSize);
            Volatile.Write(ref *tailGuardAddress, _tailGuardMagic);
        }
    }

    /// <summary>
    /// Validates that the trailing memory canary (tail guard) has not been stomped or overwritten by buffer overruns.
    /// Compiled out entirely in Release builds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void AssertIntegrity()
    {
#if DEBUG
        if (_basePtr != null)
        {
            int totalSize = *(int*)(_basePtr + 4);
            uint* tailGuardAddress = (uint*)(_basePtr + _dataOffset + totalSize);

            if (Volatile.Read(ref *tailGuardAddress) != _tailGuardMagic)
                throw new InvalidProgramException(
                    "[FATAL ERROR] Memory Corruption Detected! Tail guard magic stomped."
                );
        }
#endif
    }

    /// <summary>
    /// Asserts integrity (in Debug mode), releases the underlying native memory back to the system allocator,
    /// and invalidates the page handle.
    /// </summary>
    public void Dispose()
    {
        if (_basePtr == null)
            return;

        AssertIntegrity();
        HeapAllocator.Free(_basePtr);
        _basePtr = null;
    }

    #region Equality Operators

    /// <summary>
    /// Indicates whether the current page handle is equal to another page handle.
    /// </summary>
    /// <param name="other">An <see cref="UnmanagedPage"/> to compare with this instance.</param>
    /// <returns><c>true</c> if both handles point to the exact same native address; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(UnmanagedPage other) => _basePtr == other._basePtr;

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    /// <param name="obj">The object to compare with the current instance.</param>
    /// <returns><c>true</c> if the specified object is an <see cref="UnmanagedPage"/> pointing to the same address.</returns>
    public override readonly bool Equals(object? obj) =>
        obj is UnmanagedPage other && Equals(other);

    /// <summary>
    /// Returns the hash code for this unmanaged page handle based on its base pointer address.
    /// </summary>
    /// <returns>A 32-bit signed integer hash code.</returns>
    public override readonly int GetHashCode()
    {
        long ptr = (long)_basePtr;
        return (int)ptr ^ (int)(ptr >> 32);
    }

    /// <summary>
    /// Determines whether two specified <see cref="UnmanagedPage"/> instances have the same base pointer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(UnmanagedPage left, UnmanagedPage right) =>
        left._basePtr == right._basePtr;

    /// <summary>
    /// Determines whether two specified <see cref="UnmanagedPage"/> instances have different base pointers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(UnmanagedPage left, UnmanagedPage right) =>
        left._basePtr != right._basePtr;

    #endregion
}
