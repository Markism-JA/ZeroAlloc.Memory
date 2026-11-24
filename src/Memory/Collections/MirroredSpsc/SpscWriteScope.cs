using System.Runtime.CompilerServices;

namespace Memory.Collections.MirroredSpsc;

/// <summary>
/// Stack-allocated RAII scope for zero-copy in-place writing into a <see cref="MirroredSpscRing"/>.
/// Auto-commits the reserved block with a single Store-Release barrier upon disposal.
/// Safe against struct copying in 'using (scope)' blocks.
/// </summary>
public ref struct SpscWriteScope
{
    private unsafe NativeMirroredSpscRing* _ring;
    private readonly unsafe byte* _destination;
    private readonly int _length;
    private readonly SpscWriteTicket _ticket;

    /// <summary>
    /// Gets the raw unmanaged pointer to the start of the reserved payload memory.
    /// </summary>
    public unsafe byte* Pointer => _destination;

    /// <summary>
    /// Gets a Span slice covering the reserved payload memory for safe zero-copy writes.
    /// </summary>
    public unsafe Span<byte> Span => new(_destination, _length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe SpscWriteScope(
        NativeMirroredSpscRing* ring,
        byte* destination,
        int length,
        SpscWriteTicket ticket
    )
    {
        _ring = ring;
        _destination = destination;
        _length = length;
        _ticket = ticket;
    }

    /// <summary>
    /// Explicitly aborts the write operation, preventing the reserved space from being committed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Abort()
    {
        if (_ring != null)
        {
            _ring->AbortReserve(in _ticket);
            _ring = null;
        }
    }

    /// <summary>Commits the reserved write block with a store-release barrier.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Dispose()
    {
        if (_ring != null)
        {
            _ring->CommitWrite(in _ticket);
            _ring = null;
        }
    }
}
