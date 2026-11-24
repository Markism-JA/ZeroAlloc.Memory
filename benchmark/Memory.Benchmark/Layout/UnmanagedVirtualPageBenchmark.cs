using BenchmarkDotNet.Attributes;
using Memory.Layout;

namespace Memory.Benchmark.Layout;

[MemoryDiagnoser]
public unsafe class UnmanagedVirtualPageBenchmark
{
    // Test small 64B structural slices, 512B entity chunks, and 4096B (1 full OS Page) blocks
    [Params(64, 512, 4096)]
    public int SliceSize;

    // Test 2MB and 16MB virtual address space reservations
    [Params(2 * 1024 * 1024, 16 * 1024 * 1024)]
    public int VirtualCapacity;

    private UnmanagedVirtualPage _virtualPage;
    private const int CarveBatchCount = 500;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _virtualPage = new UnmanagedVirtualPage(VirtualCapacity);

        // Pre-commit the initial range so fast-path benchmarks don't hit OS kernel faults continuously
        _virtualPage.Carve(CarveBatchCount * SliceSize);
        _virtualPage.Reset();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _virtualPage.Dispose();
    }

    // =========================================================================
    // FAST-PATH VIRTUAL CARVING (Pre-Committed Memory - 0ns OS Calls)
    // =========================================================================

    [Benchmark(Baseline = true)]
    public void* Carve_PreCommitted_FastPath_500()
    {
        int size = SliceSize;
        void* lastPtr = null;

        // Slices from already-committed virtual memory (pure pointer increment)
        for (int i = 0; i < CarveBatchCount; i++)
        {
            lastPtr = _virtualPage.Carve(size, 64);
        }

        _virtualPage.Reset();
        return lastPtr; // Prevents RyuJIT dead-code elimination
    }

    [Benchmark]
    public void* CarveZeroed_PreCommitted_FastPath_500()
    {
        int size = SliceSize;
        void* lastPtr = null;

        for (int i = 0; i < CarveBatchCount; i++)
        {
            lastPtr = _virtualPage.CarveZeroed(size, 64);
        }

        _virtualPage.Reset();
        return lastPtr;
    }

    // =========================================================================
    // DEMAND-COMMITTING KERNEL PATH (Forces OS Page Frame Commits)
    // =========================================================================

    [Benchmark]
    public void* Carve_With_DemandCommit_Kernel_Pass()
    {
        // Instantiate a fresh virtual page with 0 physical RAM committed
        using var freshPage = new UnmanagedVirtualPage(VirtualCapacity);

        void* lastPtr = null;
        int size = SliceSize;

        // Carving forces demand-commitment of new physical pages from the OS kernel
        for (int i = 0; i < CarveBatchCount; i++)
        {
            lastPtr = freshPage.Carve(size, 64);
        }

        return lastPtr;
    }

    // =========================================================================
    // PASSIVE OS RECLAMATION & RE-COMMIT CYCLE (`madvise` / `VirtualFree`)
    // =========================================================================

    [Benchmark]
    public bool PassiveDecommitReclaim_Lifecycle()
    {
        _virtualPage.Carve(CarveBatchCount * SliceSize);
        _virtualPage.PassiveDecommitReclaim();
        void* ptr = _virtualPage.Carve(SliceSize);
        _virtualPage.Reset();

        return ptr != null;
    }
}
