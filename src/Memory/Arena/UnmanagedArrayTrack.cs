using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Memory.Arena;

/// <summary>
/// Represents an unmanaged, fixed-capacity array view over raw native memory for elements of type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The unmanaged value type stored in the array track.</typeparam>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct UnmanagedArrayTrack<T>
    where T : unmanaged
{
    private byte* _bufferStart;
    private int _length;
    private int _sizeOfT;

    /// <summary>
    /// Creates an unmanaged array tracking view over an existing block of native memory.
    /// </summary>
    /// <param name="memoryPointer">The base pointer to the native memory block. Must not be <c>null</c> if <paramref name="maxElementCapacity"/> is greater than zero.</param>
    /// <param name="maxElementCapacity">The maximum element capacity of the buffer. Must be non-negative.</param>
    /// <returns>An initialized <see cref="UnmanagedArrayTrack{T}"/> struct wrapping the target memory segment.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxElementCapacity"/> is negative.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="memoryPointer"/> is null while <paramref name="maxElementCapacity"/> is greater than zero.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="memoryPointer"/> does not meet the alignment requirements for type <typeparamref name="T"/>.</exception>
    public static UnmanagedArrayTrack<T> CreateOverPointer(
        void* memoryPointer,
        int maxElementCapacity
    )
    {
        if (maxElementCapacity < 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxElementCapacity),
                maxElementCapacity,
                "Capacity cannot be negative."
            );

        if (maxElementCapacity > 0 && memoryPointer == null)
            throw new ArgumentNullException(
                nameof(memoryPointer),
                "Memory pointer cannot be null when capacity is greater than zero."
            );

        int typeAlignment = sizeof(T) >= 8 ? 8 : 4;
        if (((long)memoryPointer & (typeAlignment - 1)) != 0)
            throw new ArgumentException(
                $"[Alignment Fault] Base address 0x{(long)memoryPointer:X} is unaligned for index tracking allocations of type {typeof(T).Name}.",
                nameof(memoryPointer)
            );

        UnmanagedArrayTrack<T> track;
        track._bufferStart = (byte*)memoryPointer;
        track._length = maxElementCapacity;
        track._sizeOfT = sizeof(T);
        return track;
    }

    /// <summary>
    /// Gets a value indicating whether this tracking view has been initialized with a non-null buffer pointer.
    /// </summary>
    public readonly bool IsCreated => _bufferStart != null;

    /// <summary>
    /// Gets the total element capacity of this unmanaged array track.
    /// </summary>
    public readonly int Length => _length;

    /// <summary>
    /// Gets the raw unmanaged pointer to the start of the underlying memory buffer.
    /// </summary>
    public readonly byte* RawBufferStart => _bufferStart;

    /// <summary>
    /// Gets a pointer to the element at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the element to locate.</param>
    /// <returns>A pointer of type <typeparamref name="T"/> pointing directly to the target element location.</returns>
    /// <remarks>
    /// <para>
    /// <b>Performance &amp; Safety Note:</b> Index bounds are validated via assertions in <c>DEBUG</c> builds only.
    /// In <c>RELEASE</c> builds, runtime bounds checking is omitted for zero-overhead performance. Accessing an out-of-bounds index
    /// in release mode results in undefined behavior or memory access violations.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T* GetPointer(int index)
    {
        Debug.Assert(
            index >= 0 && index < _length,
            $"Index {index} out of bounds layout [0..{_length - 1}]."
        );

        return (T*)(_bufferStart + (index * _sizeOfT));
    }
}
