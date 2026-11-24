using BenchmarkDotNet.Attributes;
using Memory.Allocators;

namespace Memory.Benchmark.Allocators;

[MemoryDiagnoser]
public unsafe class KernelPageAllocatorBenchmark
{
    // Benchmark 1 Page (4KB), 16 Pages (64KB), and 256 Pages (1MB)
    [Params(1, 16, 256)]
    public int PageCount;

    private int _byteSize;
    private void* _preReservedAddressWindow;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _byteSize = PageCount * PageAllocator.SystemPageSize;
    }

    [IterationSetup(Target = nameof(Commit_Decommit_Loop))]
    public void IterationSetupPreReservedWindow()
    {
        // Reserve a persistent address window for demand-commit benchmarks
        _preReservedAddressWindow = PageAllocator.ReserveAddressSpace(_byteSize);
    }

    [IterationCleanup(Target = nameof(Commit_Decommit_Loop))]
    public void IterationCleanupPreReservedWindow()
    {
        if (_preReservedAddressWindow != null)
        {
            PageAllocator.ReleaseVirtual(_preReservedAddressWindow, _byteSize);
            _preReservedAddressWindow = null;
        }
    }

    // =========================================================================
    //  VIRTUAL ADDRESS SPACE RESERVATION (Zero Physical RAM Cost)
    // =========================================================================

    [Benchmark(Baseline = true)]
    public void Reserve_And_ReleaseAddressSpace()
    {
        void* ptr = PageAllocator.ReserveAddressSpace(_byteSize);
        PageAllocator.ReleaseVirtual(ptr, _byteSize);
    }

    // =========================================================================
    //  FULL VIRTUAL ALLOCATION LIFECYCLE (Reserve + Commit + Release)
    // =========================================================================

    [Benchmark]
    public void Reserve_Commit_And_Release()
    {
        void* ptr = PageAllocator.ReserveAddressSpace(_byteSize);
        PageAllocator.CommitVirtualRange(ptr, _byteSize);
        PageAllocator.ReleaseVirtual(ptr, _byteSize);
    }

    // =========================================================================
    //  PHYSICAL TLB PAGE FAULT COST (Writes to Freshly Committed OS Pages)
    // =========================================================================

    [Benchmark]
    public byte Commit_And_Touch_Physical_Pages()
    {
        byte* ptr = (byte*)PageAllocator.ReserveAddressSpace(_byteSize);
        PageAllocator.CommitVirtualRange(ptr, _byteSize);

        int pageSize = PageAllocator.SystemPageSize;

        // Touch the first byte of every committed OS page frame to trigger hardware TLB page faults
        for (int offset = 0; offset < _byteSize; offset += pageSize)
        {
            ptr[offset] = 0xAB;
        }

        byte value = ptr[0];

        PageAllocator.ReleaseVirtual(ptr, _byteSize);
        return value; // Prevents RyuJIT dead-code elimination
    }

    // =========================================================================
    //  DEMAND COMMITTING & PASSIVE OS RECLAMATION (madvise / MEM_DECOMMIT)
    // =========================================================================

    [Benchmark]
    public bool Commit_Decommit_Loop()
    {
        void* window = _preReservedAddressWindow;

        // Demand-commit physical page frames into pre-reserved virtual address space
        bool commitSuccess = PageAllocator.CommitVirtualRange(window, _byteSize);

        // Instruct OS Kernel to reclaim physical RAM backing (madvise MADV_DONTNEED / VirtualFree MEM_DECOMMIT)
        bool decommitSuccess = PageAllocator.Decommit(window, _byteSize);

        return commitSuccess && decommitSuccess;
    }
}
