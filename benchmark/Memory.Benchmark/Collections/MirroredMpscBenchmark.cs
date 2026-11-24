using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Memory.Allocators;
using Memory.Collections.MirroredMpsc;

namespace Memory.Benchmark.Collections;

[MemoryDiagnoser]
[ShortRunJob]
public unsafe class MirroredMpscBenchmark
{
    // 64B control packets, 512B entity snapshots, and 1400B MTU-sized frames
    [Params(64, 512, 1400)]
    public int PayloadSize;

    private const int RingCapacity = 4 * 1024 * 1024; // 4MB Mirrored Virtual Ring
    private const int HeaderSize = 16; // 16B Framing header
    private const int BurstBatchSize = 32; // Matches data plane batch window

    private MirroredMpscRing _ring;
    private NativeMirroredMpscRing* _nativeRing;

    private byte* _headerBuffer;
    private byte* _payloadBuffer;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _ring = new MirroredMpscRing(RingCapacity);
        _nativeRing = _ring.DangerousGetNativePointer();

        _headerBuffer = (byte*)HeapAllocator.Allocate(HeaderSize, 16);
        _payloadBuffer = (byte*)HeapAllocator.Allocate(PayloadSize, 64);

        for (int i = 0; i < PayloadSize; i++)
        {
            _payloadBuffer[i] = (byte)(i & 0xFF);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _ring?.Dispose();

        if (_headerBuffer != null)
        {
            HeapAllocator.Free(_headerBuffer);
            _headerBuffer = null;
        }

        if (_payloadBuffer != null)
        {
            HeapAllocator.Free(_payloadBuffer);
            _payloadBuffer = null;
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Drain any unconsumed residual frames between iterations
        while (_nativeRing->TryRead(out BlockView view))
        {
            _nativeRing->AdvanceConsumer(in view);
        }
    }

    // =========================================================================
    // 1. SINGLE-FRAME ROUND-TRIP (Managed vs. Raw Native vs. Generic Struct)
    // =========================================================================

    [Benchmark(Baseline = true)]
    public byte Managed_Single_Packet_RoundTrip()
    {
        _ring.TryWrite(_payloadBuffer, PayloadSize);

        _ring.TryRead(out BlockView view);
        byte firstByte = view.Payload[0];
        _ring.AdvanceConsumer(in view); // Volatile store per item

        return firstByte;
    }

    [Benchmark]
    public byte Native_DirectPointer_Single_Packet_RoundTrip()
    {
        // Zero-trampoline unmanaged C-ABI invocation
        _nativeRing->TryWrite(_payloadBuffer, PayloadSize);

        _nativeRing->TryRead(out BlockView view);
        byte firstByte = view.Payload[0];
        _nativeRing->AdvanceConsumer(in view);

        return firstByte;
    }

    [Benchmark]
    public byte Native_GenericStruct_Write_And_Read()
    {
        // Direct unmanaged struct pass by-ref: tests compiler SIMD blitting without stackalloc
        var header = new PacketHeader
        {
            TimestampNs = 123456789L,
            SequenceId = 42,
            SourceId = 1,
            TargetId = 2,
        };
        _nativeRing->TryWrite(in header);

        _nativeRing->TryRead(out BlockView view);
        byte firstByte = view.Payload[0];
        _nativeRing->AdvanceConsumer(in view);

        return firstByte;
    }

    // =========================================================================
    // 2. TWO-SEGMENT ENVELOPE FRAMING (Metadata Header + Raw Payload)
    // =========================================================================

    [Benchmark]
    public byte Framing_TwoSegment_Write_And_Read()
    {
        // Atomically reserves and blits 16B header + N-byte payload into contiguous memory
        _nativeRing->TryWrite(_headerBuffer, HeaderSize, _payloadBuffer, PayloadSize);

        _nativeRing->TryRead(out BlockView view);
        byte headerByte = view.Payload[0];
        _nativeRing->AdvanceConsumer(in view);

        return headerByte;
    }

    // =========================================================================
    // 3. BURST DRAIN COMPARISONS (32-Packet Window)
    // =========================================================================

    [Benchmark]
    public byte Burst_Drain_SingleBySingle_32Packets()
    {
        for (int i = 0; i < BurstBatchSize; i++)
        {
            _nativeRing->TryWrite(_payloadBuffer, PayloadSize);
        }

        byte accumulator = 0;
        // 32 reads + 32 separate volatile release stores to _head
        for (int i = 0; i < BurstBatchSize; i++)
        {
            if (_nativeRing->TryRead(out BlockView view))
            {
                accumulator ^= view.Payload[0];
                _nativeRing->AdvanceConsumer(in view);
            }
        }

        return accumulator;
    }

    [Benchmark]
    public byte Burst_Drain_BatchReader_32Packets()
    {
        for (int i = 0; i < BurstBatchSize; i++)
        {
            _nativeRing->TryWrite(_payloadBuffer, PayloadSize);
        }

        byte accumulator = 0;
        // 32 reads + single amortized volatile store to _head upon Dispose()
        using (
            var batch = NativeMirroredMpscRing.BeginBatch(_nativeRing, maxBatchSize: BurstBatchSize)
        )
        {
            while (batch.TryReadNext(out BlockView view))
            {
                accumulator ^= view.Payload[0];
            }
        }

        return accumulator;
    }

    [Benchmark]
    public byte Burst_Drain_Managed_DrainBatch_32Packets()
    {
        for (int i = 0; i < BurstBatchSize; i++)
        {
            _ring.TryWrite(_payloadBuffer, PayloadSize);
        }

        // Monomorphized struct visitor via managed wrapper
        var consumer = new BenchmarkSinkConsumer();
        _ring.DrainBatch(ref consumer, maxBatchSize: BurstBatchSize);

        return consumer.Accumulator;
    }

    [Benchmark]
    public byte Burst_Drain_Native_DrainBatch_32Packets()
    {
        for (int i = 0; i < BurstBatchSize; i++)
        {
            _nativeRing->TryWrite(_payloadBuffer, PayloadSize);
        }

        // Monomorphized struct visitor directly on raw pointer: zero vtable, inlined loop
        var consumer = new BenchmarkSinkConsumer();
        _nativeRing->DrainBatch(ref consumer, maxBatchSize: BurstBatchSize);

        return consumer.Accumulator;
    }

    // =========================================================================
    // 4. MULTI-PRODUCER CONTENTION & DRAIN THROUGHPUT (4 Concurrent Threads)
    // =========================================================================

    [Benchmark]
    public void Mpsc_Concurrent_Drain_1By1()
    {
        const int producers = 4;
        const int writesPerProducer = 250;
        const int totalWrites = producers * writesPerProducer;

        Parallel.For(
            0,
            producers,
            _ =>
            {
                for (int i = 0; i < writesPerProducer; i++)
                {
                    while (!_nativeRing->TryWrite(_payloadBuffer, PayloadSize))
                    {
                        Thread.SpinWait(5);
                    }
                }
            }
        );

        int consumed = 0;
        while (consumed < totalWrites)
        {
            if (_nativeRing->TryRead(out BlockView view))
            {
                consumed++;
                _nativeRing->AdvanceConsumer(in view);
            }
        }
    }

    [Benchmark]
    public byte Mpsc_Concurrent_Drain_Batch_Devirtualized()
    {
        const int producers = 4;
        const int writesPerProducer = 250;
        const int totalWrites = producers * writesPerProducer;

        Parallel.For(
            0,
            producers,
            _ =>
            {
                for (int i = 0; i < writesPerProducer; i++)
                {
                    while (!_nativeRing->TryWrite(_payloadBuffer, PayloadSize))
                    {
                        Thread.SpinWait(5);
                    }
                }
            }
        );

        int consumed = 0;
        var consumer = new BenchmarkSinkConsumer();

        while (consumed < totalWrites)
        {
            int drained = _nativeRing->DrainBatch(ref consumer, maxBatchSize: BurstBatchSize);
            consumed += drained;

            if (drained == 0)
            {
                Thread.SpinWait(2);
            }
        }

        return consumer.Accumulator;
    }

    // =========================================================================
    // TEST STRUCTS
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct PacketHeader
    {
        public long TimestampNs;
        public uint SequenceId;
        public ushort SourceId;
        public ushort TargetId;
    }

    private struct BenchmarkSinkConsumer : IBlockConsumer
    {
        public byte Accumulator;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(byte* payload, int length)
        {
            Accumulator ^= payload[0];
            return true;
        }
    }
}
