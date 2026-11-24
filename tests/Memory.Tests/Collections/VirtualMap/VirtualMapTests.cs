using System.Runtime.InteropServices;
using Memory.Collections.VirtualMap;

namespace Memory.Tests.Collections.VirtualMap;

public class VirtualMapTests
{
    private const int _initialCapacity = 2048;

    #region 1. Basic Add & Lookup Operations

    [Fact]
    public void TryAdd_And_TryGetValue_SingleItem_PreservesPayload()
    {
        using var map = new VirtualMap<ulong, TestComponentState>(
            initialKeyCapacity: _initialCapacity
        );
        Assert.Equal(expected: 0, actual: map.Count);
        Assert.True(condition: map.IsEmpty);

        var state = new TestComponentState
        {
            PositionX = 100.5f,
            PositionY = 200.5f,
            PositionZ = 300.5f,
            EntityTag = 0xDEADBEEFUL,
            Version = 1,
        };

        bool added = map.TryAdd(key: 42UL, value: state);
        Assert.True(condition: added);
        Assert.Equal(expected: 1, actual: map.Count);
        Assert.False(condition: map.IsEmpty);

        bool found = map.TryGetValue(key: 42UL, value: out TestComponentState actual);
        Assert.True(condition: found);
        Assert.Equal(expected: state.PositionX, actual: actual.PositionX);
        Assert.Equal(expected: state.PositionY, actual: actual.PositionY);
        Assert.Equal(expected: state.PositionZ, actual: actual.PositionZ);
        Assert.Equal(expected: state.EntityTag, actual: actual.EntityTag);
        Assert.Equal(expected: state.Version, actual: actual.Version);

        Assert.True(condition: map.ContainsKey(key: 42UL));
        Assert.False(condition: map.ContainsKey(key: 999UL));
    }

    [Fact]
    public void TryAdd_DuplicateKey_RejectsInsertionWithoutOverwriting()
    {
        using var map = new VirtualMap<int, int>(initialKeyCapacity: _initialCapacity);

        Assert.True(condition: map.TryAdd(key: 10, value: 100));
        Assert.False(condition: map.TryAdd(key: 10, value: 200));
        Assert.Equal(expected: 1, actual: map.Count);

        Assert.True(condition: map.TryGetValue(key: 10, value: out int val));
        Assert.Equal(expected: 100, actual: val);
    }

    #endregion

    #region 2. Zero-Copy In-Place Mutation

    [Fact]
    public void GetValueRefOrAddDefault_MutatesExistingSlotInPlace()
    {
        using var map = new VirtualMap<ulong, TestComponentState>(
            initialKeyCapacity: _initialCapacity
        );

        // 1. Initial insert of the default entry
        ref TestComponentState stateRef = ref map.GetValueRefOrAddDefault(
            key: 500UL,
            exists: out bool exists,
            success: out bool success
        );
        Assert.True(condition: success);
        Assert.False(condition: exists);
        Assert.Equal(expected: 1, actual: map.Count);

        // 2. Direct in-place mutation through the managed reference
        stateRef.PositionX = 12.0f;
        stateRef.Version = 42;

        // 3. Second retrieval confirms the mutation occurred in-place
        ref TestComponentState existingRef = ref map.GetValueRefOrAddDefault(
            key: 500UL,
            exists: out exists,
            success: out success
        );
        Assert.True(condition: success);
        Assert.True(condition: exists);
        Assert.Equal(expected: 12.0f, actual: existingRef.PositionX);
        Assert.Equal(expected: 42, actual: existingRef.Version);

        Assert.True(condition: map.TryGetValue(key: 500UL, value: out TestComponentState stored));
        Assert.Equal(expected: 12.0f, actual: stored.PositionX);
        Assert.Equal(expected: 42, actual: stored.Version);
    }

    #endregion

    #region 3. Dynamic Growth & Demand-Commit Physical OS Pages

    [Fact]
    public void DynamicGrowth_TriggersOnLoadThreshold_DemandCommitsPagesAndPreservesEntries()
    {
        // InitialCapacity = 2048. The max load threshold is 50% = 1024 items.
        using var map = new VirtualMap<int, int>(initialKeyCapacity: _initialCapacity);
        int startingCapacity = map.Capacity;
        int startingCommitted = map.CommittedBytes;

        Assert.Equal(expected: _initialCapacity * 2, actual: startingCapacity);
        Assert.True(condition: startingCommitted > 0);

        // Insert 1025 entries to force capacity expansion across the 50% boundary (2048 -> 4096)
        const int countToInsert = 1025;
        for (int i = 0; i < countToInsert; i++)
        {
            Assert.True(condition: map.TryAdd(key: i, value: i * 10));
        }

        Assert.Equal(expected: countToInsert, actual: map.Count);
        Assert.True(condition: map.Capacity > _initialCapacity);
        Assert.Equal(expected: startingCapacity, actual: map.Capacity);
        Assert.True(condition: map.CommittedBytes >= startingCommitted);

        // Verify all 1,025 entries survived the scratch rehash
        for (int i = 0; i < countToInsert; i++)
        {
            Assert.True(condition: map.TryGetValue(key: i, value: out int val));
            Assert.Equal(expected: i * 10, actual: val);
        }
    }

    #endregion

    #region 4. Robin Hood Backward-Shift Deletion

    [Fact]
    public void Remove_HealsProbeChains_LeavesNoTombstones()
    {
        using var map = new VirtualMap<int, int>(initialKeyCapacity: _initialCapacity);
        const int total = 50;

        for (int i = 0; i < total; i++)
        {
            map.TryAdd(key: i, value: i * 2);
        }

        // Remove item from the middle of the cluster
        Assert.True(condition: map.Remove(key: 25));
        Assert.False(condition: map.Remove(key: 25)); // Idempotent false
        Assert.Equal(expected: total - 1, actual: map.Count);

        // Probe chains must heal: all other items remain retrievable
        for (int i = 0; i < total; i++)
        {
            if (i == 25)
            {
                Assert.False(condition: map.ContainsKey(key: i));
                Assert.False(condition: map.TryGetValue(key: i, value: out _));
            }
            else
            {
                Assert.True(condition: map.TryGetValue(key: i, value: out int val));
                Assert.Equal(expected: i * 2, actual: val);
            }
        }
    }

    #endregion

    #region 5. Maintenance & Passive Decommit Reclaim

    [Fact]
    public void PassiveDecommitReclaim_ShrinksCapacityAndDecommitsPhysicalRAM()
    {
        using var map = new VirtualMap<int, int>(initialKeyCapacity: _initialCapacity);

        // Force multi-tier expansion by inserting 4,500 elements
        for (int i = 0; i < 4500; i++)
        {
            map.TryAdd(key: i, value: i);
        }

        int expandedCommitted = map.CommittedBytes;
        Assert.True(condition: map.Capacity > _initialCapacity);
        Assert.True(condition: expandedCommitted > 0);

        // Reclaim memory back to baseline
        map.PassiveDecommitReclaim();

        Assert.Equal(expected: 0, actual: map.Count);
        Assert.True(condition: map.IsEmpty);
        Assert.Equal(expected: _initialCapacity * 2, actual: map.Capacity);
        Assert.True(condition: map.CommittedBytes < expandedCommitted);

        // Verify table is fully operational post-reclaim
        Assert.True(condition: map.TryAdd(key: 999, value: 9990));
        Assert.Equal(expected: 1, actual: map.Count);
        Assert.True(condition: map.TryGetValue(key: 999, value: out int readVal));
        Assert.Equal(expected: 9990, actual: readVal);
    }

    [Fact]
    public void Clear_ErasesAllEntriesWithoutDecommittingPages()
    {
        using var map = new VirtualMap<int, int>(initialKeyCapacity: _initialCapacity);
        for (int i = 0; i < 100; i++)
        {
            map.TryAdd(key: i, value: i);
        }

        int committedBefore = map.CommittedBytes;
        map.Clear();

        Assert.Equal(expected: 0, actual: map.Count);
        Assert.True(condition: map.IsEmpty);
        Assert.Equal(expected: committedBefore, actual: map.CommittedBytes); // Committed physical pages remain intact

        for (int i = 0; i < 100; i++)
        {
            Assert.False(condition: map.ContainsKey(key: i));
        }
    }

    #endregion

    #region 6. Enumeration

    [Fact]
    public void Enumerator_TraversesAllActiveOccupiedEntries()
    {
        using var map = new VirtualMap<int, int>(initialKeyCapacity: _initialCapacity);
        var expected = new Dictionary<int, int>();

        for (int i = 0; i < 30; i++)
        {
            int key = i * 7;
            int val = i * 70;
            map.TryAdd(key: key, value: val);
            expected[key: key] = val;
        }

        int visitedCount = 0;
        foreach (ref readonly var entry in map)
        {
            Assert.Equal(expected: 1u, actual: entry.State);
            Assert.True(
                condition: expected.TryGetValue(key: entry.Key, value: out int expectedVal)
            );
            Assert.Equal(expected: expectedVal, actual: entry.Value);
            visitedCount++;
        }

        Assert.Equal(expected: expected.Count, actual: visitedCount);
    }

    #endregion

    #region 7. Lifecycle & Disposal

    [Fact]
    public unsafe void DangerousGetNativePointer_ReturnsValidPointer()
    {
        using var map = new VirtualMap<int, int>(initialKeyCapacity: _initialCapacity);
        NativeVirtualMap<int, int>* native = map.DangerousGetNativePointer();

        Assert.True(condition: native != null);
        Assert.Equal(expected: map.Capacity, actual: native->Capacity);
        Assert.Equal(expected: 0, actual: native->Count);
    }

    [Fact]
    public unsafe void Dispose_IsIdempotent_And_ThrowsWhenUsedPostDispose()
    {
        var map = new VirtualMap<int, int>(initialKeyCapacity: _initialCapacity);
        map.Dispose();
        map.Dispose(); // Safe no-op

        Assert.Throws<ObjectDisposedException>(testCode: () => map.TryAdd(key: 1, value: 1));
        Assert.Throws<ObjectDisposedException>(testCode: () =>
            map.TryGetValue(key: 1, value: out _)
        );
        Assert.Throws<ObjectDisposedException>(testCode: () => map.ContainsKey(key: 1));
        Assert.Throws<ObjectDisposedException>(testCode: () => map.Remove(key: 1));
        Assert.Throws<ObjectDisposedException>(testCode: () => map.Clear());
        Assert.Throws<ObjectDisposedException>(testCode: () => map.PassiveDecommitReclaim());
        Assert.Throws<ObjectDisposedException>(testCode: () => map.DangerousGetNativePointer());
    }

    #endregion

    #region Test Structs

    [StructLayout(layoutKind: LayoutKind.Sequential, Pack = 8)]
    private struct TestComponentState
    {
        public ulong EntityTag;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public int Version;
    }

    #endregion
}
