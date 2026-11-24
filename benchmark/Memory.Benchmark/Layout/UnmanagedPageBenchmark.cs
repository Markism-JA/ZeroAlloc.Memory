using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Memory.Layout;

namespace Memory.Benchmark.Layout;

[MemoryDiagnoser]
public unsafe class UnmanagedPageBenchmark
{
    // Test typical scratchpad slices: 32B (Transform/Vector), 128B (Raycast hit data), 512B (Packet payload)
    [Params(32, 128, 512)]
    public int SliceSize;

    // Test standard SSE/AVX (16B) and CPU Cache-Line (64B) alignments
    [Params(16, 64)]
    public int Alignment;

    private const int PageCapacity = 2 * 1024 * 1024; // 2MB Page Buffer
    private const int CarveCountPerIteration = 1000;

    private UnmanagedPage _page;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _page = new UnmanagedPage(PageCapacity);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _page.Dispose();
    }

    // =========================================================================
    // RAW BUMP SLICING THROUGHPUT (Carve vs CarveZeroed)
    // =========================================================================

    [Benchmark(Baseline = true)]
    public void* Carve_Dirty_Sequential_1000()
    {
        int size = SliceSize;
        int align = Alignment;
        void* lastPtr = null;

        // Micro-benchmark 1,000 sequential bump allocations
        for (int i = 0; i < CarveCountPerIteration; i++)
        {
            lastPtr = _page.Carve(size, align);
        }

        _page.Reset();
        return lastPtr; // Prevents RyuJIT dead-code elimination
    }

    [Benchmark]
    public void* CarveZeroed_Sequential_1000()
    {
        int size = SliceSize;
        int align = Alignment;
        void* lastPtr = null;

        for (int i = 0; i < CarveCountPerIteration; i++)
        {
            lastPtr = _page.CarveZeroed(size, align);
        }

        _page.Reset();
        return lastPtr;
    }

    // =========================================================================
    // SLICE & WRITE (Simulates Physics BVH Scratch & Packet Serialization)
    // =========================================================================

    [Benchmark]
    public byte Carve_And_Write_Payload_1000()
    {
        int size = SliceSize;
        int align = Alignment;
        byte lastByte = 0;

        for (int i = 0; i < CarveCountPerIteration; i++)
        {
            byte* slice = (byte*)_page.Carve(size, align);

            // Write to head and tail of carved slice to test cache-line writing
            slice[0] = (byte)i;
            slice[size - 1] = (byte)(i ^ 0xFF);

            lastByte = slice[size - 1];
        }

        _page.Reset();
        return lastByte;
    }

    // =========================================================================
    // VALUE-TYPE STACK-PASSING OVERHEAD (Single 8B Register Verification)
    // =========================================================================

    [Benchmark]
    public void* Carve_PassedByValue_StackCopy()
    {
        // Passes 8-byte UnmanagedPage struct by value to verify 0ns register passing in RyuJIT
        void* ptr = ExecuteCarveByValue(_page, SliceSize, Alignment);
        _page.Reset();
        return ptr;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void* ExecuteCarveByValue(UnmanagedPage pageCopy, int size, int align)
    {
        return pageCopy.Carve(size, align);
    }

    // =========================================================================
    // TICK RESET & TAIL CANARY STAMPING COST
    // =========================================================================

    [Benchmark]
    public void Reset_And_AssertIntegrity_Lifecycle()
    {
        _page.Carve(SliceSize, Alignment);
        _page.Reset();
        _page.AssertIntegrity();
    }
}
