using Memory.Collections.MonotonicTable;
using Memory.Collections.VirtualMap;

namespace Memory.Tests.Collections.MonotonicTable;

public unsafe class MonotonicVirtualMapTests
{
    private const int MinCapacity = 2048; // NativeVirtualMap default key baseline
    #region 1. Monotonic State Machine Invariants

    [Fact]
    public void TryPut_AbsentKey_ReturnsInserted()
    {
        using var map = new MonotonicVirtualMap<ulong, TestLocator>(MinCapacity);

        var locator = new TestLocator(NodeId: 1, RouteMask: 0x0F);
        MonotonicResult result = map.TryPut(key: 5001UL, in locator, sequence: 100);

        Assert.Equal(MonotonicResult.Inserted, result);
        Assert.Equal(1, map.Count);
        Assert.False(map.IsEmpty);

        Assert.True(map.TryGet(5001UL, out TestLocator stored, out ulong seq));
        Assert.Equal(locator.NodeId, stored.NodeId);
        Assert.Equal(locator.RouteMask, stored.RouteMask);
        Assert.Equal(100UL, seq);
    }

    [Fact]
    public void TryPut_DominatingSequence_ReturnsAdvanced_AndOverwritesPayload()
    {
        using var map = new MonotonicVirtualMap<ulong, TestLocator>(MinCapacity);
        var initial = new TestLocator(NodeId: 1, RouteMask: 0x01);
        var advanced = new TestLocator(NodeId: 2, RouteMask: 0x02);

        map.TryPut(key: 5001UL, in initial, sequence: 100);
        MonotonicResult result = map.TryPut(key: 5001UL, in advanced, sequence: 101);

        Assert.Equal(MonotonicResult.Advanced, result);
        Assert.Equal(1, map.Count);

        Assert.True(map.TryGet(5001UL, out TestLocator stored, out ulong seq));
        Assert.Equal(2u, stored.NodeId);
        Assert.Equal(0x02u, stored.RouteMask);
        Assert.Equal(101UL, seq);
    }

    [Fact]
    public void TryPut_StaleSequence_ReturnsStale_AndRetainsExistingPayload()
    {
        using var map = new MonotonicVirtualMap<ulong, TestLocator>(MinCapacity);
        var authoritative = new TestLocator(NodeId: 9, RouteMask: 0xFF);
        var stale = new TestLocator(NodeId: 1, RouteMask: 0x00);

        map.TryPut(key: 5001UL, in authoritative, sequence: 500);
        MonotonicResult result = map.TryPut(key: 5001UL, in stale, sequence: 499);

        Assert.Equal(MonotonicResult.Stale, result);
        Assert.Equal(1, map.Count);

        Assert.True(map.TryGet(5001UL, out TestLocator stored, out ulong seq));
        Assert.Equal(9u, stored.NodeId);
        Assert.Equal(0xFFu, stored.RouteMask);
        Assert.Equal(500UL, seq);
    }

    [Theory]
    [InlineData(true, MonotonicResult.ReplacedAtSameSequence, 99u)]
    [InlineData(false, MonotonicResult.Stale, 10u)]
    public void TryPut_EqualSequence_RespectsAllowEqualFlag(
        bool allowEqual,
        MonotonicResult expectedResult,
        uint expectedNodeId
    )
    {
        using var map = new MonotonicVirtualMap<ulong, TestLocator>(MinCapacity);
        var first = new TestLocator(NodeId: 10, RouteMask: 0x1);
        var candidate = new TestLocator(NodeId: 99, RouteMask: 0x2);

        map.TryPut(key: 7001UL, in first, sequence: 250);
        MonotonicResult result = map.TryPut(
            key: 7001UL,
            in candidate,
            sequence: 250,
            allowEqualSequence: allowEqual
        );

        Assert.Equal(expectedResult, result);
        Assert.True(map.TryGet(7001UL, out TestLocator stored, out ulong seq));
        Assert.Equal(expectedNodeId, stored.NodeId);
        Assert.Equal(250UL, seq);
    }

    #endregion

    #region 2. Out-of-Order Packet Arrival & Convergence

    [Fact]
    public void OutOfOrderArrival_ConvergesToHighestSequence()
    {
        using var map = new MonotonicVirtualMap<ulong, TestLocator>(MinCapacity);

        (ulong seq, uint nodeId)[] stream =
        [
            (10, 100),
            (5, 50),
            (40, 400),
            (25, 250),
            (50, 500),
            (30, 300),
            (49, 490),
        ];

        int staleDrops = 0;
        int acceptedWrites = 0;

        foreach (var (seq, nodeId) in stream)
        {
            var locator = new TestLocator(nodeId, 0x1);
            var result = map.TryPut(key: 42UL, in locator, sequence: seq);

            if (result == MonotonicResult.Stale)
                staleDrops++;
            else
                acceptedWrites++;
        }

        Assert.Equal(3, acceptedWrites); // 10 (Inserted), 40 (Advanced), 50 (Advanced)
        Assert.Equal(4, staleDrops); // 5, 25, 30, 49
        Assert.Equal(1, map.Count);

        Assert.True(map.TryGet(42UL, out TestLocator finalLocator, out ulong finalSeq));
        Assert.Equal(500u, finalLocator.NodeId);
        Assert.Equal(50UL, finalSeq);
    }

    #endregion

    #region 3. Virtual Memory Expansion & Dynamic Growth

    [Fact]
    public void Growth_CrossesKeyCapacity_DoublesCapacityAndPreservesEntriesAndSequences()
    {
        // Allocate baseline capacity (2048 keys -> 4096 physical slots)
        using var map = new MonotonicVirtualMap<int, int>(MinCapacity);

        int initialCapacity = map.Capacity;
        int initialKeyCapacity = map.KeyCapacity;
        int initialCommitted = map.CommittedBytes;

        // Populate past the 50% load factor threshold to force physical page commit and rehash
        int targetInserts = initialKeyCapacity + 64;

        for (int i = 0; i < targetInserts; i++)
        {
            MonotonicResult res = map.TryPut(key: i, value: i * 2, sequence: (ulong)(i + 10));
            Assert.True(res == MonotonicResult.Inserted);
        }

        // Verify capacity doubled and physical commit increased
        Assert.Equal(initialCapacity * 2, map.Capacity);
        Assert.Equal(targetInserts, map.Count);
        Assert.True(map.CommittedBytes >= initialCommitted);

        // Verify all elements retained correct payload and sequence after rehash
        for (int i = 0; i < targetInserts; i++)
        {
            Assert.True(map.TryGet(i, out int val, out ulong seq));
            Assert.Equal(i * 2, val);
            Assert.Equal((ulong)(i + 10), seq);
        }

        // Verify state machine rejects older sequences on rehashed keys
        MonotonicResult staleAttempt = map.TryPut(key: 0, value: -1, sequence: 5);
        Assert.Equal(MonotonicResult.Stale, staleAttempt);
    }

    #endregion

    #region 4. Deletion, Passive Decommit & Resequencing

    [Fact]
    public void Remove_ResetsSequenceHistory_AllowingLowerSequenceReinsertion()
    {
        using var map = new MonotonicVirtualMap<ulong, int>(MinCapacity);
        map.TryPut(key: 1234UL, value: 999, sequence: 10_000);

        Assert.True(map.Remove(1234UL));
        Assert.False(map.ContainsKey(1234UL));
        Assert.Equal(0, map.Count);

        // Insertion with sequence 50 (stale prior to deletion) now succeeds as fresh Inserted
        MonotonicResult result = map.TryPut(key: 1234UL, value: 42, sequence: 50);

        Assert.Equal(MonotonicResult.Inserted, result);
        Assert.True(map.TryGet(1234UL, out int val, out ulong seq));
        Assert.Equal(42, val);
        Assert.Equal(50UL, seq);
    }

    [Fact]
    public void PassiveDecommitReclaim_ShrinksCapacityAndErasesEntries()
    {
        using var map = new MonotonicVirtualMap<int, int>(MinCapacity);

        // Expand past baseline
        int targetInserts = map.KeyCapacity + 100;
        for (int i = 0; i < targetInserts; i++)
        {
            map.TryPut(i, i, (ulong)i);
        }

        Assert.True(map.Capacity > MinCapacity * 2);

        // Reclaim physical pages back down to baseline slots
        map.PassiveDecommitReclaim();

        Assert.Equal(0, map.Count);
        Assert.True(map.IsEmpty);
        Assert.Equal(
            NativeVirtualMap<int, MonotonicEntry<int>>.DefaultMinSlotCapacity,
            map.Capacity
        );

        // Map remains functional after decommit
        Assert.Equal(MonotonicResult.Inserted, map.TryPut(999, 123, 1));
        Assert.True(map.TryGet(999, out int val, out ulong seq));
        Assert.Equal(123, val);
        Assert.Equal(1UL, seq);
    }

    #endregion

    #region 5. Lifecycle & Pointer Invariants

    [Fact]
    public unsafe void DangerousGetNativePointer_ReturnsValidPointer()
    {
        using var map = new MonotonicVirtualMap<int, int>(MinCapacity);
        NativeMonotonicVirtualMap<int, int>* native = map.DangerousGetNativePointer();

        Assert.True(native != null);
        Assert.Equal(map.Capacity, native->Capacity);
        Assert.Equal(0, native->Count);
    }

    [Fact]
    public void Dispose_ThrowsWhenInvokedPostDisposal()
    {
        var map = new MonotonicVirtualMap<int, int>(MinCapacity);
        map.Dispose();
        map.Dispose(); // Idempotent no-op

        Assert.Throws<ObjectDisposedException>(() => map.TryPut(1, 1, 1));
        Assert.Throws<ObjectDisposedException>(() => map.TryGet(1, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => map.ContainsKey(1));
        Assert.Throws<ObjectDisposedException>(() => map.Remove(1));
        Assert.Throws<ObjectDisposedException>(() => map.Clear());
        Assert.Throws<ObjectDisposedException>(() => map.PassiveDecommitReclaim());
        Assert.Throws<ObjectDisposedException>(() => map.DangerousGetNativePointer());
    }

    #endregion
}
