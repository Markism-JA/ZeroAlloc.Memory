namespace Memory.Layout;

/// <summary>
/// Common abstraction for bump-carved unmanaged memory pages.
/// </summary>
public unsafe interface IUnmanagedPage : IDisposable
{
    /// <summary>Gets the total usable payload capacity of the page in bytes.</summary>
    public int Capacity { get; }

    /// <summary>Gets the number of bytes currently carved from the page.</summary>
    public int OccupiedBytes { get; }

    /// <summary>Carves an aligned block of <paramref name="bytesNeeded"/> bytes, throwing if capacity is exceeded.</summary>
    public void* Carve(int bytesNeeded, int alignmentRequirements = 64);

    /// <summary>Attempts to carve an aligned block of <paramref name="bytesNeeded"/> bytes without throwing on exhaustion.</summary>
    public bool TryCarve(int bytesNeeded, out void* resultPtr, int alignmentRequirements = 64);

    /// <summary>Carves an aligned, zero-initialized block of <paramref name="bytesNeeded"/> bytes.</summary>
    public void* CarveZeroed(int bytesNeeded, int alignmentRequirements = 64);

    /// <summary>Resets the bump cursor to zero so the page memory can be reused.</summary>
    public void Reset();
}
