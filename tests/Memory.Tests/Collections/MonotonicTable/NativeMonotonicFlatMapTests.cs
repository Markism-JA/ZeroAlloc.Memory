using Memory.Collections.MonotonicTable;

namespace Memory.Tests.Collections.MonotonicTable;

public unsafe class NativeMonotonicFlatMapTests
{
    [Fact]
    public void Allocate_AndFree_ManagesLifecycleAndNullFreeSafely()
    {
        NativeMonotonicFlatMap<int, int>* table = NativeMonotonicFlatMap<int, int>.Allocate(32);
        try
        {
            Assert.True(table != null);
            Assert.True(table->IsEmpty);
            Assert.False(table->IsFull);
            Assert.Equal(0, table->Count);
            Assert.True(table->MaxCount >= 32);
            Assert.True(table->BackingMapPtr != null);
        }
        finally
        {
            NativeMonotonicFlatMap<int, int>.Free(table);
            NativeMonotonicFlatMap<int, int>.Free(null);
        }
    }

    [Fact]
    public void TryPut_EnforcesMonotonicSequenceDominance()
    {
        NativeMonotonicFlatMap<int, int>* table = NativeMonotonicFlatMap<int, int>.Allocate(16);
        try
        {
            Assert.Equal(MonotonicResult.Inserted, table->TryPut(1, 100, sequence: 10));
            Assert.Equal(MonotonicResult.Stale, table->TryPut(1, 999, sequence: 9));
            Assert.Equal(
                MonotonicResult.ReplacedAtSameSequence,
                table->TryPut(1, 101, sequence: 10, allowEqualSequence: true)
            );
            Assert.Equal(
                MonotonicResult.Stale,
                table->TryPut(1, 202, sequence: 10, allowEqualSequence: false)
            );
            Assert.Equal(MonotonicResult.Advanced, table->TryPut(1, 300, sequence: 11));

            Assert.True(table->TryGet(1, out int val, out ulong seq));
            Assert.Equal(300, val);
            Assert.Equal(11UL, seq);
            Assert.True(table->ContainsKey(1));

            Assert.True(table->Remove(1));
            Assert.False(table->ContainsKey(1));

            table->TryPut(2, 50, sequence: 1);
            table->Clear();
            Assert.True(table->IsEmpty);
        }
        finally
        {
            NativeMonotonicFlatMap<int, int>.Free(table);
        }
    }
}
