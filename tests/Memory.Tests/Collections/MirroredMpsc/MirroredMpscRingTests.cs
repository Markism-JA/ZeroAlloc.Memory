using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Collections.MirroredMpsc;

namespace Memory.Tests.Collections.MirroredMpsc;

public class MirroredMpscRingTests
{
    private const int _testCapacity = 64 * 1024; // 64 KB
    #region 1. Basic Framing & Value-Copy Writes

    [Fact]
    public unsafe void TryWrite_And_TryRead_SingleFrame_PreservesPayload()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        Assert.True(ring.IsEmpty);

        Span<byte> payload = stackalloc byte[128];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i ^ 0x5A);

        fixed (byte* pPayload = payload)
        {
            bool writeSuccess = ring.TryWrite(pPayload, payload.Length);
            Assert.True(writeSuccess);
        }

        Assert.False(ring.IsEmpty);

        bool readSuccess = ring.TryRead(out BlockView view);
        Assert.True(readSuccess);
        Assert.Equal(payload.Length, view.Length);

        Span<byte> readSpan = new(view.Payload, view.Length);
        Assert.True(readSpan.SequenceEqual(payload));

        ring.AdvanceConsumer(in view);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public unsafe void TryWrite_DualSegments_CombinesContiguously()
    {
        using var ring = new MirroredMpscRing(_testCapacity);

        int headerValue = 0x12345678;
        long payloadValue = 0x0102030405060708;

        bool writeSuccess = ring.TryWrite(
            (byte*)&headerValue,
            sizeof(int),
            (byte*)&payloadValue,
            sizeof(long)
        );
        Assert.True(writeSuccess);

        bool readSuccess = ring.TryRead(out BlockView view);
        Assert.True(readSuccess);
        Assert.Equal(sizeof(int) + sizeof(long), view.Length);

        int readHeader = *(int*)view.Payload;
        long readPayload = *(long*)(view.Payload + sizeof(int));

        Assert.Equal(headerValue, readHeader);
        Assert.Equal(payloadValue, readPayload);

        ring.AdvanceConsumer(in view);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public unsafe void TryWrite_SpanAndGenericStruct_WritesCorrectly()
    {
        using var ring = new MirroredMpscRing(_testCapacity);

        ReadOnlySpan<byte> spanData = [0xDE, 0xAD, 0xBE, 0xEF];
        Assert.True(ring.TryWrite(spanData));

        var testStruct = new TestPacketHeader { Id = 42, Timestamp = 123456789L };
        Assert.True(ring.TryWrite(in testStruct));

        Assert.True(ring.TryRead(out BlockView view1));
        Assert.Equal(4, view1.Length);
        Assert.Equal(0xEFBEADDEu, *(uint*)view1.Payload);
        ring.AdvanceConsumer(in view1);

        Assert.True(ring.TryRead(out BlockView view2));
        Assert.Equal(sizeof(TestPacketHeader), view2.Length);
        ref readonly var actual = ref *(TestPacketHeader*)view2.Payload;
        Assert.Equal(42, actual.Id);
        Assert.Equal(123456789L, actual.Timestamp);
        ring.AdvanceConsumer(in view2);

        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 2. Metrics & State Introspection

    [Fact]
    public unsafe void Metrics_OccupiedAndFreeBytes_ReflectExactFramingAndAlignment()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        Assert.Equal(_testCapacity, ring.Capacity);
        Assert.Equal(0, ring.OccupiedBytes);
        Assert.Equal(_testCapacity, ring.FreeBytes);
        Assert.True(ring.IsEmpty);

        // 16B BlockHeader + 20B payload = 36B -> aligned to 8B = 40B total
        const int payloadSize = 20;
        Assert.True(ring.TryWrite(stackalloc byte[payloadSize]));

        Assert.Equal(40, ring.OccupiedBytes);
        Assert.Equal(_testCapacity - 40, ring.FreeBytes);
        Assert.False(ring.IsEmpty);

        Assert.True(ring.TryRead(out BlockView view));
        ring.AdvanceConsumer(in view);

        Assert.Equal(0, ring.OccupiedBytes);
        Assert.Equal(_testCapacity, ring.FreeBytes);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void Constructor_ScalesCapacityToValidPowerOfTwo()
    {
        using var ring = new MirroredMpscRing(500_000);
        long capacity = ring.Capacity;

        Assert.True(capacity >= 500_000);
        Assert.Equal(0L, capacity & (capacity - 1));
    }

    [Fact]
    public void Constructor_InvalidDimensions_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirroredMpscRing(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirroredMpscRing(-1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirroredMpscRing(0x40000001L));
    }

    #endregion

    #region 3. Virtual Memory Mirror Boundary Straddling

    [Fact]
    public unsafe void Write_StraddlingBufferBoundary_SucceedsWithoutWrapFragmentation()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        long ringCapacity = ring.Capacity;

        int initialPaddingSize = (int)(ringCapacity - 128 - sizeof(BlockHeader));
        byte* dummyBuffer = (byte*)NativeMemory.Alloc((nuint)initialPaddingSize);
        try
        {
            bool padWrite = ring.TryWrite(dummyBuffer, initialPaddingSize);
            Assert.True(padWrite);

            bool padRead = ring.TryRead(out BlockView padView);
            Assert.True(padRead);
            ring.AdvanceConsumer(in padView);
            Assert.True(ring.IsEmpty);
        }
        finally
        {
            NativeMemory.Free(dummyBuffer);
        }

        const int straddlePayloadSize = 256;
        Span<byte> straddleData = stackalloc byte[straddlePayloadSize];
        for (int i = 0; i < straddlePayloadSize; i++)
            straddleData[i] = (byte)(i & 0xFF);

        fixed (byte* pData = straddleData)
        {
            bool straddleWrite = ring.TryWrite(pData, straddlePayloadSize);
            Assert.True(straddleWrite);
        }

        bool readSuccess = ring.TryRead(out BlockView straddleView);
        Assert.True(readSuccess);
        Assert.Equal(straddlePayloadSize, straddleView.Length);

        Span<byte> actualSpan = new(straddleView.Payload, straddleView.Length);
        Assert.True(actualSpan.SequenceEqual(straddleData));

        ring.AdvanceConsumer(in straddleView);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public unsafe void WrapAround_WithAlternatingSizes_ShouldNotTriggerPhantomCommits()
    {
        using var ring = new MirroredMpscRing(4096);
        byte[] largePayload = new byte[512];
        new Random(42).NextBytes(largePayload);

        fixed (byte* pLarge = largePayload)
        {
            for (int i = 0; i < 8; i++)
            {
                Assert.True(ring.TryWrite(pLarge, largePayload.Length));
                Assert.True(ring.TryRead(out BlockView view));
                ring.AdvanceConsumer(in view);
            }

            for (int i = 0; i < 30; i++)
            {
                int smallVal = 0x12340000 + i;
                Assert.True(ring.TryWrite((byte*)&smallVal, sizeof(int)));

                Assert.True(ring.TryRead(out BlockView view));
                int readVal = *(int*)view.Payload;
                Assert.Equal(smallVal, readVal);
                ring.AdvanceConsumer(in view);
            }

            Assert.True(ring.IsEmpty);
        }
    }

    #endregion

    #region 4. Saturation & Backpressure

    [Fact]
    public unsafe void TryWrite_WhenRingIsSaturated_ReturnsFalseWithoutCorruption()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        long capacity = ring.Capacity;

        int maxPayload = (int)(capacity - sizeof(BlockHeader) - 8);
        byte* largePayload = (byte*)NativeMemory.Alloc((nuint)maxPayload);

        try
        {
            bool firstWrite = ring.TryWrite(largePayload, maxPayload);
            Assert.True(firstWrite);

            byte smallData = 0xFF;
            bool overflowWrite = ring.TryWrite(&smallData, 1);
            Assert.False(overflowWrite);

            bool readSuccess = ring.TryRead(out BlockView view);
            Assert.True(readSuccess);
            ring.AdvanceConsumer(in view);
            Assert.True(ring.IsEmpty);

            bool resumedWrite = ring.TryWrite(&smallData, 1);
            Assert.True(resumedWrite);
        }
        finally
        {
            NativeMemory.Free(largePayload);
        }
    }

    #endregion

    #region 5. Batch Draining & BatchReader Iteration

    [Fact]
    public unsafe void DrainBatch_DrainsAllCommittedFramesUpToMaxBatchSize()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        const int frameCount = 10;

        for (int i = 0; i < frameCount; i++)
        {
            int val = i;
            Assert.True(ring.TryWrite((byte*)&val, sizeof(int)));
        }

        var collector = new TestCollector(maxItems: 32);
        int drained = ring.DrainBatch(ref collector, maxBatchSize: 32);

        Assert.Equal(frameCount, drained);
        Assert.Equal(frameCount, collector.Count);

        for (int i = 0; i < frameCount; i++)
        {
            Assert.Equal(i, collector.ReceivedValues[i]);
        }

        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public unsafe void DrainBatch_ShortCircuitsWhenConsumerReturnsFalse()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        const int totalFrames = 8;
        const int stopAtFrame = 3;

        for (int i = 0; i < totalFrames; i++)
        {
            int val = i;
            ring.TryWrite((byte*)&val, sizeof(int));
        }

        var shortCircuitConsumer = new ShortCircuitConsumer(stopAfterCount: stopAtFrame);
        int drainedFirstPass = ring.DrainBatch(ref shortCircuitConsumer, maxBatchSize: 32);

        Assert.Equal(stopAtFrame, drainedFirstPass);
        Assert.False(ring.IsEmpty);

        var drainRest = new TestCollector(maxItems: 32);
        int drainedSecondPass = ring.DrainBatch(ref drainRest, maxBatchSize: 32);

        Assert.Equal(totalFrames - stopAtFrame, drainedSecondPass);
        Assert.Equal(3, drainRest.ReceivedValues[0]);
        Assert.Equal(7, drainRest.ReceivedValues[drainRest.Count - 1]);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public unsafe void BeginBatch_BatchReader_IteratesAndCommitsOnDispose()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        const int frameCount = 5;

        for (int i = 0; i < frameCount; i++)
        {
            int val = i + 1;
            Assert.True(ring.TryWrite((byte*)&val, sizeof(int)));
        }

        using (var reader = ring.BeginBatch(maxBatchSize: 10))
        {
            int expected = 1;
            while (reader.TryReadNext(out BlockView view))
            {
                Assert.Equal(expected++, *(int*)view.Payload);
            }
            Assert.Equal(frameCount, reader.ReadCount);
        }

        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public unsafe void BeginBatch_BatchReader_Abort_DoesNotAdvanceHead()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        int val = 999;
        Assert.True(ring.TryWrite((byte*)&val, sizeof(int)));

        using (var reader = ring.BeginBatch(maxBatchSize: 10))
        {
            Assert.True(reader.TryReadNext(out BlockView view));
            Assert.Equal(999, *(int*)view.Payload);
            reader.Abort();
        }

        Assert.False(ring.IsEmpty);
        Assert.True(ring.TryRead(out BlockView peeked));
        Assert.Equal(999, *(int*)peeked.Payload);
        ring.AdvanceConsumer(in peeked);
        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 6. Multi-Threaded Concurrent Producer Stress Tests (Isolated await)

    [Fact]
    public async Task MpscConcurrentStress_MultiProducer_GuaranteesDataIntegrity()
    {
        const int producerCount = 4;
        const int itemsPerProducer = 25_000;
        const int totalExpectedItems = producerCount * itemsPerProducer;

        using var ring = new MirroredMpscRing(8 * 1024 * 1024);
        var collectionRegistry = new ConcurrentBag<int>();

        using var startSignal = new ManualResetEventSlim(false);
        using var producersCompleted = new CountdownEvent(producerCount);

        Task[] producerTasks = new Task[producerCount];
        for (int t = 0; t < producerCount; t++)
        {
            int threadId = t;
            // Unsafe code is fully isolated inside the Task.Run Action closure
            producerTasks[t] = Task.Run(() =>
            {
                startSignal.Wait();
                int markerData = threadId * 100_000;

                unsafe
                {
                    for (int i = 0; i < itemsPerProducer; i++)
                    {
                        int val = markerData + i;
                        while (!ring.TryWrite((byte*)&val, sizeof(int)))
                        {
                            Thread.SpinWait(5);
                        }
                    }
                }
                producersCompleted.Signal();
            });
        }

        var consumerTask = Task.Run(() =>
        {
            startSignal.Wait();
            int itemsHarvested = 0;

            unsafe
            {
                while (itemsHarvested < totalExpectedItems)
                {
                    if (ring.TryRead(out BlockView view))
                    {
                        int val = *(int*)view.Payload;
                        collectionRegistry.Add(val);
                        ring.AdvanceConsumer(in view);
                        itemsHarvested++;
                    }
                    else
                    {
                        Thread.SpinWait(2);
                    }
                }
            }
        });

        startSignal.Set();

        // Safely await: no pointers exist in the state machine frame
        await Task.WhenAll(producerTasks);
        await consumerTask;

        Assert.Equal(totalExpectedItems, collectionRegistry.Count);
        Assert.True(ring.IsEmpty);

        var trackingArray = collectionRegistry.ToArray();
        for (int t = 0; t < producerCount; t++)
        {
            int threadPrefix = t * 100_000;
            int count = 0;
            foreach (int value in trackingArray)
            {
                if (value >= threadPrefix && value < threadPrefix + itemsPerProducer)
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

        using var ring = new MirroredMpscRing(8 * 1024 * 1024);
        var collectionRegistry = new ConcurrentBag<int>();

        using var startSignal = new ManualResetEventSlim(false);
        using var producersCompleted = new CountdownEvent(producerCount);

        Task[] producerTasks = new Task[producerCount];
        for (int t = 0; t < producerCount; t++)
        {
            int threadId = t;
            producerTasks[t] = Task.Run(() =>
            {
                startSignal.Wait();
                int markerData = threadId * 100_000;

                unsafe
                {
                    for (int i = 0; i < itemsPerProducer; i++)
                    {
                        int val = markerData + i;
                        while (!ring.TryWrite((byte*)&val, sizeof(int)))
                        {
                            Thread.SpinWait(5);
                        }
                    }
                }
                producersCompleted.Signal();
            });
        }

        var consumerTask = Task.Run(() =>
        {
            startSignal.Wait();
            int itemsHarvested = 0;
            var consumer = new ConcurrentBagCollectorConsumer(collectionRegistry);

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

        // Safely await: completely decoupled from unmanaged pointer contexts
        await Task.WhenAll(producerTasks);
        await consumerTask;

        Assert.Equal(totalExpectedItems, collectionRegistry.Count);
        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 7. Lifecycle & Native Allocator Verification

    [Fact]
    public unsafe void DangerousGetNativePointer_ReturnsValidPointer()
    {
        using var ring = new MirroredMpscRing(_testCapacity);
        NativeMirroredMpscRing* native = ring.DangerousGetNativePointer();

        Assert.True(native != null);
        Assert.Equal(_testCapacity, native->Capacity);
        Assert.True(native->IsEmpty);
    }

    [Fact]
    public unsafe void Dispose_IsIdempotent_And_ThrowsWhenUsedPostDispose()
    {
        var ring = new MirroredMpscRing(_testCapacity);
        ring.Dispose();
        ring.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ring.DangerousGetNativePointer());
        Assert.Throws<ObjectDisposedException>(() => ring.BeginBatch());
    }

    #endregion

    #region Test Structs & Consumers

    [StructLayout(LayoutKind.Sequential)]
    private struct TestPacketHeader
    {
        public int Id;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct TestCollector : IBlockConsumer
    {
        public readonly int[] ReceivedValues;
        public int Count;

        public TestCollector(int maxItems)
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

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShortCircuitConsumer(int stopAfterCount) : IBlockConsumer
    {
        private int _consumed = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(byte* payload, int length)
        {
            if (_consumed >= stopAfterCount)
                return false;

            _consumed++;
            return true;
        }
    }

    private readonly unsafe struct ConcurrentBagCollectorConsumer(ConcurrentBag<int> bag)
        : IBlockConsumer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(byte* payload, int length)
        {
            bag.Add(*(int*)payload);
            return true;
        }
    }

    #endregion
}
