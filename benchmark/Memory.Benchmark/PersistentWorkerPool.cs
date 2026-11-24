namespace Memory.Benchmark;

/// <summary>
/// Reusable zero-allocation persistent worker thread pool for multi-threaded benchmarks.
/// Uses <see cref="Barrier"/> synchronization with cached delegates to avoid <c>Task.Run</c>
/// or <c>Parallel.For</c> per-iteration heap allocations.
/// </summary>
public sealed class PersistentWorkerPool : IDisposable
{
    private readonly int _workerCount;
    private readonly Thread[] _threads;
    private readonly Barrier _startBarrier;
    private readonly Barrier _endBarrier;
    private Action<int>? _currentWork;
    private volatile bool _stop;

    public PersistentWorkerPool(int workerCount, string namePrefix = "BenchWorker")
    {
        _workerCount = workerCount;
        _startBarrier = new Barrier(workerCount + 1);
        _endBarrier = new Barrier(workerCount + 1);
        _threads = new Thread[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            int workerId = i;
            _threads[i] = new Thread(() => WorkerLoop(workerId))
            {
                IsBackground = true,
                Name = $"{namePrefix}-{workerId}",
            };
            _threads[i].Start();
        }
    }

    private void WorkerLoop(int workerId)
    {
        while (true)
        {
            _startBarrier.SignalAndWait();
            if (_stop)
            {
                break;
            }

            _currentWork!(workerId);
            _endBarrier.SignalAndWait();
        }
    }

    /// <summary>
    /// Executes a pre-cached <paramref name="work"/> delegate across all worker threads
    /// and blocks until every worker completes.
    /// </summary>
    public void Execute(Action<int> work)
    {
        _currentWork = work;
        _startBarrier.SignalAndWait();
        _endBarrier.SignalAndWait();
    }

    public void Dispose()
    {
        if (_stop)
        {
            return;
        }

        _stop = true;
        _startBarrier.SignalAndWait();

        for (int i = 0; i < _workerCount; i++)
        {
            _threads[i].Join();
        }

        _startBarrier.Dispose();
        _endBarrier.Dispose();
    }
}
