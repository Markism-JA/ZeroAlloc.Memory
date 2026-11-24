using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Memory.Collections.BoundedMpsc;

namespace Memory.Benchmark.Collections;

/// <summary>
/// Benchmarks for <see cref="BoundedMpscRing{T}"/> and <see cref="NativeBoundedMpscRing{T}"/>
/// covering single-item round-trip, direct pointer dispatch, and batch drain vs <see cref="Channel{T}"/>.
/// </summary>
[MemoryDiagnoser]
public unsafe class BoundedMpscRingBenchmark
{
    public struct Message64
    {
        public ulong Sequence;
        public fixed byte Payload[56];
    }

    private struct SumConsumer : IValueConsumer<Message64>
    {
        public ulong Sum;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Consume(in Message64 item)
        {
            Sum += item.Sequence;
            return true;
        }
    }

    private const int RingCapacity = 16384;
    private const int BatchSize = 64;

    private BoundedMpscRing<Message64> _ring = null!;
    private NativeBoundedMpscRing<Message64>* _nativeRing;
    private Channel<Message64> _channel = null!;
    private Message64 _sample;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _ring = new BoundedMpscRing<Message64>(RingCapacity);
        _nativeRing = _ring.DangerousGetNativePointer();
        _channel = Channel.CreateBounded<Message64>(new BoundedChannelOptions(RingCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true,
        });
        _sample = new Message64 { Sequence = 0xCAFE_BABE_DEAD_BEEFul };
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _ring?.Dispose();
        _nativeRing = null;
    }

    [Benchmark(Baseline = true)]
    public ulong Managed_Single_RoundTrip()
    {
        _ring.TryWrite(in _sample);
        _ring.TryRead(out Message64 item);
        return item.Sequence;
    }

    [Benchmark]
    public ulong Native_DirectPointer_RoundTrip()
    {
        _nativeRing->TryWrite(in _sample);
        _nativeRing->TryRead(out Message64 item);
        return item.Sequence;
    }

    [Benchmark]
    public ulong Channel_TryWrite_TryRead_RoundTrip()
    {
        _channel.Writer.TryWrite(_sample);
        _channel.Reader.TryRead(out Message64 item);
        return item.Sequence;
    }

    [Benchmark]
    public ulong Batch_Write_And_Drain()
    {
        for (int i = 0; i < BatchSize; i++)
        {
            _nativeRing->TryWrite(in _sample);
        }

        var consumer = new SumConsumer();
        _nativeRing->DrainBatch(ref consumer, BatchSize);
        return consumer.Sum;
    }
}
