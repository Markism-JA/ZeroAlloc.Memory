using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Memory.Collections.MirroredMpsc;

/// <summary>
/// Represents a zero-allocation, stack-only view pointing directly to an active unmanaged block segment.
/// </summary>
public readonly unsafe ref struct BlockView
{
    /// <summary>
    /// Gets the direct unmanaged pointer to the start of the payload data.
    /// </summary>
    public readonly byte* Payload;

    /// <summary>
    /// Gets the active size of the payload in bytes.
    /// </summary>
    public readonly int Length;

    /// <summary>
    /// Gets the total aligned step size (including header and padding) consumed by this block in the ring buffer.
    /// </summary>
    public readonly int AlignedTotalSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlockView"/> struct.
    /// </summary>
    /// <param name="payload">The direct memory pointer to the payload data. Must not be <c>null</c> if <paramref name="length"/> is greater than zero.</param>
    /// <param name="length">The length of the payload in bytes. Must be non-negative.</param>
    /// <param name="alignedTotalSize">The padded total byte footprint occupied in the buffer. Must be strictly greater than zero.</param>
    /// <remarks>
    /// <para>
    /// <b>Performance &amp; Safety Note:</b> Preconditions are enforced via assertions in <c>DEBUG</c> builds only.
    /// In <c>RELEASE</c> builds, runtime exception checks are omitted for zero-overhead performance. Passing invalid parameters
    /// in release mode will result in undefined behavior or memory access violations.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BlockView(byte* payload, int length, int alignedTotalSize)
    {
        Debug.Assert(length >= 0, "Payload length cannot be negative.");
        Debug.Assert(
            alignedTotalSize > 0,
            "Aligned total size must be strictly greater than zero."
        );
        Debug.Assert(
            length == 0 || payload != null,
            "Payload pointer cannot be null when length is greater than zero."
        );

        Payload = payload;
        Length = length;
        AlignedTotalSize = alignedTotalSize;
    }

    /// <summary>
    /// Safety-wraps the raw unmanaged pointer into a managed memory window (<see cref="ReadOnlySpan{T}"/>)
    /// without heap allocations or runtime garbage tracking overhead.
    /// </summary>
    /// <returns>A safe <see cref="ReadOnlySpan{T}"/> spanning the underlying unmanaged block payload.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan() => new ReadOnlySpan<byte>(Payload, Length);
}
