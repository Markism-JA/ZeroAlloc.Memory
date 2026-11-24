using System.Runtime.InteropServices;
using Memory.Collections.VirtualMap;

namespace Memory.Tests.Collections.VirtualMap;

public unsafe class NativeVirtualMapTests
{
    private const int _baselineCapacity = 2048;

    #region 1. Lifecycle & Virtual Reservation Semantics

    [Fact]
    public void Allocate_And_Free_DirectLifecycle_Succeeds()
    {
        NativeVirtualMap<ulong, NativeStatePacket>* map = NativeVirtualMap<
            ulong,
            NativeStatePacket
        >.Allocate(initialKeyCapacity: _baselineCapacity);

        try
        {
            Assert.True(condition: map != null);
            Assert.True(condition: map->IsEmpty);
            Assert.False(condition: map->IsFull);
            Assert.Equal(expected: 0, actual: map->Count);
            Assert.Equal(expected: _baselineCapacity * 2, actual: map->Capacity);
            Assert.True(
                condition: map->MaxSlotCapacity
                    <= NativeVirtualMap<ulong, NativeStatePacket>.AbsoluteMaxSlotCount
            );
            Assert.True(condition: map->CommittedBytes > 0);
            Assert.True(condition: map->SlotsPtr != null);

            // Initially committed slots must be zero-initialized
            VirtualMapEntry<ulong, NativeStatePacket>* slots = map->SlotsPtr;
            for (int i = 0; i < _baselineCapacity; i++)
            {
                Assert.Equal(expected: 0u, actual: slots[i].State);
                Assert.Equal(expected: 0UL, actual: slots[i].Key);
            }
        }
        finally
        {
            NativeVirtualMap<ulong, NativeStatePacket>.Free(map: map);
        }
    }

    [Fact]
    public void Allocate_NegativeOrZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () =>
            NativeVirtualMap<int, int>.Allocate(0)
        );
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () =>
            NativeVirtualMap<int, int>.Allocate(-2048)
        );
    }

    [Fact]
    public void Free_NullPointer_SafeNoOp()
    {
        NativeVirtualMap<int, int>.Free(map: null);
    }

    #endregion

    #region 2. Direct Pointer Add, Lookup & In-Place Mutations

    [Fact]
    public void TryAdd_And_TryGetValue_DirectPointer_PreservesData()
    {
        NativeVirtualMap<uint, NativeStatePacket>* map = NativeVirtualMap<
            uint,
            NativeStatePacket
        >.Allocate(initialKeyCapacity: _baselineCapacity);

        try
        {
            var packet = new NativeStatePacket
            {
                ActorId = 999,
                PayloadCRC = 0xAA55AA55U,
                Heading = 3.14159f,
            };

            Assert.True(condition: map->TryAdd(key: 42u, value: in packet));
            Assert.Equal(expected: 1, actual: map->Count);

            Assert.True(condition: map->TryGetValue(key: 42u, value: out NativeStatePacket read));
            Assert.Equal(expected: packet.ActorId, actual: read.ActorId);
            Assert.Equal(expected: packet.PayloadCRC, actual: read.PayloadCRC);
            Assert.Equal(expected: packet.Heading, actual: read.Heading);

            Assert.False(condition: map->TryGetValue(key: 100u, value: out _));
        }
        finally
        {
            NativeVirtualMap<uint, NativeStatePacket>.Free(map: map);
        }
    }

    [Fact]
    public void GetValueRefOrAddDefault_DirectPointer_MutatesInPlace()
    {
        NativeVirtualMap<int, NativeStatePacket>* map = NativeVirtualMap<
            int,
            NativeStatePacket
        >.Allocate(initialKeyCapacity: _baselineCapacity);

        try
        {
            ref NativeStatePacket packetRef = ref map->GetValueRefOrAddDefault(
                key: 7,
                exists: out bool exists,
                success: out bool success
            );
            Assert.True(condition: success);
            Assert.False(condition: exists);

            packetRef.ActorId = 1234;
            packetRef.PayloadCRC = 0xFEEDFACEU;

            Assert.True(condition: map->TryGetValue(key: 7, value: out NativeStatePacket read));
            Assert.Equal(expected: 1234, actual: read.ActorId);
            Assert.Equal(expected: 0xFEEDFACEU, actual: read.PayloadCRC);
        }
        finally
        {
            NativeVirtualMap<int, NativeStatePacket>.Free(map: map);
        }
    }

    #endregion

    #region 3. Dynamic Growth & Memory Reclamation

    [Fact]
    public void DirectPointer_DynamicGrowth_RehashesWithoutDataLoss()
    {
        NativeVirtualMap<int, int>* map = NativeVirtualMap<int, int>.Allocate(
            initialKeyCapacity: _baselineCapacity
        );

        try
        {
            const int count = 1200; // Crosses 50% load factor threshold of 2048 (1024 entries)
            for (int i = 0; i < count; i++)
            {
                Assert.True(condition: map->TryAdd(key: i, value: i * 5));
            }

            Assert.Equal(expected: count, actual: map->Count);
            Assert.Equal(expected: _baselineCapacity * 2, actual: map->Capacity);

            for (int i = 0; i < count; i++)
            {
                Assert.True(condition: map->TryGetValue(key: i, value: out int val));
                Assert.Equal(expected: i * 5, actual: val);
            }
        }
        finally
        {
            NativeVirtualMap<int, int>.Free(map: map);
        }
    }

    [Fact]
    public void DirectPointer_PassiveDecommitReclaim_ShrinksBackToBaseline()
    {
        NativeVirtualMap<int, int>* map = NativeVirtualMap<int, int>.Allocate(
            initialKeyCapacity: _baselineCapacity
        );

        try
        {
            for (int i = 0; i < 3000; i++)
            {
                map->TryAdd(key: i, value: i);
            }

            int highCommitted = map->CommittedBytes;
            Assert.True(condition: map->Capacity > _baselineCapacity);

            map->PassiveDecommitReclaim();

            Assert.Equal(expected: 0, actual: map->Count);
            Assert.Equal(expected: _baselineCapacity * 2, actual: map->Capacity);
            Assert.True(condition: map->CommittedBytes < highCommitted);
        }
        finally
        {
            NativeVirtualMap<int, int>.Free(map: map);
        }
    }

    #endregion

    #region 4. Backward-Shift Collision Healing

    [Fact]
    public void BackwardShift_CollidingCluster_HealsCorrectly()
    {
        NativeVirtualMap<int, int>* map = NativeVirtualMap<int, int>.Allocate(
            initialKeyCapacity: _baselineCapacity
        );

        try
        {
            // Insert continuous cluster
            for (int i = 0; i < 40; i++)
            {
                map->TryAdd(key: i, value: i * 100);
            }

            // Remove every even entry
            for (int i = 0; i < 40; i += 2)
            {
                Assert.True(condition: map->Remove(key: i));
            }

            Assert.Equal(expected: 20, actual: map->Count);

            // Odd entries must remain retrievable through shifted chains
            for (int i = 1; i < 40; i += 2)
            {
                Assert.True(condition: map->TryGetValue(key: i, value: out int val));
                Assert.Equal(expected: i * 100, actual: val);
            }
        }
        finally
        {
            NativeVirtualMap<int, int>.Free(map: map);
        }
    }

    #endregion

    #region 5. Hash Function Bit Dispersion

    [Theory]
    [InlineData(data: [(byte)10, (byte)11])]
    public void HashKey_ByteKeys_ProduceDistinctHashes(byte k1, byte k2)
    {
        uint h1 = NativeVirtualMap<byte, int>.HashKey(key: in k1);
        uint h2 = NativeVirtualMap<byte, int>.HashKey(key: in k2);
        Assert.NotEqual(expected: h1, actual: h2);
    }

    [Theory]
    [InlineData(data: [100u, 101u])]
    public void HashKey_UIntKeys_ProduceDistinctHashes(uint k1, uint k2)
    {
        uint h1 = NativeVirtualMap<uint, int>.HashKey(key: in k1);
        uint h2 = NativeVirtualMap<uint, int>.HashKey(key: in k2);
        Assert.NotEqual(expected: h1, actual: h2);
    }

    [Theory]
    [InlineData(data: [1000000UL, 1000001UL])]
    public void HashKey_ULongKeys_ProduceDistinctHashes(ulong k1, ulong k2)
    {
        uint h1 = NativeVirtualMap<ulong, int>.HashKey(key: in k1);
        uint h2 = NativeVirtualMap<ulong, int>.HashKey(key: in k2);
        Assert.NotEqual(expected: h1, actual: h2);
    }

    [Fact]
    public void HashKey_MultiWordStructKey_ProducesDispersedHashes()
    {
        var key1 = new NativeMultiWordKey
        {
            High = 0x1111222233334444UL,
            Low = 0x5555666677778888UL,
        };
        var key2 = new NativeMultiWordKey
        {
            High = 0x1111222233334444UL,
            Low = 0x5555666677778889UL,
        };

        uint h1 = NativeVirtualMap<NativeMultiWordKey, int>.HashKey(key: in key1);
        uint h2 = NativeVirtualMap<NativeMultiWordKey, int>.HashKey(key: in key2);

        Assert.NotEqual(expected: h1, actual: h2);
    }

    #endregion

    #region Test Structs

    [StructLayout(layoutKind: LayoutKind.Sequential, Pack = 8)]
    private struct NativeStatePacket
    {
        public int ActorId;
        public uint PayloadCRC;
        public float Heading;
        public int Reserved;
    }

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    public struct NativeMultiWordKey : IEquatable<NativeMultiWordKey>
    {
        public ulong High;
        public ulong Low;

        public bool Equals(NativeMultiWordKey other) => High == other.High && Low == other.Low;

        public override bool Equals(object? obj) =>
            obj is NativeMultiWordKey other && Equals(other: other);

        public override int GetHashCode() => (int)(High ^ Low);
    }

    #endregion
}
