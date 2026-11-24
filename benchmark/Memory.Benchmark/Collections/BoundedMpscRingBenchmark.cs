using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Memory.Collections.BoundedMpsc;

namespace Memory.Benchmark.Collections;

/// <summary>
/// Benchmarks for <see cref="BoundedMpscRing{T}"/> and <see cref="NativeBoundedMpscRing{T}"/>
/// covering single-item round-trip, batch drain, and 4-producer contended throughput vs
/// <see cref="Channel{T}"/> and <see cref="ConcurrentQueue{T}"/>.
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
    private const int Producers = 4;
    private const int ContendedWritesPerProducer = 250;
    private const int ContendedTotalWrites = Producers * ContendedWritesPerProducer;

    private BoundedMpscRing<Message64> _ring = null!;
    private NativeBoundedMpscRing<Message64>* _nativeRing;
    private Channel<Message64> _channel = null!;
    private ConcurrentQueue<Message64> _concurrentQueue = null!;
    private Message64 _sample;
    private PersistentWorkerPool _workerPool = null!;
    private Action<int> _mpscProducerWork = null!;
    private Action<int> _channelProducerWork = null!;
    private Action<int> _queueProducerWork = null!;

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
        _concurrentQueue = new ConcurrentQueue<Message64>();
        _sample = new Message64 { Sequence = 0xCAFE_BABE_DEAD_BEEFul };

        _workerPool = new PersistentWorkerPool(Producers, "BoundedMpscBench");
        _mpscProducerWork = _ =>
        {
            for (int i = 0; i < ContendedWritesPerProducer; i++)
            {
                while (!_nativeRing->TryWrite(in _sample))
                {
                    Thread.SpinWait(2);
                }
            }
        };
        _channelProducerWork = _ =>
        {
            for (int i = 0; i < ContendedWritesPerProducer; i++)
            {
                while (!_channel.Writer.TryWrite(_sample))
                {
                    Thread.SpinWait(2);
                }
            }
        };
        _queueProducerWork = _ =>
        {
            for (int i = 0; i < ContendedWritesPerProducer; i++)
            {
                _concurrentQueue.Enqueue(_sample);
            }
        };
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _workerPool?.Dispose();
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

    [Benchmark]
    public ulong Contended_4Producers_BoundedMpsc()
    {
        _workerPool.Execute(_mpscProducerWork);

        var consumer = new SumConsumer();
        int drained = 0;
        while (drained < ContendedTotalWrites)
        {
            drained += _nativeRing->DrainBatch(ref consumer, BatchSize);
        }

        return consumer.Sum;
    }

    [Benchmark]
    public ulong Contended_4Producers_Channel()
    {
        _workerPool.Execute(_channelProducerWork);

        ulong sum = 0;
        int drained = 0;
        while (drained < ContendedTotalWrites)
        {
            if (_channel.Reader.TryRead(out Message64 item))
            {
                sum += item.Sequence;
                drained++;
            }
        }

        return sum;
    }

    [Benchmark]
    public ulong Contended_4Producers_ConcurrentQueue()
    {
        _workerPool.Execute(_queueProducerWork);

        ulong sum = 0;
        int drained = 0;
        while (drained < ContendedTotalWrites)
        {
            if (_concurrentQueue.TryDequeue(out Message64 item))
            {
                sum += item.Sequence;
                drained++;
            }
        }

        return sum;
    }
}
