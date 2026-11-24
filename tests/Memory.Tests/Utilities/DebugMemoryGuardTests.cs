using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Tests.Utilities;

public unsafe class DebugMemoryGuardTests
{
    private struct SampleState
    {
        public int Value;
        public long Sequence;
    }

    [Fact]
    public void PoisonBuffer_FillsInDebugAndCompilesOutInRelease()
    {
        const int length = 64;
        byte* buffer = (byte*)HeapAllocator.Allocate(length, 16);

        try
        {
            NativeMemoryBufferUtility.ZeroMemory(buffer, length);
            DebugMemoryGuard.PoisonBuffer(buffer, length);

#if DEBUG
            for (int i = 0; i < length; i++)
            {
                Assert.Equal(DebugMemoryGuard.PoisonByte, buffer[i]);
            }
#else
            for (int i = 0; i < length; i++)
            {
                Assert.Equal(0, buffer[i]);
            }
#endif

            // Null and non-positive lengths are safe no-ops
            DebugMemoryGuard.PoisonBuffer(null, length);
            DebugMemoryGuard.PoisonBuffer(buffer, 0);
        }
        finally
        {
            HeapAllocator.Free(buffer);
        }
    }

    [Fact]
    public void AssertNotPoisoned_SucceedsForInitializedStruct()
    {
        var state = new SampleState { Value = 42, Sequence = 7 };
        DebugMemoryGuard.AssertNotPoisoned(in state);
    }
}
