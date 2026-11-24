using Memory.Collections.Spsc;

namespace Memory.Tests.Collections.Spsc;

public class SpscRingTests
{
    private unsafe struct PacketPayload
    {
        public ulong SequenceId;
        public int OpCode;
        public fixed byte Data[16];
    }

    [Fact]
    public void Constructor_ShouldAutoInitializeAndScaleToValidPowerOfTwo()
    {
        using var ring = new SpscRing<int>(5000);

        int capacity = ring.Capacity;

        Assert.True(capacity >= 5000, "Ring failed to scale capacity up to match requirements.");
        Assert.Equal(0, capacity & (capacity - 1)); // Enforce power-of-two verification
        Assert.Equal(8192, capacity);
    }

    [Fact]
    public void Constructor_ShouldThrowExceptions_OnInvalidDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpscRing<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpscRing<int>(-1024));
    }

    [Fact]
    public void SequentialEnqueueAndDequeue_ShouldPreserveDataIntegrity()
    {
        using var ring = new SpscRing<int>(1024);

        for (int i = 0; i < 500; i++)
        {
            Assert.True(ring.TryEnqueue(i), $"Failed to enqueue item: {i}");
        }

        for (int i = 0; i < 500; i++)
        {
            Assert.True(ring.TryDequeue(out int value), $"Failed to dequeue item: {i}");
            Assert.Equal(i, value);
        }

        Assert.False(ring.TryDequeue(out _), "Reader reported active frames in an empty queue.");
    }

    [Fact]
    public unsafe void CustomUnmanagedStructPayload_ShouldPreserveFieldAlignmentAndData()
    {
        using var ring = new SpscRing<PacketPayload>(64);

        var original = new PacketPayload { SequenceId = 0xDEADBEEFCAFEBABEUL, OpCode = 42 };
        original.Data[0] = 0xAA;
        original.Data[15] = 0xFF;

        Assert.True(ring.TryEnqueue(in original));
        Assert.True(ring.TryDequeue(out PacketPayload result));

        Assert.Equal(original.SequenceId, result.SequenceId);
        Assert.Equal(original.OpCode, result.OpCode);
        Assert.Equal(0xAA, result.Data[0]);
        Assert.Equal(0xFF, result.Data[15]);
    }

    [Fact]
    public void RingBuffer_ShouldHandleContinuousWrapAroundBoundaryCrossing()
    {
        const int capacity = 64;
        using var ring = new SpscRing<int>(capacity);

        for (int cycle = 0; cycle < 1000; cycle++)
        {
            Assert.True(ring.TryEnqueue(cycle), $"Failed to write entry at cycle index: {cycle}");
            Assert.True(
                ring.TryDequeue(out int item),
                $"Failed to read entry at cycle index: {cycle}"
            );
            Assert.Equal(cycle, item);
        }

        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void BackpressureGates_ShouldSignalFalse_WhenRingCapacitySaturates()
    {
        const int capacity = 128;
        using var ring = new SpscRing<int>(capacity);

        for (int i = 0; i < capacity; i++)
        {
            Assert.True(ring.TryEnqueue(i), $"Failed on valid boundary write index: {i}");
        }

        Assert.False(
            ring.TryEnqueue(9999),
            "Ring allowed allocation past saturated buffer boundaries."
        );

        // Relieve pressure by dequeuing one item and re-test
        Assert.True(ring.TryDequeue(out int firstItem));
        Assert.Equal(0, firstItem);

        Assert.True(ring.TryEnqueue(9999), "Ring failed to allow write after freeing a slot.");
    }

    // =========================================================================
    //  ZERO-COPY TWO-PHASE WRITE API TESTS
    // =========================================================================

    [Fact]
    public unsafe void ZeroCopyWritePointer_ShouldAllowInPlaceMutationAndCommit()
    {
        using var ring = new SpscRing<PacketPayload>(64);

        Assert.True(ring.TryGetWritePointer(out PacketPayload* slot));
        slot->SequenceId = 0x1234567890ABCDEFUL;
        slot->OpCode = 99;
        slot->Data[0] = 0xBE;
        slot->Data[15] = 0xEF;

        ring.CommitWrite();

        Assert.True(ring.TryDequeue(out PacketPayload read));
        Assert.Equal(0x1234567890ABCDEFUL, read.SequenceId);
        Assert.Equal(99, read.OpCode);
        Assert.Equal(0xBE, read.Data[0]);
        Assert.Equal(0xEF, read.Data[15]);
    }

    [Fact]
    public unsafe void ZeroCopyWritePointer_ShouldReturnFalseAndNull_WhenBufferIsFull()
    {
        const int capacity = 32;
        using var ring = new SpscRing<int>(capacity);

        for (int i = 0; i < capacity; i++)
        {
            Assert.True(ring.TryGetWritePointer(out int* slot));
            *slot = i;
            ring.CommitWrite();
        }

        Assert.False(
            ring.TryGetWritePointer(out int* overflowSlot),
            "Zero-copy write pointer was granted on a fully saturated ring."
        );
        Assert.True(
            overflowSlot == null,
            "Destination pointer must be null on failed reservation."
        );
    }

    // =========================================================================
    //  BATCH ENQUEUE (EnqueueBatch) & DRAIN TESTS
    // =========================================================================

    [Fact]
    public void EnqueueBatch_ShouldCorrectlyTransferContiguousAndWrappedBatches()
    {
        const int capacity = 64;
        using var ring = new SpscRing<int>(capacity);

        // 1. Shift head/tail near the wrap boundary
        for (int i = 0; i < 50; i++)
        {
            ring.TryEnqueue(i);
            ring.TryDequeue(out _);
        }

        // 2. Batch enqueue across wrap boundary (tail: 50 -> 50 + 30 = 80 % 64 = index 16)
        Span<int> source = stackalloc int[30];
        for (int i = 0; i < 30; i++)
        {
            source[i] = 1000 + i;
        }

        int written = ring.EnqueueBatch(source);
        Assert.Equal(30, written);

        // 3. Drain in a single batch across the wrap boundary
        Span<int> destination = stackalloc int[30];
        int drained = ring.Drain(destination);
        Assert.Equal(30, drained);

        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(1000 + i, destination[i]);
        }

        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void EnqueueBatch_ShouldHandleEmptyAndSaturationClamping()
    {
        const int capacity = 64;
        using var ring = new SpscRing<int>(capacity);

        // Empty span write safe no-op
        Assert.Equal(0, ring.EnqueueBatch(ReadOnlySpan<int>.Empty));

        // Pre-fill 50 slots (14 slots free)
        for (int i = 0; i < 50; i++)
        {
            ring.TryEnqueue(i);
        }

        // Attempt writing 20 items (should clamp to 14)
        Span<int> oversizedBatch = stackalloc int[20];
        for (int i = 0; i < 20; i++)
        {
            oversizedBatch[i] = 500 + i;
        }

        int written = ring.EnqueueBatch(oversizedBatch);
        Assert.Equal(14, written);

        // Saturated ring should write 0
        Assert.Equal(0, ring.EnqueueBatch(oversizedBatch));

        // Verify total readable elements is exactly capacity
        Span<int> drainAll = stackalloc int[capacity];
        Assert.Equal(capacity, ring.Drain(drainAll));

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(i, drainAll[i]);
        }
        for (int i = 0; i < 14; i++)
        {
            Assert.Equal(500 + i, drainAll[50 + i]);
        }
    }

    [Fact]
    public void Drain_ShouldCorrectlyTransferContiguousAndWrappedBatches()
    {
        const int capacity = 64;
        using var ring = new SpscRing<int>(capacity);

        // 1. Shift head/tail near physical end
        for (int i = 0; i < 50; i++)
        {
            Assert.True(ring.TryEnqueue(i));
            Assert.True(ring.TryDequeue(out _));
        }

        // 2. Enqueue scalar items crossing wrap boundary
        const int itemCount = 30;
        for (int i = 0; i < itemCount; i++)
        {
            Assert.True(ring.TryEnqueue(100 + i));
        }

        // 3. Drain wrapped batch
        Span<int> drainTarget = stackalloc int[itemCount];
        int drained = ring.Drain(drainTarget);

        Assert.Equal(itemCount, drained);
        for (int i = 0; i < itemCount; i++)
        {
            Assert.Equal(100 + i, drainTarget[i]);
        }

        Assert.False(ring.TryDequeue(out _), "Ring should be empty after full drain.");
    }

    [Fact]
    public void Drain_ShouldHandleEmptyAndPartialDestinationSpans()
    {
        using var ring = new SpscRing<int>(64);

        for (int i = 0; i < 20; i++)
        {
            ring.TryEnqueue(i);
        }

        // Empty destination buffer
        Assert.Equal(0, ring.Drain(Span<int>.Empty));

        // Partial drain
        Span<int> smallBuffer = stackalloc int[5];
        int drained = ring.Drain(smallBuffer);

        Assert.Equal(5, drained);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i, smallBuffer[i]);
        }

        // Drain remainder
        Span<int> remainderBuffer = stackalloc int[20];
        int remainderDrained = ring.Drain(remainderBuffer);

        Assert.Equal(15, remainderDrained);
        for (int i = 0; i < 15; i++)
        {
            Assert.Equal(5 + i, remainderBuffer[i]);
        }
    }

    // =========================================================================
    //  LIFECYCLE & DISPOSAL TESTS
    // =========================================================================

    [Fact]
    public void Dispose_ShouldBeIdempotentAndSafe()
    {
        var ring = new SpscRing<int>(64);
        ring.TryEnqueue(1);

        ring.Dispose();
        ring.Dispose(); // Multiple disposal calls must not fault
    }

    // =========================================================================
    //  CONCURRENT MULTITHREADED STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task SpscConcurrentZeroCopyPipeline_ShouldMaintainDataConsistency()
    {
        const int totalItems = 1_000_000;
        using var ring = new SpscRing<int>(16384);

        var startSignal = new ManualResetEventSlim(false);

        var producerTask = Task.Run(() =>
        {
            startSignal.Wait();

            for (int i = 0; i < totalItems; i++)
            {
                unsafe
                {
                    int* slot;
                    while (!ring.TryGetWritePointer(out slot))
                    {
                        Thread.SpinWait(4);
                    }

                    *slot = i;
                    ring.CommitWrite();
                }
            }
        });

        var consumerTask = Task.Run(() =>
        {
            startSignal.Wait();

            int expectedSequence = 0;
            while (expectedSequence < totalItems)
            {
                if (ring.TryDequeue(out int val))
                {
                    if (val != expectedSequence)
                    {
                        throw new InvalidOperationException(
                            $"Corruption in zero-copy pipeline! Expected {expectedSequence} but received {val}."
                        );
                    }
                    expectedSequence++;
                }
                else
                {
                    Thread.SpinWait(4);
                }
            }
        });

        startSignal.Set();
        await Task.WhenAll(producerTask, consumerTask);
    }

    [Fact]
    public async Task SpscConcurrentBatchIngestAndDrainStressTest_ShouldMaintainOrderUnderBurstLoad()
    {
        const int totalItems = 1_000_000;
        const int batchSize = 64;
        using var ring = new SpscRing<int>(16384);

        var startSignal = new ManualResetEventSlim(false);

        var producerTask = Task.Run(() =>
        {
            startSignal.Wait();

            int current = 0;
            int[] buffer = new int[batchSize];

            while (current < totalItems)
            {
                int toSend = Math.Min(batchSize, totalItems - current);
                for (int i = 0; i < toSend; i++)
                {
                    buffer[i] = current + i;
                }

                int written = ring.EnqueueBatch(new ReadOnlySpan<int>(buffer, 0, toSend));
                if (written > 0)
                {
                    current += written;
                }
                else
                {
                    Thread.SpinWait(4);
                }
            }
        });

        var consumerTask = Task.Run(() =>
        {
            startSignal.Wait();

            int expectedSequence = 0;
            int[] drainBuffer = new int[batchSize];

            while (expectedSequence < totalItems)
            {
                int drained = ring.Drain(drainBuffer.AsSpan());
                if (drained > 0)
                {
                    for (int i = 0; i < drained; i++)
                    {
                        if (drainBuffer[i] != expectedSequence)
                        {
                            throw new InvalidOperationException(
                                $"Batch pipeline corruption! Expected {expectedSequence} but got {drainBuffer[i]}."
                            );
                        }
                        expectedSequence++;
                    }
                }
                else
                {
                    Thread.SpinWait(4);
                }
            }
        });

        startSignal.Set();
        await Task.WhenAll(producerTask, consumerTask);
    }
}
