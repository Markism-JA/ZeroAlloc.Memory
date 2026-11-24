using BenchmarkDotNet.Attributes;
using Memory.Allocators;
using Memory.Arena;

namespace Memory.Benchmark.Arena;

[MemoryDiagnoser]
[ShortRunJob]
public unsafe class UnmanagedArrayTrackBenchmark
{
    public struct TestComponent
    {
        public float X,
            Y,
            Z,
            W; // 16-byte SIMD vector layout
    }

    [Params(1000, 10000, 100000)]
    public int ElementCount;

    private byte* _rawBuffer;
    private UnmanagedArrayTrack<TestComponent> _arrayTrack;
    private int[] _randomIndices = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        int totalBytes = ElementCount * sizeof(TestComponent);
        _rawBuffer = (byte*)HeapAllocator.Allocate(totalBytes, 64);
        _arrayTrack = UnmanagedArrayTrack<TestComponent>.CreateOverPointer(
            _rawBuffer,
            ElementCount
        );

        // Pre-generate randomized index map to test CPU cache-line miss penalty
        var random = new Random(42);
        _randomIndices = new int[ElementCount];
        for (int i = 0; i < ElementCount; i++)
        {
            _randomIndices[i] = random.Next(0, ElementCount);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_rawBuffer != null)
        {
            HeapAllocator.Free(_rawBuffer);
            _rawBuffer = null;
        }
    }

    // =========================================================================
    //  SEQUENTIAL ITERATION THROUGHPUT (Cache-Friendly Stride)
    // =========================================================================

    [Benchmark(Baseline = true)]
    public float Sequential_Read_Sum()
    {
        float sum = 0f;
        int count = _arrayTrack.Length;

        for (int i = 0; i < count; i++)
        {
            TestComponent* comp = _arrayTrack.GetPointer(i);
            sum += comp->X + comp->Y + comp->Z + comp->W;
        }

        return sum; // Prevents RyuJIT dead-code elimination
    }

    [Benchmark]
    public void Sequential_Write_Mutate()
    {
        int count = _arrayTrack.Length;

        for (int i = 0; i < count; i++)
        {
            TestComponent* comp = _arrayTrack.GetPointer(i);
            comp->X = i;
            comp->Y = i * 2f;
            comp->Z = i * 3f;
            comp->W = i * 4f;
        }
    }

    // =========================================================================
    //  RANDOM ACCESS THROUGHPUT (L1/L2 Cache Line Stalls)
    // =========================================================================

    [Benchmark]
    public float Random_Access_Read_Sum()
    {
        float sum = 0f;
        int[] indices = _randomIndices;
        int count = indices.Length;

        for (int i = 0; i < count; i++)
        {
            TestComponent* comp = _arrayTrack.GetPointer(indices[i]);
            sum += comp->X;
        }

        return sum;
    }
}
