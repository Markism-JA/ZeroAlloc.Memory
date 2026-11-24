using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Memory.Allocators;

namespace Memory.Benchmark.Allocators;

[MemoryDiagnoser]
public unsafe class NativeHeapAllocatorBenchmark
{
    // Test realistic game server memory allocation payloads
    [Params(64, 512, 4096, 65536)]
    public int AllocationSize;

    // Test standard SIMD/Cache alignments (16B SSE/AVX, 64B Cache Line)
    [Params(16, 64)]
    public int Alignment;

    private const int _batchCount = 1000;
    private void** _pointerBatchBuffer;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Pre-allocate pointer buffer for batch benchmarks so array creation doesn't pollute GC metrics
        _pointerBatchBuffer = (void**)Marshal.AllocHGlobal(sizeof(void*) * _batchCount);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_pointerBatchBuffer != null)
        {
            Marshal.FreeHGlobal((IntPtr)_pointerBatchBuffer);
            _pointerBatchBuffer = null;
        }
    }

    // =========================================================================
    //  SINGLE ALLOCATION LIFECYCLE (Pure Alloc/Free Latency)
    // =========================================================================

    [Benchmark(Baseline = true)]
    public void NativeMemory_AlignedAlloc_Free()
    {
        void* ptr = NativeMemory.AlignedAlloc((nuint)AllocationSize, (nuint)Alignment);
        NativeMemory.AlignedFree(ptr);
    }

    [Benchmark]
    public void NativeHeapAllocator_PrimaryDispatch()
    {
        void* ptr = HeapAllocator.Allocate(AllocationSize, Alignment);
        HeapAllocator.Free(ptr);
    }

    [Benchmark]
    public void NativeHeapAllocator_Fallback_OSDirect()
    {
        void* ptr = HeapAllocator.Allocate_Fallback(AllocationSize, Alignment);
        HeapAllocator.Free_Fallback(ptr);
    }

    [Benchmark]
    public void Marshal_AllocHGlobal_Free()
    {
        IntPtr ptr = Marshal.AllocHGlobal(AllocationSize);
        Marshal.FreeHGlobal(ptr);
    }

    // =========================================================================
    // 3. BATCH CHURN (Simulates 1000 High-Frequency Server Allocations)
    // =========================================================================

    [Benchmark]
    public void NativeHeapAllocator_Fallback_Batch_Churn_1000()
    {
        int size = AllocationSize;
        int align = Alignment;
        void** buffer = _pointerBatchBuffer;

        // Burst Allocate
        for (int i = 0; i < _batchCount; i++)
            buffer[i] = HeapAllocator.Allocate_Fallback(size, align);

        // Burst Free
        for (int i = 0; i < _batchCount; i++)
            HeapAllocator.Free_Fallback(buffer[i]);
    }
}
