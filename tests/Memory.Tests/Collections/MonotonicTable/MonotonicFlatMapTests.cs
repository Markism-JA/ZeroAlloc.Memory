using System.Runtime.InteropServices;
using Memory.Collections.MonotonicTable;

namespace Memory.Tests.Collections.MonotonicTable;

public class MonotonicFlatMapTests
{
    private const int DefaultCapacity = 64;

    #region 1. Monotonic State Machine Invariant

    [Fact]
    public void TryPut_AbsentKey_ReturnsInserted()
    {
        using var map = new MonotonicFlatMap<int, int>(DefaultCapacity);

        MonotonicResult result = map.TryPut(key: 1, value: 100, sequence: 10);

        Assert.Equal(MonotonicResult.Inserted, result);
        Assert.Equal(1, map.Count);
        Assert.True(map.TryGet(1, out int val, out ulong seq));
        Assert.Equal(100, val);
        Assert.Equal(10UL, seq);
    }

    [Fact]
    public void TryPut_HigherSequence_ReturnsAdvanced_AndUpdatesPayload()
    {
        using var map = new MonotonicFlatMap<int, int>(DefaultCapacity);
        map.TryPut(key: 1, value: 100, sequence: 10);

        MonotonicResult result = map.TryPut(key: 1, value: 200, sequence: 15);

        Assert.Equal(MonotonicResult.Advanced, result);
        Assert.Equal(1, map.Count);
        Assert.True(map.TryGet(1, out int val, out ulong seq));
        Assert.Equal(200, val);
        Assert.Equal(15UL, seq);
    }

    [Fact]
    public void TryPut_LowerSequence_ReturnsStale_AndPreservesExistingData()
    {
        using var map = new MonotonicFlatMap<int, int>(DefaultCapacity);
        map.TryPut(key: 1, value: 100, sequence: 50);

        MonotonicResult result = map.TryPut(key: 1, value: 999, sequence: 49);

        Assert.Equal(MonotonicResult.Stale, result);
        Assert.Equal(1, map.Count);
        Assert.True(map.TryGet(1, out int val, out ulong seq));
        Assert.Equal(100, val);
        Assert.Equal(50UL, seq);
    }

    [Theory]
    [InlineData(true, MonotonicResult.ReplacedAtSameSequence, 999)]
    [InlineData(false, MonotonicResult.Stale, 100)]
    public void TryPut_EqualSequence_RespectsAllowEqualFlag(
        bool allowEqual,
        MonotonicResult expectedResult,
        int expectedValue
    )
    {
        using var map = new MonotonicFlatMap<int, int>(DefaultCapacity);
        map.TryPut(key: 1, value: 100, sequence: 50);

        MonotonicResult result = map.TryPut(
            key: 1,
            value: 999,
            sequence: 50,
            allowEqualSequence: allowEqual
        );

        Assert.Equal(expectedResult, result);
        Assert.True(map.TryGet(1, out int val, out ulong seq));
        Assert.Equal(expectedValue, val);
        Assert.Equal(50UL, seq);
    }

    #endregion

    #region 2. Out-of-Order Packet Arrival Simulation

    [Fact]
    public void OutOfOrderArrival_ConvergesToHighestSequence()
    {
        using var map = new MonotonicFlatMap<int, PacketPayload>(DefaultCapacity);

        // Sequence arriving scrambled over network: 10, 5, 40, 25, 50, 30, 49
        (ulong seq, PacketPayload data)[] stream =
        [
            (10, new PacketPayload(10, 0xAA01)),
            (5, new PacketPayload(5, 0xAA02)),
            (40, new PacketPayload(40, 0xAA03)),
            (25, new PacketPayload(25, 0xAA04)),
            (50, new PacketPayload(50, 0xAA05)),
            (30, new PacketPayload(30, 0xAA06)),
            (49, new PacketPayload(49, 0xAA07)),
        ];

        int staleDrops = 0;
        int accepts = 0;

        foreach (var (seq, data) in stream)
        {
            var res = map.TryPut(key: 42, value: in data, sequence: seq);
            if (res == MonotonicResult.Stale)
                staleDrops++;
            else
                accepts++;
        }

        // Accepts: 10 (Inserted), 40 (Advanced), 50 (Advanced) = 3
        // Stale drops: 5, 25, 30, 49 = 4
        Assert.Equal(3, accepts);
        Assert.Equal(4, staleDrops);
        Assert.Equal(1, map.Count);

        Assert.True(map.TryGet(42, out PacketPayload finalData, out ulong finalSeq));
        Assert.Equal(50, finalData.PayloadId);
        Assert.Equal(0xAA05u, finalData.Flags);
        Assert.Equal(50UL, finalSeq);
    }

    #endregion

    #region 3. Deletion & Resequencing

    [Fact]
    public void Remove_ResetsSequenceHistory_AllowingOlderSequenceReinsertion()
    {
        using var map = new MonotonicFlatMap<int, int>(DefaultCapacity);
        map.TryPut(key: 1, value: 100, sequence: 1000);

        Assert.True(map.Remove(1));
        Assert.False(map.ContainsKey(1));
        Assert.Equal(0, map.Count);

        // Putting with sequence 50 (which was previously stale relative to 1000)
        // must now succeed as a brand-new insertion.
        MonotonicResult result = map.TryPut(key: 1, value: 50, sequence: 50);

        Assert.Equal(MonotonicResult.Inserted, result);
        Assert.True(map.TryGet(1, out int val, out ulong seq));
        Assert.Equal(50, val);
        Assert.Equal(50UL, seq);
    }

    #endregion

    #region 4. High Sequence Boundaries

    [Fact]
    public void TryPut_NearUInt64Max_AdvancesCorrectly()
    {
        using var map = new MonotonicFlatMap<int, int>(DefaultCapacity);
        const ulong nearMax = ulong.MaxValue - 10;

        Assert.Equal(MonotonicResult.Inserted, map.TryPut(1, 1, nearMax));
        Assert.Equal(MonotonicResult.Advanced, map.TryPut(1, 2, ulong.MaxValue));

        Assert.True(map.TryGet(1, out int val, out ulong seq));
        Assert.Equal(2, val);
        Assert.Equal(ulong.MaxValue, seq);
    }

    #endregion

    #region Test Structs

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public readonly struct PacketPayload(int payloadId, uint flags) : IEquatable<PacketPayload>
    {
        public readonly int PayloadId = payloadId;
        public readonly uint Flags = flags;

        public bool Equals(PacketPayload other) =>
            PayloadId == other.PayloadId && Flags == other.Flags;

        public override bool Equals(object? obj) => obj is PacketPayload other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(PayloadId, Flags);
    }

    #endregion
}
