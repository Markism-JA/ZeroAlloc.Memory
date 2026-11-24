using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Collections.FlatMap;

namespace Memory.Tests.Collections.FlatMap;

public unsafe class NativeFlatMapTests
{
    private const int _defaultCapacity = 128;

    #region 1. Lifecycle & Direct Allocation

    [Fact]
    public void Allocate_And_Free_DirectLifecycle_Succeeds()
    {
        NativeFlatMap<int, long>* map = NativeFlatMap<int, long>.Allocate(_defaultCapacity);

        try
        {
            Assert.True(map != null);
            Assert.True(map->IsEmpty);
            Assert.False(map->IsFull);
            Assert.Equal(0, map->Count);
            Assert.True(map->Capacity >= _defaultCapacity * 2);
            Assert.Equal(map->Capacity / 2, map->MaxCount);
            Assert.True(map->EntriesPtr != null);

            // Verify all entry slots are zero-initialized (State == 0)
            FlatMapEntry<int, long>* entries = map->EntriesPtr;
            for (int i = 0; i < map->Capacity; i++)
            {
                Assert.Equal(0u, entries[i].State);
                Assert.Equal(0, entries[i].Key);
                Assert.Equal(0L, entries[i].Value);
            }
        }
        finally
        {
            NativeFlatMap<int, long>.Free(map);
        }
    }

    [Fact]
    public void Allocate_NegativeOrZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeFlatMap<int, long>.Allocate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeFlatMap<int, long>.Allocate(-10));
    }

    [Fact]
    public void Allocate_ExceedingIntMaxValueBytes_GuardsAgainstIntegerOverflow()
    {
        // Requesting slots whose total byte size exceeds int.MaxValue
        // FlatMapEntry<int, long> has a pack size of 24 bytes.
        // 67,108,864 slots * 24B = 1,610,612,736 bytes
        // 134,217,728 slots * 24B = 3,221,225,472 bytes (> 2GB int.MaxValue)
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeFlatMap<int, long>.Allocate(67_108_864) // Pow2*2 = 134,217,728 slots
        );
    }

    [Fact]
    public void Free_NullPointer_SafeNoOp()
    {
        NativeFlatMap<int, long>.Free(null);
    }

    #endregion

    #region 2. Direct Pointer Add, Lookup & Ref Mutations

    [Fact]
    public void TryAdd_And_TryGetValue_DirectPointer_PreservesData()
    {
        NativeFlatMap<uint, NativePayload>* map = NativeFlatMap<uint, NativePayload>.Allocate(
            _defaultCapacity
        );

        try
        {
            var payload = new NativePayload { Id = 42, Checksum = 0xCAFEBABE };

            Assert.True(map->TryAdd(100u, in payload));
            Assert.Equal(1, map->Count);

            Assert.True(map->TryGetValue(100u, out NativePayload read));
            Assert.Equal(42, read.Id);
            Assert.Equal(0xCAFEBABE, read.Checksum);

            Assert.False(map->TryGetValue(200u, out _));
        }
        finally
        {
            NativeFlatMap<uint, NativePayload>.Free(map);
        }
    }

    [Fact]
    public void GetValueRefOrAddDefault_DirectPointer_MutatesInPlace()
    {
        NativeFlatMap<int, NativePayload>* map = NativeFlatMap<int, NativePayload>.Allocate(
            _defaultCapacity
        );

        try
        {
            ref NativePayload payloadRef = ref map->GetValueRefOrAddDefault(
                5,
                out bool exists,
                out bool success
            );
            Assert.True(success);
            Assert.False(exists);

            payloadRef.Id = 777;
            payloadRef.Checksum = 0x12345678;

            Assert.True(map->TryGetValue(5, out NativePayload read));
            Assert.Equal(777, read.Id);
            Assert.Equal(0x12345678u, read.Checksum);
        }
        finally
        {
            NativeFlatMap<int, NativePayload>.Free(map);
        }
    }

    [Fact]
    public void SaturatedMap_GetValueRefOrAddDefault_ReturnsNullRefAndFalse()
    {
        NativeFlatMap<int, int>* map = NativeFlatMap<int, int>.Allocate(16);

        try
        {
            int max = map->MaxCount;
            for (int i = 0; i < max; i++)
            {
                map->TryAdd(i, i);
            }

            Assert.True(map->IsFull);

            ref int nullRef = ref map->GetValueRefOrAddDefault(
                999,
                out bool exists,
                out bool success
            );
            Assert.False(success);
            Assert.False(exists);
            Assert.True(Unsafe.AsPointer(ref nullRef) == null);
        }
        finally
        {
            NativeFlatMap<int, int>.Free(map);
        }
    }

    #endregion

    #region 3. Hash Distribution Across Key Types

    [Theory]
    [InlineData((byte)1, (byte)2)]
    public void HashKey_ByteKey_DispersesBits(byte k1, byte k2)
    {
        uint h1 = NativeFlatMap<byte, int>.HashKey(k1);
        uint h2 = NativeFlatMap<byte, int>.HashKey(k2);
        Assert.NotEqual(h1, h2);
    }

    [Theory]
    [InlineData((ushort)100, (ushort)101)]
    public void HashKey_UShortKey_DispersesBits(ushort k1, ushort k2)
    {
        uint h1 = NativeFlatMap<ushort, int>.HashKey(k1);
        uint h2 = NativeFlatMap<ushort, int>.HashKey(k2);
        Assert.NotEqual(h1, h2);
    }

    [Theory]
    [InlineData(1000u, 1001u)]
    public void HashKey_UIntKey_DispersesBits(uint k1, uint k2)
    {
        uint h1 = NativeFlatMap<uint, int>.HashKey(k1);
        uint h2 = NativeFlatMap<uint, int>.HashKey(k2);
        Assert.NotEqual(h1, h2);
    }

    [Theory]
    [InlineData(100000L, 100001L)]
    public void HashKey_ULongKey_DispersesBits(long k1, long k2)
    {
        uint h1 = NativeFlatMap<long, int>.HashKey(k1);
        uint h2 = NativeFlatMap<long, int>.HashKey(k2);
        Assert.NotEqual(h1, h2);
    }

    #endregion

    #region 4. Robin Hood Backward Shift & Clear

    [Fact]
    public void BackwardShift_CollidingCluster_HealsCorrectly()
    {
        NativeFlatMap<int, int>* map = NativeFlatMap<int, int>.Allocate(_defaultCapacity);

        try
        {
            // Insert 30 consecutive elements to ensure overlapping probe chains
            for (int i = 0; i < 30; i++)
            {
                map->TryAdd(i, i * 10);
            }

            // Remove every other item
            for (int i = 0; i < 30; i += 2)
            {
                Assert.True(map->Remove(i));
            }

            Assert.Equal(15, map->Count);

            // Odd items must still be resolvable through the shifted chain
            for (int i = 1; i < 30; i += 2)
            {
                Assert.True(map->TryGetValue(i, out int val));
                Assert.Equal(i * 10, val);
            }
        }
        finally
        {
            NativeFlatMap<int, int>.Free(map);
        }
    }

    [Fact]
    public void Clear_DirectPointer_ClearsMemoryAndResetsCount()
    {
        NativeFlatMap<int, int>* map = NativeFlatMap<int, int>.Allocate(_defaultCapacity);

        try
        {
            for (int i = 0; i < 10; i++)
            {
                map->TryAdd(i, i);
            }

            map->Clear();
            Assert.Equal(0, map->Count);
            Assert.True(map->IsEmpty);

            FlatMapEntry<int, int>* entries = map->EntriesPtr;
            for (int i = 0; i < map->Capacity; i++)
            {
                Assert.Equal(0u, entries[i].State);
            }
        }
        finally
        {
            NativeFlatMap<int, int>.Free(map);
        }
    }

    #endregion

    #region Test Structs

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativePayload
    {
        public int Id;
        public uint Checksum;
    }

    #endregion
}
