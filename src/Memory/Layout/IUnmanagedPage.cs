namespace Memory.Layout;

public unsafe interface IUnmanagedPage : IDisposable
{
    public int Capacity { get; }
    public int OccupiedBytes { get; }

    public void* Carve(int bytesNeeded, int alignmentRequirements = 64);
    public bool TryCarve(int bytesNeeded, out void* resultPtr, int alignmentRequirements = 64);
    public void* CarveZeroed(int bytesNeeded, int alignmentRequirements = 64);
    public void Reset();
}
