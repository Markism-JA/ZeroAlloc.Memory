using System.Runtime.CompilerServices;

namespace Memory.Collections.MirroredMpsc;

/// <summary>
/// Unmanaged static visitor interface for high-performance batch draining.
/// </summary>
public unsafe interface IBlockConsumer
{
    /// <summary>
    /// Consumes a single committed block directly from the mirrored ring memory.
    /// </summary>
    /// <param name="payload">Direct pointer to the block payload.</param>
    /// <param name="length">Length of the payload in bytes.</param>
    /// <returns>True to continue batch drain; false to halt batch execution early (backpressure).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Consume(byte* payload, int length);
}
