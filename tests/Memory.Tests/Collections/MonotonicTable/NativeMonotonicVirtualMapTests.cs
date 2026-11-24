using Memory.Collections.MonotonicTable;

namespace Memory.Tests.Collections.MonotonicTable;

public unsafe class NativeMonotonicVirtualMapTests
{
    [Fact]
    public void Allocate_AndFree_ManagesLifecycleAndNullFreeSafely()
    {
        NativeMonotonicVirtualMap<int, int>* table = NativeMonotonicVirtualMap<int, int>.Allocate(64);
        try
        {
            Assert.True(table != null);
            Assert.True(table->IsEmpty);
            Assert.Equal(0, table->Count);
            Assert.True(table->KeyCapacity >= 64);
            Assert.True(table->CommittedBytes > 0);
            Assert.True(table->BackingMapPtr != null);
        }
        finally
        {
            NativeMonotonicVirtualMap<int, int>.Free(table);
            NativeMonotonicVirtualMap<int, int>.Free(null);
        }
    }

    [Fact]
    public void TryPut_Remove_Clear_AndPassiveDecommitReclaim_WorkEndToEnd()
    {
        NativeMonotonicVirtualMap<int, int>* table = NativeMonotonicVirtualMap<int, int>.Allocate(64);
        try
        {
            int key = 42;
            int v1 = 10;
            int v2 = 20;
            int v3 = 30;

            Assert.Equal(MonotonicResult.Inserted, table->TryPut(in key, in v1, sequence: 5));
            Assert.Equal(MonotonicResult.Stale, table->TryPut(in key, in v2, sequence: 4));
            Assert.Equal(
                MonotonicResult.ReplacedAtSameSequence,
                table->TryPut(in key, in v2, sequence: 5, allowEqualSequence: true)
            );
            Assert.Equal(
                MonotonicResult.Stale,
                table->TryPut(in key, in v3, sequence: 5, allowEqualSequence: false)
            );
            Assert.Equal(MonotonicResult.Advanced, table->TryPut(in key, in v3, sequence: 6));

            Assert.True(table->TryGet(in key, out int value, out ulong seq));
            Assert.Equal(30, value);
            Assert.Equal(6UL, seq);
            Assert.True(table->ContainsKey(in key));

            Assert.True(table->Remove(in key));
            Assert.False(table->ContainsKey(in key));

            table->Clear();
            table->PassiveDecommitReclaim();
            Assert.True(table->IsEmpty);
        }
        finally
        {
            NativeMonotonicVirtualMap<int, int>.Free(table);
        }
    }
}
