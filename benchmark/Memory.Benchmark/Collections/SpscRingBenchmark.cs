using BenchmarkDotNet.Attributes;
using Memory.Allocators;
using Memory.Collections;
using Memory.Collections.Spsc;

namespace Memory.Benchmark.Collections;

[MemoryDiagnoser]
public unsafe class SpscRingBenchmark
{
    public struct Packet64
    {
        public ulong Sequence;
        public fixed byte Payload[56];
    }

    public struct Packet512
    {
        public ulong Sequence;
        public fixed byte Payload[504];
    }

    private const int RingCapacity = 16384;
    private const int BatchSize = 64;
    private const int PipelineItemCount = 100_000;

    private SpscRing<Packet64> _ring64 = null!;
    private SpscRing<Packet512> _ring512 = null!;

    private Packet64* _batchSource64;
    private Packet64* _batchDrain64;

    private Packet64 _sample64;
    private Packet512 _sample512;

    // Dedicated, persistent thread infrastructure to eliminate ThreadPool / Task allocations
    private Thread _consumerThread = null!;
    private readonly AutoResetEvent _startConsumerSignal = new(false);
    private readonly AutoResetEvent _consumerCompletedSignal = new(false);
    private volatile bool _isRunning;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _ring64 = new SpscRing<Packet64>(RingCapacity);
        _ring512 = new SpscRing<Packet512>(RingCapacity);

        // Pre-allocate native buffers for batch testing (avoids array GC headers and bounds checking)
        _batchSource64 = (Packet64*)HeapAllocator.Allocate(BatchSize * sizeof(Packet64), 64);
        _batchDrain64 = (Packet64*)HeapAllocator.Allocate(BatchSize * sizeof(Packet64), 64);

        _sample64 = new Packet64 { Sequence = 0xCAFE_BABE_DEAD_BEEFul };
        _sample512 = new Packet512 { Sequence = 0xCAFE_BABE_DEAD_BEEFul };

        for (int i = 0; i < BatchSize; i++)
        {
            _batchSource64[i] = _sample64;
        }

        // Spin up persistent consumer worker thread pinned to a long-lived loop
        _isRunning = true;
        _consumerThread = new Thread(ConsumerWorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "SPSC-Benchmark-Consumer",
        };
        _consumerThread.Start();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _isRunning = false;
        _startConsumerSignal.Set();
        _consumerThread?.Join();
        _startConsumerSignal.Dispose();
        _consumerCompletedSignal.Dispose();

        _ring64?.Dispose();
        _ring512?.Dispose();

        if (_batchSource64 != null)
        {
            HeapAllocator.Free(_batchSource64);
            _batchSource64 = null;
        }

        if (_batchDrain64 != null)
        {
            HeapAllocator.Free(_batchDrain64);
            _batchDrain64 = null;
        }
    }

    private void ConsumerWorkerLoop()
    {
        while (_isRunning)
        {
            _startConsumerSignal.WaitOne();
            if (!_isRunning)
                break;

            int consumed = 0;
            while (consumed < PipelineItemCount)
            {
                if (_ring64.TryDequeue(out _))
                {
                    consumed++;
                }
                else
                {
                    Thread.SpinWait(4);
                }
            }

            _consumerCompletedSignal.Set();
        }
    }

    // =========================================================================
    //  SINGLE ELEMENT ROUND-TRIP: VALUE-COPY (TryEnqueue -> TryDequeue)
    // =========================================================================

    [Benchmark(Baseline = true)]
    public ulong RoundTrip_Copy_64B()
    {
        _ring64.TryEnqueue(in _sample64);
        _ring64.TryDequeue(out Packet64 dequeued);
        return dequeued.Sequence;
    }

    [Benchmark]
    public ulong RoundTrip_Copy_512B()
    {
        _ring512.TryEnqueue(in _sample512);
        _ring512.TryDequeue(out Packet512 dequeued);
        return dequeued.Sequence;
    }

    // =========================================================================
    //  SINGLE ELEMENT ROUND-TRIP: ZERO-COPY (TryGetWritePointer -> TryDequeue)
    // =========================================================================

    [Benchmark]
    public ulong RoundTrip_ZeroCopyPointer_64B()
    {
        if (_ring64.TryGetWritePointer(out Packet64* dest))
        {
            *dest = _sample64;
            _ring64.CommitWrite();
        }

        _ring64.TryDequeue(out Packet64 dequeued);
        return dequeued.Sequence;
    }

    [Benchmark]
    public ulong RoundTrip_ZeroCopyPointer_512B()
    {
        if (_ring512.TryGetWritePointer(out Packet512* dest))
        {
            *dest = _sample512;
            _ring512.CommitWrite();
        }

        _ring512.TryDequeue(out Packet512 dequeued);
        return dequeued.Sequence;
    }

    // =========================================================================
    //  BATCH INGEST & DRAIN (Loop vs Block-MemoryCopy)
    // =========================================================================

    [Benchmark]
    public int Batch_ScalarEnqueue_And_Drain_64B()
    {
        for (int i = 0; i < BatchSize; i++)
        {
            _ring64.TryEnqueue(in _sample64);
        }

        return _ring64.Drain(new Span<Packet64>(_batchDrain64, BatchSize));
    }

    [Benchmark]
    public int Batch_BulkEnqueue_And_Drain_64B()
    {
        _ring64.EnqueueBatch(new ReadOnlySpan<Packet64>(_batchSource64, BatchSize));
        return _ring64.Drain(new Span<Packet64>(_batchDrain64, BatchSize));
    }

    // =========================================================================
    //  CONCURRENT PIPELINE THROUGHPUT (Dedicated Worker Thread)
    // =========================================================================

    [Benchmark]
    public void Concurrent_Thread_Pipeline_Throughput_Copy()
    {
        _startConsumerSignal.Set();

        for (int i = 0; i < PipelineItemCount; i++)
        {
            while (!_ring64.TryEnqueue(in _sample64))
            {
                Thread.SpinWait(4);
            }
        }

        _consumerCompletedSignal.WaitOne();
    }

    [Benchmark]
    public void Concurrent_Thread_Pipeline_Throughput_ZeroCopyPointer()
    {
        _startConsumerSignal.Set();

        for (int i = 0; i < PipelineItemCount; i++)
        {
            Packet64* dest;
            while (!_ring64.TryGetWritePointer(out dest))
            {
                Thread.SpinWait(4);
            }

            *dest = _sample64;
            _ring64.CommitWrite();
        }

        _consumerCompletedSignal.WaitOne();
    }
}
