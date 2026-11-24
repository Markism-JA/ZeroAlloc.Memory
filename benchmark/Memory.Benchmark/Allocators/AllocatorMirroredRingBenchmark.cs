using BenchmarkDotNet.Attributes;
using Memory.Allocators;

namespace Memory.Benchmark.Allocators;

[MemoryDiagnoser]
[ShortRunJob]
public unsafe class AllocatorMirroredRingBenchmark
{
    // Benchmark 64KB (Packet Ingress Ring), 1MB (World Broadcast Ring), and 4MB (Shard Master Ring)
    [Params(64 * 1024, 1 * 1024 * 1024, 4 * 1024 * 1024)]
    public int RingSize;

    private byte* _mirroredBuffer;
    private void* _nativeHandle;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _mirroredBuffer = PageAllocator.CreateMirroredBuffer(RingSize, out _nativeHandle);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_mirroredBuffer != null)
        {
            PageAllocator.FreeMirroredBuffer(_mirroredBuffer, RingSize, _nativeHandle);
            _mirroredBuffer = null;
            _nativeHandle = null;
        }
    }

    // =========================================================================
    //  DUAL-VIEW CREATION & TEARDOWN LATENCY
    // =========================================================================

    [Benchmark(Baseline = true)]
    public void Create_And_Free_MirroredBuffer()
    {
        void* handle;
        byte* buffer = PageAllocator.CreateMirroredBuffer(RingSize, out handle);
        PageAllocator.FreeMirroredBuffer(buffer, RingSize, handle);
    }

    // =========================================================================
    //  HARDWARE VIRTUAL PAGE ALIASING (Primary Write -> Mirrored Read)
    // =========================================================================

    [Benchmark]
    public byte Write_Primary_Read_Mirrored()
    {
        byte* primaryView = _mirroredBuffer;
        byte* mirroredView = _mirroredBuffer + RingSize;

        // Write value to primary view at offset 0
        *primaryView = 0xFE;

        // Read value directly from mirrored view at offset 0 (hits the exact same physical RAM frame!)
        byte value = *mirroredView;

        return value; // Prevents RyuJIT dead-code elimination
    }

    // =========================================================================
    //  ZERO-COPY CROSS-BOUNDARY BLIT THROUGHPUT
    // =========================================================================

    [Benchmark]
    public byte ZeroCopy_CrossBoundary_Contiguous_Blit()
    {
        const int payloadSize = 512;
        byte* buffer = _mirroredBuffer;

        // Position write cursor so it straddles the end boundary line of Mirror A
        // (starts 256 bytes before the end of the primary buffer and spills 256 bytes into Mirror B)
        byte* boundaryStraddlePtr = buffer + (RingSize - (payloadSize / 2));

        // Write 512 bytes contiguously without any branching, splits, or modulo wraparound math!
        for (int i = 0; i < payloadSize; i++)
        {
            boundaryStraddlePtr[i] = (byte)i;
        }

        // Verify physical wraparound by reading byte 0 at the head of the primary buffer
        byte wrappedHeadByte = buffer[0];

        return wrappedHeadByte;
    }
}
