using Memory.Allocators;

namespace Memory.Tests.Allocators
{
    public unsafe class HeapAllocatorTests
    {
        [Theory]
        [InlineData(1024, 16)] // 16-Byte standard structural tracking
        [InlineData(2048, 32)] // 32-Byte AVX vector alignment tracking
        [InlineData(4096, 64)] // 64-Byte Cache-line bounding checks
        public void Allocate_ShouldReturnPointersWithExactRequestedAlignment(
            int bytes,
            int alignment
        )
        {
            void* allocatedMemoryBlockPtr = null;

            try
            {
                // Act: Request raw block from native OS heap mapping
                allocatedMemoryBlockPtr = HeapAllocator.Allocate(bytes, alignment);

                Assert.True(
                    allocatedMemoryBlockPtr != null,
                    "OS Native Heap returned a null reference pointer."
                );

                // Convert address to numeric raw long word for verification math
                ulong addressNumericValue = (ulong)allocatedMemoryBlockPtr;
                ulong alignmentMask = (ulong)(alignment - 1);

                // Assert: The address modulo alignment MUST equal 0.
                // A bitwise AND with the alignment mask handles this calculation at hardware speeds.
                ulong alignmentOffsetRemainder = addressNumericValue & alignmentMask;

                Assert.Equal(0UL, alignmentOffsetRemainder);

                // Confirm memory is actively writable (User-space Ring 3 validation loop)
                byte* volatileAccessByteTracker = (byte*)allocatedMemoryBlockPtr;
                volatileAccessByteTracker[0] = 0xAA;
                volatileAccessByteTracker[bytes - 1] = 0xBB;

                Assert.Equal(0xAA, volatileAccessByteTracker[0]);
                Assert.Equal(0xBB, volatileAccessByteTracker[bytes - 1]);
            }
            finally
            {
                // Clean up (no memory leak)
                if (allocatedMemoryBlockPtr != null)
                {
                    HeapAllocator.Free(allocatedMemoryBlockPtr);
                }
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(24)]
        public void NormalizeAlignment_SnapsInvalidOrSmallAlignmentsToPointerSize(int requested)
        {
            Assert.Equal(sizeof(void*), HeapAllocator.NormalizeAlignment(requested));
        }

        [Theory]
        [InlineData(8, 8)]
        [InlineData(16, 16)]
        [InlineData(64, 64)]
        public void NormalizeAlignment_PreservesValidPowerOfTwoAlignments(int requested, int expected)
        {
            Assert.Equal(expected, HeapAllocator.NormalizeAlignment(requested));
        }

        [Theory]
        [InlineData(128, 16)]
        [InlineData(2048, 64)]
        public void AllocateFallback_AndFreeFallback_WorkWithRequestedAlignment(
            int bytes,
            int alignment
        )
        {
            void* ptr = HeapAllocator.Allocate_Fallback(bytes, alignment);
            try
            {
                Assert.True(ptr != null);
                Assert.Equal(0UL, (ulong)ptr & (ulong)(alignment - 1));

                byte* bytesPtr = (byte*)ptr;
                bytesPtr[0] = 0x55;
                bytesPtr[bytes - 1] = 0x77;
                Assert.Equal(0x55, bytesPtr[0]);
                Assert.Equal(0x77, bytesPtr[bytes - 1]);
            }
            finally
            {
                HeapAllocator.Free_Fallback(ptr);
            }
        }

        [Fact]
        public void Free_NullPointer_IsSafeNoOp()
        {
            HeapAllocator.Free(null);
            HeapAllocator.Free_Fallback(null);
        }
    }
}
