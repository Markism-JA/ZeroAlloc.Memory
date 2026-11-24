using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Collections.MirroredMpsc;
using Memory.Collections.MirroredSpsc;

namespace Memory.Tests.Collections.MirrroredSpsc;

public unsafe class MirroredSpscRingTests
{
    private const int _testCapacity = 64 * 1024; // 64 KB (Windows & Linux compatible)
    #region 1. Basic Framing & Value-Copy Writes

    [Fact]
    public void TryWrite_And_TryRead_SingleFrame_PreservesPayload()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
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
    public void TryWrite_DualSegments_CombinesContiguously()
    {
        using var ring = new MirroredSpscRing(_testCapacity);

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
    public void TryWrite_SpanAndGenericStruct_WritesCorrectly()
    {
        using var ring = new MirroredSpscRing(_testCapacity);

        // 1. Test ReadOnlySpan<byte> overload
        ReadOnlySpan<byte> spanData = [0xDE, 0xAD, 0xBE, 0xEF];
        Assert.True(ring.TryWrite(spanData));

        // 2. Test generic unmanaged struct overload
        var testStruct = new TestPacketHeader { Id = 42, Timestamp = 123456789L };
        Assert.True(ring.TryWrite(in testStruct));

        // Validate span frame
        Assert.True(ring.TryRead(out BlockView view1));
        Assert.Equal(4, view1.Length);
        Assert.Equal(0xEFBEADDEu, *(uint*)view1.Payload);
        ring.AdvanceConsumer(in view1);

        // Validate struct frame
        Assert.True(ring.TryRead(out BlockView view2));
        Assert.Equal(sizeof(TestPacketHeader), view2.Length);
        ref readonly var actual = ref *(TestPacketHeader*)view2.Payload;
        Assert.Equal(42, actual.Id);
        Assert.Equal(123456789L, actual.Timestamp);
        ring.AdvanceConsumer(in view2);

        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 2. Two-Phase Zero-Copy (Reservation & Abort)

    [Fact]
    public void TryReserve_ManagedScope_AutoCommitsOnDispose()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        const int payloadLength = 32;

        Assert.True(ring.TryReserve(payloadLength, out SpscWriteScope scope));
        using (scope)
        {
            Assert.Equal(payloadLength, scope.Span.Length);
            scope.Span.Fill(0xAA);
        } // Auto-commits to _tail here

        Assert.False(ring.IsEmpty);
        Assert.True(ring.TryRead(out BlockView view));
        Assert.Equal(payloadLength, view.Length);
        Assert.Equal(0xAA, *view.Payload);
        Assert.Equal(0xAA, *(view.Payload + payloadLength - 1));

        ring.AdvanceConsumer(in view);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void TryReserve_ManagedScope_Abort_DoesNotAdvanceTail()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        long initialTail = ring.TailSequence;

        Assert.True(ring.TryReserve(64, out SpscWriteScope scope));
        using (scope)
        {
            scope.Span.Fill(0x55);
            scope.Abort(); // Disarm auto-commit
        }

        // Tail must remain untouched; ring must remain empty
        Assert.Equal(initialTail, ring.TailSequence);
        Assert.True(ring.IsEmpty);
        Assert.False(ring.TryRead(out _));

        // Subsequent write should succeed at the exact same tail position
        Assert.True(ring.TryWrite([0x01, 0x02, 0x03, 0x04]));
        Assert.True(ring.TryRead(out BlockView view));
        Assert.Equal(4, view.Length);
        Assert.Equal(0x01, *view.Payload);
        ring.AdvanceConsumer(in view);
    }

    [Fact]
    public void TryReserve_NativeRawTicket_CommitsExplicitly()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        NativeMirroredSpscRing* native = ring.DangerousGetNativePointer();

        const int payloadSize = 16;
        Assert.True(native->TryReserve(payloadSize, out byte* dest, out SpscWriteTicket ticket));
        Assert.True(dest != null);

        *(ulong*)dest = 0x1122334455667788UL;
        *(ulong*)(dest + 8) = 0x99AABBCCDDEEFF00UL;

        // Frame is not yet committed to consumer
        Assert.True(native->IsEmpty);

        native->CommitWrite(in ticket);

        // Frame is now published
        Assert.False(native->IsEmpty);
        Assert.True(native->TryRead(out BlockView view));
        Assert.Equal(payloadSize, view.Length);
        Assert.Equal(0x1122334455667788UL, *(ulong*)view.Payload);
        Assert.Equal(0x99AABBCCDDEEFF00UL, *(ulong*)(view.Payload + 8));

        native->AdvanceConsumer(in view);
        Assert.True(native->IsEmpty);
    }

    #endregion

    #region 3. Amortized Batch Writes (Working Tail)

    [Fact]
    public void AmortizedBatch_AccumulatesWithoutStoreRelease_UntilCommitBatch()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        long initialTail = ring.GetProducerTail();
        long workingTail = initialTail;

        const int batchCount = 5;
        for (int i = 0; i < batchCount; i++)
        {
            int val = i * 10;
            bool ok = ring.TryWriteUncommitted((byte*)&val, sizeof(int), null, 0, ref workingTail);
            Assert.True(ok);
        }

        // Consumer must see ZERO items because _tail has not been committed
        Assert.True(ring.IsEmpty);
        Assert.Equal(initialTail, ring.TailSequence);
        Assert.False(ring.TryRead(out _));

        // Single store-release commit for all 5 frames
        ring.CommitBatch(workingTail);

        Assert.False(ring.IsEmpty);
        Assert.Equal(workingTail, ring.TailSequence);

        // Verify all 5 frames drain in FIFO order
        for (int i = 0; i < batchCount; i++)
        {
            Assert.True(ring.TryRead(out BlockView view));
            Assert.Equal(i * 10, *(int*)view.Payload);
            ring.AdvanceConsumer(in view);
        }

        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 4. Metrics & State Introspection

    [Fact]
    public void Metrics_OccupiedAndFreeBytes_ReflectExactFramingAndAlignment()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        Assert.Equal(_testCapacity, ring.Capacity);
        Assert.Equal(0, ring.OccupiedBytes);
        Assert.Equal(_testCapacity, ring.FreeBytes);
        Assert.True(ring.IsEmpty);

        const int payloadSize = 20; // 8B header + 20B payload = 28B -> aligned to 8B = 32B total
        Assert.True(ring.TryWrite(stackalloc byte[payloadSize]));

        Assert.Equal(32, ring.OccupiedBytes);
        Assert.Equal(_testCapacity - 32, ring.FreeBytes);
        Assert.False(ring.IsEmpty);

        Assert.True(ring.TryRead(out BlockView view));
        ring.AdvanceConsumer(in view);

        Assert.Equal(0, ring.OccupiedBytes);
        Assert.Equal(_testCapacity, ring.FreeBytes);
        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 5. Virtual Memory Mirror Boundary Straddling

    [Fact]
    public void Write_StraddlingBufferBoundary_SucceedsWithoutWrapFragmentation()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        long ringCapacity = ring.Capacity;

        // 1. Advance the writing head near the end of the buffer (leaving 128 bytes free before ring wrap)
        int initialPaddingSize = (int)(ringCapacity - 128 - sizeof(SpscBlockHeader));
        byte* dummyBuffer = (byte*)NativeMemory.Alloc((nuint)initialPaddingSize);
        try
        {
            bool padWrite = ring.TryWrite(dummyBuffer, initialPaddingSize);
            Assert.True(padWrite);

            // Read and consume immediately so the buffer has free capacity,
            // but the tail is located at [ringCapacity - 128]
            bool padRead = ring.TryRead(out BlockView padView);
            Assert.True(padRead);
            ring.AdvanceConsumer(in padView);
            Assert.True(ring.IsEmpty);
        }
        finally
        {
            NativeMemory.Free(dummyBuffer);
        }

        // 2. Write a 256-byte payload starting at (Capacity - 128) and crossing into Mirror View 2
        const int straddlePayloadSize = 256;
        Span<byte> straddleData = stackalloc byte[straddlePayloadSize];
        for (int i = 0; i < straddlePayloadSize; i++)
            straddleData[i] = (byte)(i & 0xFF);

        fixed (byte* pData = straddleData)
        {
            bool straddleWrite = ring.TryWrite(pData, straddlePayloadSize);
            Assert.True(
                straddleWrite,
                "Failed to write frame crossing mirrored virtual memory boundary."
            );
        }

        // 3. Read back linear contiguous memory across the boundary
        bool readSuccess = ring.TryRead(out BlockView straddleView);
        Assert.True(readSuccess);
        Assert.Equal(straddlePayloadSize, straddleView.Length);

        Span<byte> actualSpan = new(straddleView.Payload, straddleView.Length);
        Assert.True(actualSpan.SequenceEqual(straddleData));

        ring.AdvanceConsumer(in straddleView);
        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 6. Saturation & Backpressure

    [Fact]
    public void TryWrite_WhenRingIsSaturated_ReturnsFalseWithoutCorruption()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        long capacity = ring.Capacity;

        int maxPayload = (int)(capacity - sizeof(SpscBlockHeader) - 8);
        byte* largePayload = (byte*)NativeMemory.Alloc((nuint)maxPayload);

        try
        {
            bool firstWrite = ring.TryWrite(largePayload, maxPayload);
            Assert.True(firstWrite);

            // Ring is now full; later write must fail cleanly
            byte smallData = 0xFF;
            bool overflowWrite = ring.TryWrite(&smallData, 1);
            Assert.False(overflowWrite);

            // Drain the initial frame
            bool readSuccess = ring.TryRead(out BlockView view);
            Assert.True(readSuccess);
            ring.AdvanceConsumer(in view);
            Assert.True(ring.IsEmpty);

            // Ring should now accept writes again
            bool resumedWrite = ring.TryWrite(&smallData, 1);
            Assert.True(resumedWrite);
        }
        finally
        {
            NativeMemory.Free(largePayload);
        }
    }

    #endregion

    #region 7. Batch Draining & BatchReader Iteration

    [Fact]
    public void DrainBatch_DrainsAllCommittedFramesUpToMaxBatchSize()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        const int frameCount = 10;

        for (int i = 0; i < frameCount; i++)
        {
            int val = i;
            bool ok = ring.TryWrite((byte*)&val, sizeof(int));
            Assert.True(ok);
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
    public void DrainBatch_ShortCircuitsWhenConsumerReturnsFalse()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
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
        // Fix: Use drainRest.Count - 1 to inspect the last drained element
        Assert.Equal(7, drainRest.ReceivedValues[drainRest.Count - 1]);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void BeginBatch_BatchReader_IteratesAndCommitsOnDispose()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
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
        } // Reader commits _head here

        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void BeginBatch_BatchReader_Abort_DoesNotAdvanceHead()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        int val = 999;
        Assert.True(ring.TryWrite((byte*)&val, sizeof(int)));

        using (var reader = ring.BeginBatch(maxBatchSize: 10))
        {
            Assert.True(reader.TryReadNext(out BlockView view));
            Assert.Equal(999, *(int*)view.Payload);
            reader.Abort(); // Disarm commit
        }

        // Head must remain unadvanced
        Assert.False(ring.IsEmpty);
        Assert.True(ring.TryRead(out BlockView peeked));
        Assert.Equal(999, *(int*)peeked.Payload);
        ring.AdvanceConsumer(in peeked);
        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 8. Multi-Threaded Dual-Canary Stress Test

    [Fact]
    public void Concurrent_SPSC_Stress_DualCanary_GuaranteesStoreReleaseOrdering()
    {
        using var ring = new MirroredSpscRing(_testCapacity);
        const int totalMessages = 250_000;
        long tornFrameDetected = 0;
        long failedSequence = -1;

        var producer = Task.Factory.StartNew(
            () =>
            {
                byte* messageBuffer = stackalloc byte[256];
                SpinWait spinner = default;

                for (long seq = 0; seq < totalMessages; seq++)
                {
                    // Dynamic payload length (24 to 128 bytes)
                    int payloadLength = 24 + (int)(seq % 104);

                    // 1. Head Canary: Sequence Number
                    *(long*)messageBuffer = seq;

                    // 2. Interior Pattern
                    byte pattern = (byte)(seq ^ 0xAA);
                    for (int b = 8; b < payloadLength - 8; b++)
                    {
                        messageBuffer[b] = pattern;
                    }

                    // 3. Tail Canary: Bitwise complement (~seq)
                    *(long*)(messageBuffer + payloadLength - 8) = ~seq;

                    while (!ring.TryWrite(messageBuffer, payloadLength))
                    {
                        spinner.SpinOnce();
                    }
                }
            },
            TaskCreationOptions.LongRunning
        );

        var consumer = Task.Factory.StartNew(
            () =>
            {
                long expectedSeq = 0;
                SpinWait spinner = default;

                while (expectedSeq < totalMessages)
                {
                    if (ring.TryRead(out BlockView view))
                    {
                        long headCanary = *(long*)view.Payload;
                        long tailCanary = *(long*)(view.Payload + view.Length - 8);

                        // If store-release ordering fails, head or tail canary will be corrupt
                        if (headCanary != expectedSeq || tailCanary != ~expectedSeq)
                        {
                            Interlocked.Increment(ref tornFrameDetected);
                            Volatile.Write(ref failedSequence, expectedSeq);
                            break;
                        }

                        // Validate payload body
                        byte expectedPattern = (byte)(expectedSeq ^ 0xAA);
                        byte* payloadBody = view.Payload + 8;
                        int bodyLength = view.Length - 16;
                        for (int b = 0; b < bodyLength; b++)
                        {
                            if (payloadBody[b] != expectedPattern)
                            {
                                Interlocked.Increment(ref tornFrameDetected);
                                Volatile.Write(ref failedSequence, expectedSeq);
                                break;
                            }
                        }

                        ring.AdvanceConsumer(in view);
                        expectedSeq++;
                    }
                    else
                    {
                        spinner.SpinOnce();
                    }
                }
            },
            TaskCreationOptions.LongRunning
        );

#pragma warning disable xUnit1031 // unsafe test method cannot be async
        Task.WaitAll(producer, consumer);
#pragma warning restore xUnit1031

        Assert.Equal(0, Volatile.Read(ref tornFrameDetected));
        Assert.Equal(-1, Volatile.Read(ref failedSequence));
        Assert.True(ring.IsEmpty);
    }

    #endregion

    #region 9. Lifecycle & Native Allocator Verification

    [Fact]
    public void Native_Allocate_And_Free_DirectExecution_Succeeds()
    {
        NativeMirroredSpscRing* nativeRing = NativeMirroredSpscRing.Allocate(_testCapacity);
        Assert.True(nativeRing != null);
        Assert.True(nativeRing->IsEmpty);
        Assert.Equal(_testCapacity, nativeRing->Capacity);

        int testVal = 12345;
        Assert.True(nativeRing->TryWrite((byte*)&testVal, sizeof(int)));
        Assert.False(nativeRing->IsEmpty);

        Assert.True(nativeRing->TryRead(out BlockView view));
        Assert.Equal(12345, *(int*)view.Payload);
        nativeRing->AdvanceConsumer(in view);
        Assert.True(nativeRing->IsEmpty);

        NativeMirroredSpscRing.Free(nativeRing);
    }

    [Fact]
    public void Dispose_IsIdempotent_And_ThrowsWhenUsedPostDispose()
    {
        var ring = new MirroredSpscRing(_testCapacity);
        ring.Dispose();
        ring.Dispose(); // Should not throw on duplicate invocation

        Assert.Throws<ObjectDisposedException>(() => ring.DangerousGetNativePointer());
    }

    #endregion

    #region Test Structs

    [StructLayout(LayoutKind.Sequential)]
    private struct TestPacketHeader
    {
        public int Id;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestCollector : IBlockConsumer
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
    private struct ShortCircuitConsumer(int stopAfterCount) : IBlockConsumer
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

    #endregion
}
