using System.Runtime.InteropServices;
using Memory.Collections.CircularWindow;

namespace Memory.Tests.Collections.CircularWindow;

public unsafe class NativeCircularWindowTests
{
    private const int _defaultCapacity = 256;

    #region 1. Lifecycle & Allocator Semantics

    [Fact]
    public void Allocate_And_Free_DirectLifecycle_Succeeds()
    {
        NativeCircularWindow<NativeFrame>* window = NativeCircularWindow<NativeFrame>.Allocate(
            _defaultCapacity
        );

        try
        {
            Assert.True(window != null);
            Assert.Equal(_defaultCapacity, window->Capacity);
            Assert.Equal(_defaultCapacity - 1, window->Mask);
            Assert.Equal(0, window->WriteCursor);
            Assert.True(window->SlotsPtr != null);
            Assert.True(window->CursorPtr != null);

            // Verify initial slot sequence tickets are set to -1L (uninitialized)
            CircularWindowSlot<NativeFrame>* slots = window->SlotsPtr;
            for (int i = 0; i < _defaultCapacity; i++)
            {
                Assert.Equal(-1L, slots[i].Sequence);
            }
        }
        finally
        {
            NativeCircularWindow<NativeFrame>.Free(window);
        }
    }

    [Fact]
    public void Allocate_InvalidDimensions_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => NativeCircularWindow<NativeFrame>.Allocate(0));
        Assert.Throws<ArgumentException>(() =>
            NativeCircularWindow<NativeFrame>.Allocate(48) // < 64
        );
        Assert.Throws<ArgumentException>(() =>
            NativeCircularWindow<NativeFrame>.Allocate(127) // Not power of 2
        );
    }

    [Fact]
    public void Allocate_ExceedingIntMaxValueBytes_GuardsAgainstIntegerOverflow()
    {
        // NativeFrame = 24 bytes -> CircularWindowSlot<NativeFrame> = 32 bytes
        // 67,108,864 * 32B = 2,147,483,648 bytes (> int.MaxValue)
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCircularWindow<NativeFrame>.Allocate(67_108_864)
        );
    }

    [Fact]
    public void Free_NullPointer_SafeNoOp()
    {
        NativeCircularWindow<NativeFrame>.Free(null);
    }

    #endregion

    #region 2. Direct Pointer Append & ReadLatest Operations

    [Fact]
    public void TryAppend_And_ReadLatest_DirectPointers_PreservesData()
    {
        NativeCircularWindow<NativeFrame>* window = NativeCircularWindow<NativeFrame>.Allocate(
            _defaultCapacity
        );

        try
        {
            var frame = new NativeFrame
            {
                Timestamp = 987654321L,
                EntityId = 101,
                Flags = 0x0F,
            };

            window->Append(in frame);
            Assert.Equal(1, window->WriteCursor);

            NativeFrame actual;
            int readCount = window->ReadLatest(&actual, 1);

            Assert.Equal(1, readCount);
            Assert.Equal(frame.Timestamp, actual.Timestamp);
            Assert.Equal(frame.EntityId, actual.EntityId);
            Assert.Equal(frame.Flags, actual.Flags);
        }
        finally
        {
            NativeCircularWindow<NativeFrame>.Free(window);
        }
    }

    [Fact]
    public void AppendBatch_DirectPointer_AdvancesCursorAndSetsSequences()
    {
        NativeCircularWindow<NativeFrame>* window = NativeCircularWindow<NativeFrame>.Allocate(
            _defaultCapacity
        );

        try
        {
            const int count = 8;
            NativeFrame* batch = stackalloc NativeFrame[count];
            for (int i = 0; i < count; i++)
            {
                batch[i] = new NativeFrame { EntityId = (ulong)(i + 1), Timestamp = i * 100 };
            }

            window->AppendBatch(batch, count);
            Assert.Equal(count, window->WriteCursor);

            CircularWindowSlot<NativeFrame>* slots = window->SlotsPtr;
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(i, slots[i].Sequence);
                Assert.Equal((ulong)(i + 1), slots[i].Value.EntityId);
            }

            NativeFrame* readDest = stackalloc NativeFrame[count];
            int readCount = window->ReadLatest(readDest, count);

            Assert.Equal(count, readCount);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(batch[i].EntityId, readDest[i].EntityId);
                Assert.Equal(batch[i].Timestamp, readDest[i].Timestamp);
            }
        }
        finally
        {
            NativeCircularWindow<NativeFrame>.Free(window);
        }
    }

    #endregion

    #region 3. Seqlock Protocol & In-Flight Mutation Rejection

    [Fact]
    public void ReadLatest_WhenSlotIsMarkedDirtyWithMinusOne_SkipsTornSlot()
    {
        NativeCircularWindow<NativeFrame>* window = NativeCircularWindow<NativeFrame>.Allocate(64);

        try
        {
            for (int i = 0; i < 5; i++)
            {
                var frame = new NativeFrame { EntityId = (ulong)i, Timestamp = i };
                window->Append(in frame);
            }

            // Simulate an in-flight writer stomping slot index 2 by marking its sequence dirty (-1L)
            CircularWindowSlot<NativeFrame>* slots = window->SlotsPtr;
            slots[2].Sequence = -1L;

            NativeFrame* readBuffer = stackalloc NativeFrame[5];
            int readCount = window->ReadLatest(readBuffer, 5);

            // Slot 2 must be rejected by the pre-read seqlock ticket check; slots 0, 1, 3, 4 survive
            Assert.Equal(4, readCount);
            Assert.Equal(0UL, readBuffer[0].EntityId);
            Assert.Equal(1UL, readBuffer[1].EntityId);
            Assert.Equal(3UL, readBuffer[2].EntityId);
            Assert.Equal(4UL, readBuffer[3].EntityId);
        }
        finally
        {
            NativeCircularWindow<NativeFrame>.Free(window);
        }
    }

    #endregion

    #region 4. C-ABI Predicate Filtering & Reversal

    [Fact]
    public void QueryFiltered_NativeFunctionPointer_FiltersAndHaltsOnCutoff()
    {
        NativeCircularWindow<NativeFrame>* window = NativeCircularWindow<NativeFrame>.Allocate(
            _defaultCapacity
        );

        try
        {
            const int total = 30;
            for (int i = 0; i < total; i++)
            {
                var frame = new NativeFrame { Timestamp = i * 10, EntityId = (ulong)i };
                window->Append(in frame);
            }

            NativeFrame* dest = stackalloc NativeFrame[total];
            long minTimestamp = 150; // Halt when timestamp < 150

            int matched = window->QueryFiltered(
                dest,
                total,
                &minTimestamp,
                &FilterAboveMinTimestamp
            );

            // Items with Timestamp >= 150: 150 through 290 -> 15 items
            Assert.Equal(15, matched);

            // Validates reverse-in-place restoration: output must be chronologically ordered
            for (int i = 0; i < matched; i++)
            {
                long expectedTimestamp = 150 + (i * 10);
                Assert.Equal(expectedTimestamp, dest[i].Timestamp);
            }
        }
        finally
        {
            NativeCircularWindow<NativeFrame>.Free(window);
        }
    }

    #endregion

    #region 5. Multi-Threaded Seqlock Stress Test

    [Fact]
    public void Native_Concurrent_WriterOverwriting_ReadersNeverObserveTearing()
    {
        const int capacity = 64; // Small capacity forces high-frequency wrap-around
        NativeCircularWindow<NativeStressPacket>* window =
            NativeCircularWindow<NativeStressPacket>.Allocate(capacity);

        try
        {
            const int totalWrites = 200_000;
            long tornFramesDetected = 0;

            using var startSignal = new ManualResetEventSlim(false);
            using var writerCompleted = new ManualResetEventSlim(false);

            var writerTask = Task.Run(() =>
            {
                startSignal.Wait();

                for (long seq = 1; seq <= totalWrites; seq++)
                {
                    var packet = new NativeStressPacket
                    {
                        HeadCanary = seq,
                        Timestamp = seq * 5,
                        TailCanary = ~seq,
                    };

                    window->Append(in packet);
                }

                writerCompleted.Set();
            });

            const int readerCount = 2;
            Task[] readerTasks = new Task[readerCount];

            for (int r = 0; r < readerCount; r++)
            {
                readerTasks[r] = Task.Run(() =>
                {
                    startSignal.Wait();
                    NativeStressPacket* buffer = stackalloc NativeStressPacket[16];

                    while (!writerCompleted.IsSet)
                    {
                        int read = window->ReadLatest(buffer, 16);
                        for (int i = 0; i < read; i++)
                        {
                            long head = buffer[i].HeadCanary;
                            long tail = buffer[i].TailCanary;

                            if (head != ~tail)
                            {
                                Interlocked.Increment(ref tornFramesDetected);
                            }
                        }
                    }
                });
            }

            startSignal.Set();
#pragma warning disable xUnit1031 // unsafe test method cannot be async
            Task.WaitAll(writerTask);
            Task.WaitAll(readerTasks);
#pragma warning restore xUnit1031

            Assert.Equal(0, Volatile.Read(ref tornFramesDetected));
            Assert.Equal(totalWrites, window->WriteCursor);
        }
        finally
        {
            NativeCircularWindow<NativeStressPacket>.Free(window);
        }
    }

    #endregion

    #region Test Structs & C-ABI Function Pointers

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NativeFrame
    {
        public long Timestamp;
        public ulong EntityId;
        public uint Flags;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NativeStressPacket
    {
        public long HeadCanary;
        public long Timestamp;
        public long TailCanary;
    }

    private static int FilterAboveMinTimestamp(void* context, NativeFrame* item)
    {
        long min = *(long*)context;
        if (item->Timestamp < min)
            return -1; // Terminal cutoff

        return 1; // Match
    }

    #endregion
}
