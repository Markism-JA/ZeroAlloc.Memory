using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Collections.BoundedMpsc;

namespace Memory.Tests.Collections.BoundedMpsc;

public sealed class BoundedMpscRingTests
{
    private const int _defaultCapacity = 4096;

    #region 1. Basic Enqueue & Dequeue (Value Preservation)

    [Fact]
    public void TryWrite_And_TryRead_SingleItem_PreservesPayload()
    {
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(_defaultCapacity);
        Assert.True(ring.IsEmpty);

        var item = new TestTelemetryEnvelope
        {
            TimestampNs = 123456789L,
            TemplateId = 0xAA55,
            NodeId = 7,
            Level = 2,
            Arg0 = 0x1122334455667788UL,
            Arg1 = 0x99AABBCCDDEEFF00UL,
        };

        bool writeOk = ring.TryWrite(in item);
        Assert.True(writeOk);
        Assert.False(ring.IsEmpty);

        bool readOk = ring.TryRead(out TestTelemetryEnvelope actual);
        Assert.True(readOk);
        Assert.Equal(item.TimestampNs, actual.TimestampNs);
        Assert.Equal(item.TemplateId, actual.TemplateId);
        Assert.Equal(item.NodeId, actual.NodeId);
        Assert.Equal(item.Level, actual.Level);
        Assert.Equal(item.Arg0, actual.Arg0);
        Assert.Equal(item.Arg1, actual.Arg1);

        Assert.True(ring.IsEmpty);
        Assert.False(ring.TryRead(out _));
    }

    [Fact]
    public void Enqueue_Dequeue_Alternating_AdvancesAcrossMultipleLaps()
    {
        // Small capacity to force multiple laps around the ring
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(4);
        const int iterations = 100;

        for (int i = 0; i < iterations; i++)
        {
            var item = new TestTelemetryEnvelope { TimestampNs = i, TemplateId = (uint)i };
            Assert.True(ring.TryWrite(in item));
            Assert.True(ring.TryRead(out TestTelemetryEnvelope read));
            Assert.Equal(i, read.TimestampNs);
        }

        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 2. Capacity Sizing & Validation

    [Fact]
    public void Constructor_ScalesCapacityToValidPowerOfTwo()
    {
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(5000);
        long capacity = ring.Capacity;

        Assert.Equal(8192, capacity);
        Assert.Equal(0L, capacity & (capacity - 1));
    }

    [Fact]
    public void Constructor_InvalidDimensions_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedMpscRing<TestTelemetryEnvelope>(0)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedMpscRing<TestTelemetryEnvelope>(-1024)
        );
    }

    [Fact]
    public void Constructor_ExceedingIntMaxValueBytes_ThrowsArgumentOutOfRangeException()
    {
        // 67,108,864 elements * 48B slot size = 3,221,225,472 bytes (> 2GB int.MaxValue)
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedMpscRing<TestTelemetryEnvelope>(67_108_864)
        );
    }

    #endregion

    #region 3. Metrics & Backpressure Saturation

    [Fact]
    public void Metrics_OccupiedAndFreeCount_ReflectExactState()
    {
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(16);
        Assert.Equal(16, ring.Capacity);
        Assert.Equal(0, ring.OccupiedCount);
        Assert.Equal(16, ring.FreeCount);
        Assert.True(ring.IsEmpty);

        for (int i = 0; i < 5; i++)
        {
            var item = new TestTelemetryEnvelope { TimestampNs = i };
            Assert.True(ring.TryWrite(in item));
        }

        Assert.Equal(5, ring.OccupiedCount);
        Assert.Equal(11, ring.FreeCount);
        Assert.False(ring.IsEmpty);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(ring.TryRead(out _));
        }

        Assert.Equal(0, ring.OccupiedCount);
        Assert.Equal(16, ring.FreeCount);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void Backpressure_WhenSaturated_ReturnsFalseWithoutCorruption()
    {
        const int capacity = 8;
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(capacity);

        // Fill queue to capacity
        for (int i = 0; i < capacity; i++)
        {
            var item = new TestTelemetryEnvelope { TimestampNs = i };
            Assert.True(ring.TryWrite(in item));
        }

        // Must reject additional writes
        var overflowItem = new TestTelemetryEnvelope { TimestampNs = 999 };
        Assert.False(ring.TryWrite(in overflowItem));

        // Drain one element to free a slot
        Assert.True(ring.TryRead(out TestTelemetryEnvelope first));
        Assert.Equal(0, first.TimestampNs);

        // Subsequent write must now succeed
        Assert.True(ring.TryWrite(in overflowItem));

        // Read remaining elements
        for (int i = 1; i < capacity; i++)
        {
            Assert.True(ring.TryRead(out TestTelemetryEnvelope item));
            Assert.Equal(i, item.TimestampNs);
        }

        Assert.True(ring.TryRead(out TestTelemetryEnvelope last));
        Assert.Equal(999, last.TimestampNs);
        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 4. Batch Draining & BatchReader Iteration

    [Fact]
    public void DrainBatch_DrainsAllCommittedItemsUpToMaxBatchSize()
    {
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(_defaultCapacity);
        const int count = 10;

        for (int i = 0; i < count; i++)
        {
            var item = new TestTelemetryEnvelope { TimestampNs = i };
            Assert.True(ring.TryWrite(in item));
        }

        var consumer = new TestEnvelopeCollector(maxItems: 32);
        int drained = ring.DrainBatch(ref consumer, maxBatchSize: 32);

        Assert.Equal(count, drained);
        Assert.Equal(count, consumer.Count);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, consumer.Items[i].TimestampNs);
        }

        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void DrainBatch_ShortCircuitsWhenConsumerReturnsFalse()
    {
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(_defaultCapacity);
        const int totalItems = 8;
        const int haltAt = 3;

        for (int i = 0; i < totalItems; i++)
        {
            var item = new TestTelemetryEnvelope { TimestampNs = i };
            ring.TryWrite(in item);
        }

        var shortCircuitConsumer = new ShortCircuitEnvelopeConsumer(haltAtCount: haltAt);
        int firstDrain = ring.DrainBatch(ref shortCircuitConsumer, maxBatchSize: 32);

        Assert.Equal(haltAt, firstDrain);
        Assert.False(ring.IsEmpty);

        var remainingConsumer = new TestEnvelopeCollector(maxItems: 32);
        int secondDrain = ring.DrainBatch(ref remainingConsumer, maxBatchSize: 32);

        Assert.Equal(totalItems - haltAt, secondDrain);
        Assert.Equal(haltAt, remainingConsumer.Items[0].TimestampNs);
        Assert.Equal(totalItems - 1, remainingConsumer.Items[secondDrain - 1].TimestampNs);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void BeginBatch_BatchReader_IteratesAndCommitsOnDispose()
    {
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(_defaultCapacity);
        const int count = 5;

        for (int i = 0; i < count; i++)
        {
            var item = new TestTelemetryEnvelope { TimestampNs = i + 1 };
            Assert.True(ring.TryWrite(in item));
        }

        using (var reader = ring.BeginBatch(maxBatchSize: 10))
        {
            long expected = 1;
            while (reader.TryReadNext(out TestTelemetryEnvelope item))
            {
                Assert.Equal(expected++, item.TimestampNs);
            }
            Assert.Equal(count, reader.ReadCount);
        }

        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 5. Multi-Threaded Concurrent Producer Stress Tests

    [Fact]
    public async Task MpscConcurrentStress_MultiProducer_GuaranteesDataIntegrity()
    {
        const int producerCount = 4;
        const int itemsPerProducer = 25_000;
        const int totalExpectedItems = producerCount * itemsPerProducer;

        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(131_072);
        var collectionRegistry = new ConcurrentBag<long>();

        using var startSignal = new ManualResetEventSlim(false);
        using var producersCompleted = new CountdownEvent(producerCount);

        Task[] producerTasks = new Task[producerCount];
        for (int t = 0; t < producerCount; t++)
        {
            int threadId = t;
            producerTasks[t] = Task.Run(() =>
            {
                startSignal.Wait();
                long markerBase = (long)threadId * 1_000_000L;

                for (int i = 0; i < itemsPerProducer; i++)
                {
                    var item = new TestTelemetryEnvelope
                    {
                        TimestampNs = markerBase + i,
                        NodeId = (ushort)threadId,
                    };

                    while (!ring.TryWrite(in item))
                    {
                        Thread.SpinWait(5);
                    }
                }

                producersCompleted.Signal();
            });
        }

        var consumerTask = Task.Run(() =>
        {
            startSignal.Wait();
            int itemsHarvested = 0;

            while (itemsHarvested < totalExpectedItems)
            {
                if (ring.TryRead(out TestTelemetryEnvelope item))
                {
                    collectionRegistry.Add(item.TimestampNs);
                    itemsHarvested++;
                }
                else
                {
                    Thread.SpinWait(2);
                }
            }
        });

        startSignal.Set();

        await Task.WhenAll(producerTasks);
        await consumerTask;

        Assert.Equal(totalExpectedItems, collectionRegistry.Count);
        Assert.True(ring.IsEmpty);

        var trackingArray = collectionRegistry.ToArray();
        for (int t = 0; t < producerCount; t++)
        {
            long threadPrefix = (long)t * 1_000_000L;
            int count = 0;
            foreach (long val in trackingArray)
            {
                if (val >= threadPrefix && val < threadPrefix + itemsPerProducer)
                    count++;
            }
            Assert.Equal(itemsPerProducer, count);
        }
    }

    [Fact]
    public async Task MpscConcurrentStress_WithDrainBatch_GuaranteesBatchAtomicity()
    {
        const int producerCount = 4;
        const int itemsPerProducer = 25_000;
        const int totalExpectedItems = producerCount * itemsPerProducer;

        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(131_072);
        var collectionRegistry = new ConcurrentBag<long>();

        using var startSignal = new ManualResetEventSlim(false);
        using var producersCompleted = new CountdownEvent(producerCount);

        Task[] producerTasks = new Task[producerCount];
        for (int t = 0; t < producerCount; t++)
        {
            int threadId = t;
            producerTasks[t] = Task.Run(() =>
            {
                startSignal.Wait();
                long markerBase = (long)threadId * 1_000_000L;

                for (int i = 0; i < itemsPerProducer; i++)
                {
                    var item = new TestTelemetryEnvelope
                    {
                        TimestampNs = markerBase + i,
                        NodeId = (ushort)threadId,
                    };

                    while (!ring.TryWrite(in item))
                    {
                        Thread.SpinWait(5);
                    }
                }

                producersCompleted.Signal();
            });
        }

        var consumerTask = Task.Run(() =>
        {
            startSignal.Wait();
            int itemsHarvested = 0;
            var consumer = new ConcurrentBagEnvelopeCollector(collectionRegistry);

            while (itemsHarvested < totalExpectedItems)
            {
                int drained = ring.DrainBatch(ref consumer, maxBatchSize: 32);
                itemsHarvested += drained;

                if (drained == 0 && !producersCompleted.IsSet)
                {
                    Thread.SpinWait(2);
                }
            }
        });

        startSignal.Set();

        await Task.WhenAll(producerTasks);
        await consumerTask;

        Assert.Equal(totalExpectedItems, collectionRegistry.Count);
        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 6. Lifecycle & Interop

    [Fact]
    public unsafe void DangerousGetNativePointer_ReturnsValidPointer()
    {
        using var ring = new BoundedMpscRing<TestTelemetryEnvelope>(_defaultCapacity);
        NativeBoundedMpscRing<TestTelemetryEnvelope>* native = ring.DangerousGetNativePointer();

        Assert.True(native != null);
        Assert.Equal(_defaultCapacity, native->Capacity);
        Assert.True(native->IsEmpty);
    }

    [Fact]
    public unsafe void Dispose_IsIdempotent_And_ThrowsWhenUsedPostDispose()
    {
        var ring = new BoundedMpscRing<TestTelemetryEnvelope>(_defaultCapacity);
        ring.Dispose();
        ring.Dispose(); // Safe no-op

        var item = new TestTelemetryEnvelope();
        Assert.Throws<ObjectDisposedException>(() => ring.TryWrite(in item));
        Assert.Throws<ObjectDisposedException>(() => ring.DangerousGetNativePointer());
        Assert.Throws<ObjectDisposedException>(() => ring.BeginBatch());
    }

    #endregion

    #region Test Structs & Consumers

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct TestTelemetryEnvelope
    {
        public long TimestampNs;
        public ulong Arg0;
        public ulong Arg1;
        public uint TemplateId;
        public ushort NodeId;
        public byte Level;
        public byte Reserved;
    }

    private struct TestEnvelopeCollector(int maxItems) : IValueConsumer<TestTelemetryEnvelope>
    {
        public readonly TestTelemetryEnvelope[] Items = new TestTelemetryEnvelope[maxItems];
        public int Count = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(in TestTelemetryEnvelope value)
        {
            Items[Count++] = value;
            return true;
        }
    }

    private struct ShortCircuitEnvelopeConsumer(int haltAtCount)
        : IValueConsumer<TestTelemetryEnvelope>
    {
        private int _consumed = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(in TestTelemetryEnvelope value)
        {
            if (_consumed >= haltAtCount)
                return false;

            _consumed++;
            return true;
        }
    }

    private readonly struct ConcurrentBagEnvelopeCollector(ConcurrentBag<long> bag)
        : IValueConsumer<TestTelemetryEnvelope>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(in TestTelemetryEnvelope value)
        {
            bag.Add(value.TimestampNs);
            return true;
        }
    }

    #endregion
}
