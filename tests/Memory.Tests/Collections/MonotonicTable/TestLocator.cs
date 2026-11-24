using System.Runtime.InteropServices;

namespace Memory.Tests.Collections.MonotonicTable;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly record struct TestLocator(uint NodeId, uint RouteMask)
{
    public readonly uint NodeId = NodeId;
    public readonly uint RouteMask = RouteMask;

    public bool Equals(TestLocator other) => NodeId == other.NodeId && RouteMask == other.RouteMask;

    public override int GetHashCode() => HashCode.Combine(NodeId, RouteMask);
}
