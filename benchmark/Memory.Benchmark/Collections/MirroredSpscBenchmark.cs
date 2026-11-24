using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Memory.Allocators;
using Memory.Collections.MirroredMpsc;
using Memory.Collections.MirroredSpsc;

namespace Memory.Benchmark.Collections;

/// <summary>
/// Zero-copy variable-length framing benchmarks for <see cref="MirroredSpscRing"/> and
/// <see cref="NativeMirroredSpscRing"/>, including <see cref="SpscWriteScope"/> reservation
/// and batch consumer draining.
/// </summary>
[MemoryDiagnoser]
public unsafe class MirroredSpscBenchmark
{
    [Params(64, 512, 1400)]
    public int PayloadSize;

    private struct ByteConsumer : IBlockConsumer
    {
        public int Sum;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(byte* payload, int length)
        {
            Sum += payload[0];
            return true;
        }
    }

    private const int RingCapacity = 4 * 1024 * 1024;
    private const int BurstBatchSize = 32;

    private MirroredSpscRing _ring = null!;
    private NativeMirroredSpscRing* _nativeRing;
    private byte* _payloadBuffer;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _ring = new MirroredSpscRing(RingCapacity);
        _nativeRing = _ring.DangerousGetNativePointer();
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
        _nativeRing = null;

        if (_payloadBuffer != null)
        {
            HeapAllocator.Free(_payloadBuffer);
            _payloadBuffer = null;
        }
    }

    [Benchmark(Baseline = true)]
    public byte Managed_Single_Packet_RoundTrip()
    {
        _ring.TryWrite(_payloadBuffer, PayloadSize);
        _ring.TryRead(out BlockView view);
        byte firstByte = view.Payload[0];
        _ring.AdvanceConsumer(in view);
        return firstByte;
    }

    [Benchmark]
    public byte Native_Single_Packet_RoundTrip()
    {
        _nativeRing->TryWrite(_payloadBuffer, PayloadSize);
        _nativeRing->TryRead(out BlockView view);
        byte firstByte = view.Payload[0];
        _nativeRing->AdvanceConsumer(in view);
        return firstByte;
    }

    [Benchmark]
    public byte Managed_WriteScope_ZeroCopy_RoundTrip()
    {
        if (_ring.TryReserve(PayloadSize, out SpscWriteScope scope))
        {
            Buffer.MemoryCopy(_payloadBuffer, scope.Pointer, PayloadSize, PayloadSize);
            scope.Dispose();
        }

        _ring.TryRead(out BlockView view);
        byte firstByte = view.Payload[0];
        _ring.AdvanceConsumer(in view);
        return firstByte;
    }

    [Benchmark]
    public byte Native_ReserveCommit_ZeroCopy_RoundTrip()
    {
        if (_nativeRing->TryReserve(PayloadSize, out byte* destination, out SpscWriteTicket ticket))
        {
            Buffer.MemoryCopy(_payloadBuffer, destination, PayloadSize, PayloadSize);
            _nativeRing->CommitWrite(in ticket);
        }

        _nativeRing->TryRead(out BlockView view);
        byte firstByte = view.Payload[0];
        _nativeRing->AdvanceConsumer(in view);
        return firstByte;
    }

    [Benchmark]
    public int Batch_Write_And_Drain()
    {
        for (int i = 0; i < BurstBatchSize; i++)
        {
            _nativeRing->TryWrite(_payloadBuffer, PayloadSize);
        }

        var consumer = new ByteConsumer();
        _nativeRing->DrainBatch(ref consumer, BurstBatchSize);
        return consumer.Sum;
    }
}
