using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Collections.BoundedMpsc;

namespace Memory.Tests.Collections.BoundedMpsc;

public unsafe class NativeBoundedMpscRingTests
{
    private const int _defaultCapacity = 4096;

    #region 1. Lifecycle & Allocator Semantics

    [Fact]
    public void Allocate_And_Free_DirectLifecycle_Succeeds()
    {
        NativeBoundedMpscRing<NativeTestPayload>* ring =
            NativeBoundedMpscRing<NativeTestPayload>.Allocate(_defaultCapacity);

        try
        {
            Assert.True(ring != null);
            Assert.True(ring->IsEmpty);
            Assert.Equal(_defaultCapacity, ring->Capacity);
            Assert.Equal(0, ring->OccupiedCount);
            Assert.Equal(_defaultCapacity, ring->FreeCount);
            Assert.Equal(0, ring->HeadSequence);
            Assert.Equal(0, ring->TailSequence);

            // Verify turnstile sequence ticket initialization: slot[i].Sequence == i
            BoundedMpscSlot<NativeTestPayload>* slots = ring->DangerousGetSlots();
            Assert.True(slots != null);
            for (int i = 0; i < _defaultCapacity; i++)
            {
                Assert.Equal(i, slots[i].Sequence);
            }
        }
        finally
        {
            NativeBoundedMpscRing<NativeTestPayload>.Free(ring);
        }
    }

    [Fact]
    public void Allocate_NegativeOrZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeBoundedMpscRing<NativeTestPayload>.Allocate(0)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeBoundedMpscRing<NativeTestPayload>.Allocate(-4096)
        );
    }

    [Fact]
    public void Allocate_ExceedingIntMaxValueBytes_GuardsAgainstIntegerOverflow()
    {
        // 134,217,728 elements * 24B slot size = 3,221,225,472 bytes (> 2GB int.MaxValue)
        // Passes MaxAllowedCapacity (1,073,741,824) but correctly trips the totalBytesNeeded overflow check
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeBoundedMpscRing<NativeTestPayload>.Allocate(134_217_728)
        );
    }

    [Fact]
    public void Free_NullPointer_SafeNoOp()
    {
        // Must not fault or throw on null
        NativeBoundedMpscRing<NativeTestPayload>.Free(null);
    }

    #endregion

    #region 2. Direct Pointer Enqueue & Dequeue Operations

    [Fact]
    public void TryWrite_And_TryRead_DirectPointer_PreservesPayload()
    {
        NativeBoundedMpscRing<NativeTestPayload>* ring =
            NativeBoundedMpscRing<NativeTestPayload>.Allocate(_defaultCapacity);

        try
        {
            var item = new NativeTestPayload
            {
                Id = 42,
                Checksum = 0xCAFEBABEU,
                Payload = 0x1234567890ABCDEFUL,
            };

            Assert.True(ring->TryWrite(in item));
            Assert.False(ring->IsEmpty);

            Assert.True(ring->TryRead(out NativeTestPayload actual));
            Assert.Equal(item.Id, actual.Id);
            Assert.Equal(item.Checksum, actual.Checksum);
            Assert.Equal(item.Payload, actual.Payload);

            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeBoundedMpscRing<NativeTestPayload>.Free(ring);
        }
    }

    [Fact]
    public void TurnstileProgression_AdvancesSequenceByFullCapacityPerCycle()
    {
        const int capacity = 4;
        NativeBoundedMpscRing<NativeTestPayload>* ring =
            NativeBoundedMpscRing<NativeTestPayload>.Allocate(capacity);

        try
        {
            BoundedMpscSlot<NativeTestPayload>* slots = ring->DangerousGetSlots();

            // Initial ticket sequence: [0, 1, 2, 3]
            Assert.Equal(0, slots[0].Sequence);

            var item = new NativeTestPayload { Id = 100 };
            Assert.True(ring->TryWrite(in item));

            // Producer committed: slot[0].Sequence must be (0 + 1) = 1
            Assert.Equal(1, slots[0].Sequence);

            Assert.True(ring->TryRead(out _));

            // Consumer drained: slot[0].Sequence must advance to (targetPos + capacity) = 0 + 4 = 4
            Assert.Equal(4, slots[0].Sequence);
        }
        finally
        {
            NativeBoundedMpscRing<NativeTestPayload>.Free(ring);
        }
    }

    #endregion

    #region 3. Native Batch Draining & BatchReader

    [Fact]
    public void DrainBatch_DirectPointer_DrainsBatchCorrectly()
    {
        NativeBoundedMpscRing<NativeTestPayload>* ring =
            NativeBoundedMpscRing<NativeTestPayload>.Allocate(_defaultCapacity);

        try
        {
            const int count = 6;
            for (int i = 0; i < count; i++)
            {
                var payload = new NativeTestPayload { Id = (i + 1) * 10 };
                Assert.True(ring->TryWrite(in payload));
            }

            var collector = new NativeCollector(maxItems: 16);
            int drained = ring->DrainBatch(ref collector, maxBatchSize: 16);

            Assert.Equal(count, drained);
            Assert.Equal(count, collector.Count);

            for (int i = 0; i < count; i++)
            {
                Assert.Equal((i + 1) * 10, collector.Items[i].Id);
            }

            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeBoundedMpscRing<NativeTestPayload>.Free(ring);
        }
    }

    [Fact]
    public void Static_BeginBatch_BatchReader_IteratesAndCommitsDirectly()
    {
        NativeBoundedMpscRing<NativeTestPayload>* ring =
            NativeBoundedMpscRing<NativeTestPayload>.Allocate(_defaultCapacity);

        try
        {
            for (int i = 0; i < 4; i++)
            {
                var payload = new NativeTestPayload { Id = i + 100 };
                Assert.True(ring->TryWrite(in payload));
            }

            using (
                var reader = NativeBoundedMpscRing<NativeTestPayload>.BeginBatch(
                    ring,
                    maxBatchSize: 10
                )
            )
            {
                int expected = 100;
                while (reader.TryReadNext(out NativeTestPayload item))
                {
                    Assert.Equal(expected++, item.Id);
                }
                Assert.Equal(4, reader.ReadCount);
            }

            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeBoundedMpscRing<NativeTestPayload>.Free(ring);
        }
    }

    #endregion

    #region 4. Multi-Producer Monotonic Dual-Canary Stress Test

    [Fact]
    public void Native_Concurrent_MPSC_MultiProducer_GuaranteesStoreReleaseOrdering()
    {
        const int capacity = 65_536;
        NativeBoundedMpscRing<NativeStressPacket>* ring =
            NativeBoundedMpscRing<NativeStressPacket>.Allocate(capacity);

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
                    SpinWait spinner = default;

                    for (long seq = 0; seq < itemsPerProducer; seq++)
                    {
                        long packetId = ((long)threadId << 32) | (seq & 0xFFFFFFFFL);
                        var packet = new NativeStressPacket
                        {
                            HeadCanary = packetId,
                            Sequence = seq,
                            ProducerId = threadId,
                            TailCanary = ~packetId,
                        };

                        while (!ring->TryWrite(in packet))
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
                long[] expectedPerProducer = new long[producerCount];
                SpinWait spinner = default;

                while (itemsHarvested < totalExpectedItems)
                {
                    if (ring->TryRead(out NativeStressPacket packet))
                    {
                        // 1. Bitwise dual-canary validation
                        if (packet.HeadCanary != ~packet.TailCanary)
                        {
                            Interlocked.Increment(ref tornFrameDetected);
                            Volatile.Write(ref failedSequence, packet.HeadCanary);
                            break;
                        }

                        // 2. Monotonic order check per producer
                        int producerId = packet.ProducerId;
                        if (packet.Sequence != expectedPerProducer[producerId])
                        {
                            Interlocked.Increment(ref tornFrameDetected);
                            Volatile.Write(ref failedSequence, packet.HeadCanary);
                            break;
                        }

                        expectedPerProducer[producerId]++;
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
            NativeBoundedMpscRing<NativeStressPacket>.Free(ring);
        }
    }

    #endregion

    #region Test Structs

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NativeTestPayload
    {
        public ulong Payload;
        public int Id;
        public uint Checksum;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct NativeStressPacket
    {
        public long HeadCanary;
        public long Sequence;
        public long TailCanary;
        public int ProducerId;
        public int Reserved;
    }

    private struct NativeCollector : IValueConsumer<NativeTestPayload>
    {
        public readonly NativeTestPayload[] Items;
        public int Count;

        public NativeCollector(int maxItems)
        {
            Items = new NativeTestPayload[maxItems];
            Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(in NativeTestPayload value)
        {
            Items[Count++] = value;
            return true;
        }
    }

    #endregion
}
