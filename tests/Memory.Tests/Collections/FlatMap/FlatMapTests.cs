using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Collections.FlatMap;

namespace Memory.Tests.Collections.FlatMap;

public class FlatMapTests
{
    private const int _defaultCapacity = 128;

    #region 1. Basic Add & Lookup Operations

    [Fact]
    public void TryAdd_And_TryGetValue_SingleItem_PreservesValue()
    {
        using var map = new FlatMap<int, TestPayload>(_defaultCapacity);
        Assert.Equal(0, map.Count);
        Assert.True(map.IsEmpty);

        var payload = new TestPayload
        {
            EntityId = 1001,
            StateFlags = 0xAA55,
            Accumulator = 12345.67f,
        };

        bool added = map.TryAdd(42, in payload);
        Assert.True(added);
        Assert.Equal(1, map.Count);
        Assert.False(map.IsEmpty);

        bool found = map.TryGetValue(42, out TestPayload actual);
        Assert.True(found);
        Assert.Equal(payload.EntityId, actual.EntityId);
        Assert.Equal(payload.StateFlags, actual.StateFlags);
        Assert.Equal(payload.Accumulator, actual.Accumulator);

        Assert.True(map.ContainsKey(42));
        Assert.False(map.ContainsKey(999));
    }

    [Fact]
    public void TryAdd_DuplicateKey_ReturnsFalseWithoutMutatingExistingValue()
    {
        using var map = new FlatMap<int, TestPayload>(_defaultCapacity);

        var initial = new TestPayload { EntityId = 1 };
        var duplicate = new TestPayload { EntityId = 2 };

        Assert.True(map.TryAdd(10, in initial));
        Assert.False(map.TryAdd(10, in duplicate));
        Assert.Equal(1, map.Count);

        Assert.True(map.TryGetValue(10, out TestPayload stored));
        Assert.Equal(1UL, stored.EntityId);
    }

    #endregion

    #region 2. Zero-Copy In-Place Mutation

    [Fact]
    public void GetValueRefOrAddDefault_MutatesExistingEntryInPlace()
    {
        using var map = new FlatMap<int, TestPayload>(_defaultCapacity);

        // The first call inserts default and returns the ref
        ref TestPayload entryRef = ref map.GetValueRefOrAddDefault(
            77,
            out bool exists,
            out bool success
        );
        Assert.True(success);
        Assert.False(exists);
        Assert.Equal(1, map.Count);

        // Mutate directly through the managed reference
        entryRef.EntityId = 8888;
        entryRef.Accumulator = 42.0f;

        // The second call retrieves an existing entry
        ref TestPayload existingRef = ref map.GetValueRefOrAddDefault(77, out exists, out success);
        Assert.True(success);
        Assert.True(exists);
        Assert.Equal(8888UL, existingRef.EntityId);
        Assert.Equal(42.0f, existingRef.Accumulator);

        // Verify TryGetValue observes the in-place mutation
        Assert.True(map.TryGetValue(77, out TestPayload read));
        Assert.Equal(8888UL, read.EntityId);
        Assert.Equal(42.0f, read.Accumulator);
    }

    #endregion

    #region 3. Strict 50% Max Load Factor Enforcement

    [Fact]
    public unsafe void LoadFactorEnforcement_RejectsWritesWhenFull_WithoutInfiniteLoop()
    {
        // Request small capacity: scaled to pow2=32 -> physical capacity = 64 slots -> MaxCount = 32
        using var map = new FlatMap<int, int>(16);
        int maxCount = map.MaxCount;
        Assert.True(maxCount > 0);
        Assert.Equal(map.Capacity / 2, maxCount);

        // Fill the table exactly to the 50% load factor threshold
        for (int i = 0; i < maxCount; i++)
        {
            Assert.True(map.TryAdd(i, i * 10));
        }

        Assert.Equal(maxCount, map.Count);
        Assert.True(map.IsFull);

        // Next write must fail immediately without hanging in an infinite probing loop
        Assert.False(map.TryAdd(99999, 0));

        // GetValueRefOrAddDefault must return success = false and a null-referenced ref
        ref int nullRef = ref map.GetValueRefOrAddDefault(99999, out bool exists, out bool success);
        Assert.False(success);
        Assert.False(exists);
        Assert.True(Unsafe.AsPointer(ref nullRef) == null);

        // Missing key lookups must still terminate gracefully and return false
        Assert.False(map.TryGetValue(99999, out _));
        Assert.False(map.ContainsKey(99999));
    }

    #endregion

    #region 4. Backward-Shift Deletion (Robin Hood / Knuth)

    [Fact]
    public void Remove_HealsProbeChains_PreservingCollidingEntries()
    {
        using var map = new FlatMap<int, int>(_defaultCapacity);

        // Populate multiple sequential elements
        const int count = 20;
        for (int i = 0; i < count; i++)
        {
            Assert.True(map.TryAdd(i, i * 100));
        }

        Assert.Equal(count, map.Count);

        // Remove an entry from the middle of the probe chain
        Assert.True(map.Remove(10));
        Assert.False(map.Remove(10)); // Idempotent false
        Assert.Equal(count - 1, map.Count);

        // Verify all remaining entries can still be located (a probe chain was shifted and healed)
        for (int i = 0; i < count; i++)
        {
            if (i == 10)
            {
                Assert.False(map.ContainsKey(i));
                Assert.False(map.TryGetValue(i, out _));
            }
            else
            {
                Assert.True(map.TryGetValue(i, out int value));
                Assert.Equal(i * 100, value);
            }
        }
    }

    [Fact]
    public void Remove_AllEntriesOneByOne_EmptiesMapCleanly()
    {
        using var map = new FlatMap<int, int>(64);
        int max = map.MaxCount;

        for (int i = 0; i < max; i++)
        {
            map.TryAdd(i, i);
        }

        for (int i = 0; i < max; i++)
        {
            Assert.True(map.Remove(i));
        }

        Assert.Equal(0, map.Count);
        Assert.True(map.IsEmpty);

        // Table can be reused after being emptied
        Assert.True(map.TryAdd(42, 4200));
        Assert.Equal(1, map.Count);
        Assert.True(map.TryGetValue(42, out int val));
        Assert.Equal(4200, val);
    }

    #endregion

    #region 5. Enumeration & Clear

    [Fact]
    public void Enumerator_IteratesAllActiveEntries()
    {
        using var map = new FlatMap<int, int>(_defaultCapacity);
        var expected = new Dictionary<int, int>();

        for (int i = 0; i < 15; i++)
        {
            int key = i * 3;
            int val = i * 30;
            map.TryAdd(key, val);
            expected[key] = val;
        }

        int visitedCount = 0;
        foreach (var entry in map)
        {
            Assert.Equal(1u, entry.State);
            Assert.True(expected.TryGetValue(entry.Key, out int expectedVal));
            Assert.Equal(expectedVal, entry.Value);
            visitedCount++;
        }

        Assert.Equal(expected.Count, visitedCount);
    }

    [Fact]
    public void Clear_ResetsCount_And_ErasesAllEntries()
    {
        using var map = new FlatMap<int, int>(_defaultCapacity);
        for (int i = 0; i < 10; i++)
        {
            map.TryAdd(i, i);
        }

        map.Clear();
        Assert.Equal(0, map.Count);
        Assert.True(map.IsEmpty);

        for (int i = 0; i < 10; i++)
        {
            Assert.False(map.ContainsKey(i));
        }
    }

    #endregion

    #region 6. Multi-Word Struct Keys

    [Fact]
    public void MultiWordStructKey_HashesAndMatchesCorrectly()
    {
        using var map = new FlatMap<Custom16ByteKey, int>(_defaultCapacity);

        var keyA = new Custom16ByteKey { High = 0x1122334455667788UL, Low = 0xAABBCCDDEEFF0011UL };
        var keyB = new Custom16ByteKey { High = 0x1122334455667788UL, Low = 0xAABBCCDDEEFF0022UL };

        Assert.True(map.TryAdd(keyA, 100));
        Assert.True(map.TryAdd(keyB, 200));

        Assert.True(map.TryGetValue(keyA, out int valA));
        Assert.Equal(100, valA);

        Assert.True(map.TryGetValue(keyB, out int valB));
        Assert.Equal(200, valB);
    }

    #endregion

    #region 7. Lifecycle & Disposal

    [Fact]
    public unsafe void DangerousGetNativePointer_ReturnsValidPointer()
    {
        using var map = new FlatMap<int, int>(_defaultCapacity);
        NativeFlatMap<int, int>* native = map.DangerousGetNativePointer();

        Assert.True(native != null);
        Assert.Equal(map.Capacity, native->Capacity);
        Assert.Equal(0, native->Count);
    }

    [Fact]
    public unsafe void Dispose_IsIdempotent_And_ThrowsWhenUsedPostDispose()
    {
        var map = new FlatMap<int, int>(_defaultCapacity);
        map.Dispose();
        map.Dispose(); // Safe no-op

        Assert.Throws<ObjectDisposedException>(() => map.TryAdd(1, 1));
        Assert.Throws<ObjectDisposedException>(() => map.TryGetValue(1, out _));
        Assert.Throws<ObjectDisposedException>(() => map.ContainsKey(1));
        Assert.Throws<ObjectDisposedException>(() => map.Remove(1));
        Assert.Throws<ObjectDisposedException>(() => map.Clear());
        Assert.Throws<ObjectDisposedException>(() => map.DangerousGetNativePointer());
    }

    #endregion

    #region Test Structs

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct TestPayload
    {
        public ulong EntityId;
        public uint StateFlags;
        public float Accumulator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Custom16ByteKey : IEquatable<Custom16ByteKey>
    {
        public ulong High;
        public ulong Low;

        public bool Equals(Custom16ByteKey other) => High == other.High && Low == other.Low;

        public override bool Equals(object? obj) => obj is Custom16ByteKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(High, Low);
    }

    #endregion
}
