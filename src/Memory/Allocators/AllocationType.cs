namespace Memory.Allocators;

/// <summary>
/// Specifies the underlying physical or virtual backing source of an unmanaged allocation.
/// </summary>
public enum AllocationType : byte
{
    /// <summary>
    /// Allocated directly on the process user-space C-heap via native aligned allocators.
    /// </summary>
    UserSpaceHeap,

    /// <summary>
    /// Backed by virtual page reservations with lazy demand-paging commitment.
    /// </summary>
    KernelVirtual,
}
