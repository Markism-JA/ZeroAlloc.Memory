using System.Buffers;
using BenchmarkDotNet.Attributes;
using Memory.Allocators;
using Memory.Arena;

namespace Memory.Benchmark.Arena;

[MemoryDiagnoser]
public unsafe class UnmanagedLinearArenaBenchmark
{
    public struct EntityTransform
    {
        public float X,
            Y,
            Z;
        public float QX,
            QY,
            QZ,
            QW;
    }

    // 1MB Arena (65,536 elements of EntityTransform)
    private const int ArenaCapacityBytes = 1024 * 1024;
    private void* _arenaVirtualBuffer;
    private UnmanagedLinearArena<EntityTransform> _arena;

    private const int WorkerCount = 4;
    private PersistentWorkerPool _workerPool = null!;
    private Action<int> _parallelLeaseAction = null!;

    [Params(100, 1000, 10000)]
    public int LeaseCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Allocate page-aligned virtual memory for the arena
        _arenaVirtualBuffer = PageAllocator.ReserveAddressSpace(ArenaCapacityBytes);
        PageAllocator.CommitVirtualRange(_arenaVirtualBuffer, ArenaCapacityBytes);

        _arena = UnmanagedLinearArena<EntityTransform>.CreateOverPointer(
            _arenaVirtualBuffer,
            ArenaCapacityBytes,
            AllocationType.KernelVirtual
        );

        _workerPool = new PersistentWorkerPool(WorkerCount, "ArenaBench");
        _parallelLeaseAction = threadId =>
        {
            int perThreadLeases = LeaseCount / WorkerCount;
            for (int i = 0; i < perThreadLeases; i++)
            {
                EntityTransform* transform = _arena.Lease(1);
                if (transform != null)
                {
                    transform->Y = threadId;
                }
            }
        };
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _workerPool?.Dispose();
        if (_arenaVirtualBuffer != null)
        {
            PageAllocator.ReleaseVirtual(_arenaVirtualBuffer, ArenaCapacityBytes);
            _arenaVirtualBuffer = null;
        }
    }

    // =========================================================================
    //  SINGLE-THREADED BUMP LEASING & TICK RESET
    // =========================================================================

    [Benchmark(Baseline = true)]
    public void Lease_SingleThreaded_Cycle()
    {
        int count = LeaseCount;

        for (int i = 0; i < count; i++)
        {
            EntityTransform* transform = _arena.Lease(1);
            transform->X = i;
        }

        _arena.Reset();
    }

    [Benchmark]
    public void Lease_Batch_Slices()
    {
        // Batch lease 100 elements at a time (e.g., job batching)
        int batchSize = 100;
        int iterations = LeaseCount / batchSize;

        for (int i = 0; i < iterations; i++)
        {
            EntityTransform* slice = _arena.Lease(batchSize);
            slice[0].X = i;
        }

        _arena.Reset();
    }

    [Benchmark]
    public void ArrayPool_Shared_Batch_RentReturn()
    {
        const int batchSize = 100;
        int iterations = LeaseCount / batchSize;

        for (int i = 0; i < iterations; i++)
        {
            EntityTransform[] rented = ArrayPool<EntityTransform>.Shared.Rent(batchSize);
            rented[0].X = i;
            ArrayPool<EntityTransform>.Shared.Return(rented);
        }
    }

    // =========================================================================
    //  MULTI-THREADED CAS CONTENTION (Parallel System Execution)
    // =========================================================================

    [Benchmark]
    public void Parallel_MultiThreaded_Lease_Contention()
    {
        _workerPool.Execute(_parallelLeaseAction);
        _arena.Reset();
    }

    // =========================================================================
    //  PASSIVE OS MEMORY TRIMMING (`madvise` / `VirtualFree`)
    // =========================================================================

    [Benchmark]
    public void Lease_And_TrimBackingMemory()
    {
        // Lease 50% of the arena capacity
        int elementsToLease = ArenaCapacityBytes / (2 * sizeof(EntityTransform));
        _arena.Lease(elementsToLease);

        // Instruct OS Kernel to reclaim unused physical RAM pages in the upper half of the arena
        _arena.TrimBackingMemory();

        _arena.Reset();
    }
}
