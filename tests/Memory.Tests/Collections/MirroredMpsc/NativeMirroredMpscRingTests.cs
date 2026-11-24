using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Collections.MirroredMpsc;

namespace Memory.Tests.Collections.MirroredMpsc;

public unsafe class NativeMirroredMpscRingTests
{
    private const int _testCapacity = 64 * 1024; // 64 KB hardware boundary
    #region 1. Lifecycle & Allocator Semantics

    [Fact]
    public void Allocate_And_Free_DirectLifecycle_Succeeds()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            Assert.True(ring != null);
            Assert.True(ring->IsEmpty);
            Assert.Equal(_testCapacity, ring->Capacity);
            Assert.Equal(0, ring->OccupiedBytes);
            Assert.Equal(_testCapacity, ring->FreeBytes);
            Assert.Equal(1, ring->HeadSequence);
            Assert.Equal(1, ring->TailSequence);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    [Fact]
    public void Allocate_NegativeOrZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeMirroredMpscRing.Allocate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeMirroredMpscRing.Allocate(-4096));
    }

    [Fact]
    public void Free_NullPointer_SafeNoOp()
    {
        // Must safely NO-OP without crashing
        NativeMirroredMpscRing.Free(null);
    }

    #endregion

    #region 2. Direct Value-Copy Framing via Pointers

    [Fact]
    public void TryWrite_And_TryRead_DirectPointer_PreservesPayload()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            uint value = 0xFEEDFACE;
            Assert.True(ring->TryWrite((byte*)&value, sizeof(uint)));

            Assert.False(ring->IsEmpty);
            Assert.True(ring->TryRead(out BlockView view));
            Assert.Equal(sizeof(uint), view.Length);
            Assert.Equal(value, *(uint*)view.Payload);

            ring->AdvanceConsumer(in view);
            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    [Fact]
    public void TryWrite_DualSegments_DirectPointer_CombinesContiguously()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            ulong part1 = 0x1122334455667788UL;
            uint part2 = 0xAABBCCDD;

            bool writeOk = ring->TryWrite(
                (byte*)&part1,
                sizeof(ulong),
                (byte*)&part2,
                sizeof(uint)
            );
            Assert.True(writeOk);

            Assert.True(ring->TryRead(out BlockView view));
            Assert.Equal(sizeof(ulong) + sizeof(uint), view.Length);
            Assert.Equal(part1, *(ulong*)view.Payload);
            Assert.Equal(part2, *(uint*)(view.Payload + sizeof(ulong)));

            ring->AdvanceConsumer(in view);
            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    [Fact]
    public void TryWrite_GenericStruct_DirectPointer_Succeeds()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            var header = new NativeTestHeader { Opcode = 0x7F, SequenceId = 99999UL };
            Assert.True(ring->TryWrite(in header));

            Assert.True(ring->TryRead(out BlockView view));
            Assert.Equal(sizeof(NativeTestHeader), view.Length);
            ref readonly var actual = ref *(NativeTestHeader*)view.Payload;
            Assert.Equal((ushort)0x7F, actual.Opcode);
            Assert.Equal(99999UL, actual.SequenceId);

            ring->AdvanceConsumer(in view);
            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    #endregion

    #region 3. Manual CommitRead Advancement

    [Fact]
    public void CommitRead_AdvancesHeadDirectlyByByteCount()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            int val = 42;
            Assert.True(ring->TryWrite((byte*)&val, sizeof(int)));

            Assert.True(ring->TryRead(out BlockView view));
            Assert.Equal(42, *(int*)view.Payload);

            // Advance head directly using CommitRead by AlignedTotalSize
            ring->CommitRead(view.AlignedTotalSize);

            Assert.True(ring->IsEmpty);
            Assert.Equal(0, ring->OccupiedBytes);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    #endregion

    #region 4. Native Batch Draining & BatchReader

    [Fact]
    public void DrainBatch_DirectNativePointer_DrainsBatchCorrectly()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            const int count = 6;
            for (int i = 0; i < count; i++)
            {
                int val = (i + 1) * 10;
                Assert.True(ring->TryWrite((byte*)&val, sizeof(int)));
            }

            var collector = new NativeTestCollector(maxItems: 16);
            int drained = ring->DrainBatch(ref collector, maxBatchSize: 16);

            Assert.Equal(count, drained);
            Assert.Equal(count, collector.Count);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal((i + 1) * 10, collector.ReceivedValues[i]);
            }

            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    [Fact]
    public void Static_BeginBatch_BatchReader_IteratesAndCommitsDirectly()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            for (int i = 0; i < 4; i++)
            {
                int val = i + 100;
                Assert.True(ring->TryWrite((byte*)&val, sizeof(int)));
            }

            using (var reader = NativeMirroredMpscRing.BeginBatch(ring, maxBatchSize: 10))
            {
                int expected = 100;
                while (reader.TryReadNext(out BlockView view))
                {
                    Assert.Equal(expected++, *(int*)view.Payload);
                }
                Assert.Equal(4, reader.ReadCount);
            }

            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    [Fact]
    public void Static_BeginBatch_BatchReader_Abort_DoesNotCommit()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            int val = 777;
            Assert.True(ring->TryWrite((byte*)&val, sizeof(int)));

            using (var reader = NativeMirroredMpscRing.BeginBatch(ring, maxBatchSize: 10))
            {
                Assert.True(reader.TryReadNext(out BlockView view));
                Assert.Equal(777, *(int*)view.Payload);
                reader.Abort();
            }

            Assert.False(ring->IsEmpty);
            Assert.True(ring->TryRead(out BlockView peeked));
            Assert.Equal(777, *(int*)peeked.Payload);
            ring->AdvanceConsumer(in peeked);
            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    #endregion

    #region 5. Virtual Memory Mirror Boundary Straddling (Native)

    [Fact]
    public void Native_Write_StraddlingBufferBoundary_Succeeds()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(_testCapacity);
        try
        {
            long capacity = ring->Capacity;
            int initialPaddingSize = (int)(capacity - 128 - sizeof(BlockHeader));

            byte* dummyBuffer = (byte*)NativeMemory.Alloc((nuint)initialPaddingSize);
            try
            {
                Assert.True(ring->TryWrite(dummyBuffer, initialPaddingSize));
                Assert.True(ring->TryRead(out BlockView padView));
                ring->AdvanceConsumer(in padView);
                Assert.True(ring->IsEmpty);
            }
            finally
            {
                NativeMemory.Free(dummyBuffer);
            }

            // Straddle frame
            const int straddleSize = 256;
            Span<byte> straddleData = stackalloc byte[straddleSize];
            for (int i = 0; i < straddleSize; i++)
                straddleData[i] = (byte)(i ^ 0x3C);

            fixed (byte* pData = straddleData)
            {
                Assert.True(ring->TryWrite(pData, straddleSize));
            }

            Assert.True(ring->TryRead(out BlockView straddleView));
            Assert.Equal(straddleSize, straddleView.Length);

            Span<byte> actualSpan = new(straddleView.Payload, straddleView.Length);
            Assert.True(actualSpan.SequenceEqual(straddleData));

            ring->AdvanceConsumer(in straddleView);
            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    #endregion

    #region 6. Multi-Producer Direct Native Dual-Canary Stress Test

    [Fact]
    public void Native_Concurrent_MPSC_MultiProducer_GuaranteesStoreReleaseOrdering()
    {
        NativeMirroredMpscRing* ring = NativeMirroredMpscRing.Allocate(8 * 1024 * 1024);
        try
        {
            const int producerCount = 4;
            const int itemsPerProducer = 50_000;
            const int totalExpectedItems = producerCount * itemsPerProducer;

            long tornFrameDetected = 0;
            long failedSequence = -1;

            using var startSignal = new ManualResetEventSlim(false);
            using var producersCompleted = new CountdownEvent(producerCount);

            Task[] producerTasks = new Task[producerCount];
            for (int t = 0; t < producerCount; t++)
            {
                int threadId = t;
                producerTasks[t] = Task.Run(() =>
                {
                    startSignal.Wait();
                    byte* messageBuffer = stackalloc byte[256];
                    SpinWait spinner = default;

                    for (long seq = 0; seq < itemsPerProducer; seq++)
                    {
                        int payloadLength = 32 + (int)(seq % 64);
                        long canaryId = ((long)threadId << 32) | (seq & 0xFFFFFFFFL);

                        // Head canary
                        *(long*)messageBuffer = canaryId;

                        // Interior pattern
                        byte pattern = (byte)(canaryId ^ 0x5A);
                        for (int b = 8; b < payloadLength - 8; b++)
                        {
                            messageBuffer[b] = pattern;
                        }

                        // Tail canary
                        *(long*)(messageBuffer + payloadLength - 8) = ~canaryId;

                        while (!ring->TryWrite(messageBuffer, payloadLength))
                        {
                            spinner.SpinOnce();
                        }
                    }

                    producersCompleted.Signal();
                });
            }

            var consumerTask = Task.Run(() =>
            {
                startSignal.Wait();
                int itemsHarvested = 0;
                long[] expectedPerThread = new long[producerCount];
                SpinWait spinner = default;

                while (itemsHarvested < totalExpectedItems)
                {
                    if (ring->TryRead(out BlockView view))
                    {
                        long headCanary = *(long*)view.Payload;
                        long tailCanary = *(long*)(view.Payload + view.Length - 8);

                        if (headCanary != ~tailCanary)
                        {
                            Interlocked.Increment(ref tornFrameDetected);
                            Volatile.Write(ref failedSequence, headCanary);
                            break;
                        }

                        int threadId = (int)(headCanary >> 32);
                        long seq = headCanary & 0xFFFFFFFFL;

                        // Sequence monotonic check per producer
                        if (seq != expectedPerThread[threadId])
                        {
                            Interlocked.Increment(ref tornFrameDetected);
                            Volatile.Write(ref failedSequence, headCanary);
                            break;
                        }
                        expectedPerThread[threadId]++;

                        // Validate interior pattern
                        byte expectedPattern = (byte)(headCanary ^ 0x5A);
                        byte* payloadBody = view.Payload + 8;
                        int bodyLength = view.Length - 16;
                        for (int b = 0; b < bodyLength; b++)
                        {
                            if (payloadBody[b] != expectedPattern)
                            {
                                Interlocked.Increment(ref tornFrameDetected);
                                Volatile.Write(ref failedSequence, headCanary);
                                break;
                            }
                        }

                        ring->AdvanceConsumer(in view);
                        itemsHarvested++;
                    }
                    else
                    {
                        spinner.SpinOnce();
                    }
                }
            });

            startSignal.Set();
            Task.WaitAll(producerTasks);
            consumerTask.Wait();

            Assert.Equal(0, Volatile.Read(ref tornFrameDetected));
            Assert.Equal(-1, Volatile.Read(ref failedSequence));
            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredMpscRing.Free(ring);
        }
    }

    #endregion

    #region Test Structs

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTestHeader
    {
        public ushort Opcode;
        public ulong SequenceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTestCollector : IBlockConsumer
    {
        public readonly int[] ReceivedValues;
        public int Count;

        public NativeTestCollector(int maxItems)
        {
            ReceivedValues = new int[maxItems];
            Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(byte* payload, int length)
        {
            if (length >= sizeof(int))
            {
                ReceivedValues[Count++] = *(int*)payload;
                return true;
            }
            return false;
        }
    }

    #endregion
}
