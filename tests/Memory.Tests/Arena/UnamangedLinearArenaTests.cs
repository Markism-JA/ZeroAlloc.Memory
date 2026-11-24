using Memory.Allocators;
using Memory.Arena;

namespace Memory.Tests.Arena
{
    public unsafe class UnmanagedLinearArenaTests
    {
        private struct MockTransform
        {
            public float X,
                Y,
                Z;
        }

        [Fact]
        public void InitialState_ShouldMatchCreatedMetadata()
        {
            const int totalBytes = 4096;
            void* rawMemoryBlock = HeapAllocator.Allocate(totalBytes, 8);

            try
            {
                var arena = UnmanagedLinearArena<MockTransform>.CreateOverPointer(
                    rawMemoryBlock,
                    totalBytes,
                    AllocationType.UserSpaceHeap
                );

                Assert.True(arena.IsCreated);
                Assert.Equal(totalBytes, arena.CapacityInBytes);
                Assert.Equal(0, arena.AllocatedBytes);
                Assert.Equal(0f, arena.UtilizationRatio);
                Assert.True(arena.RawBufferStart == (byte*)rawMemoryBlock);
            }
            finally
            {
                HeapAllocator.Free(rawMemoryBlock);
            }
        }

        [Fact]
        public void SequentialLeases_ShouldAdvancePointerOffsetsLinearly()
        {
            // Allocate space for exactly 3 structures
            int elementSize = sizeof(MockTransform);
            int totalBytes = elementSize * 3;
            void* rawMemoryBlock = HeapAllocator.Allocate(totalBytes, 8);

            try
            {
                var arena = UnmanagedLinearArena<MockTransform>.CreateOverPointer(
                    rawMemoryBlock,
                    totalBytes
                );

                // Lease 1 element
                MockTransform* ptr1 = arena.Lease(1);
                Assert.True(ptr1 == (MockTransform*)rawMemoryBlock);
                Assert.Equal(elementSize, arena.AllocatedBytes);

                // Lease 2 more elements
                MockTransform* ptr2 = arena.Lease(2);
                Assert.True(ptr2 == (MockTransform*)((byte*)rawMemoryBlock + elementSize));
                Assert.Equal(totalBytes, arena.AllocatedBytes);
                Assert.Equal(1f, arena.UtilizationRatio);

                // Verify space exhaustion triggers exception safely
                Assert.Throws<OutOfMemoryException>(() => arena.Lease(1));
            }
            finally
            {
                HeapAllocator.Free(rawMemoryBlock);
            }
        }

        [Fact]
        public void Reset_ShouldRewindAllocationIndex_WithoutZeroingBackingMemory()
        {
            int totalBytes = sizeof(MockTransform) * 2;
            void* rawMemoryBlock = HeapAllocator.Allocate(totalBytes, 8);

            try
            {
                var arena = UnmanagedLinearArena<MockTransform>.CreateOverPointer(
                    rawMemoryBlock,
                    totalBytes
                );

                MockTransform* originalPtr = arena.Lease(1);
                originalPtr->X = 99.9f;

                // Reset should only rewind the user-space internal integer index pointer
                arena.Reset();
                Assert.Equal(0, arena.AllocatedBytes);

                // Re-leasing the same spot should yield the exact same address block
                MockTransform* recycledPtr = arena.Lease(1);
                Assert.True(originalPtr == recycledPtr);
                Assert.Equal(99.9f, recycledPtr->X); // Raw data persists until manually overwritten
            }
            finally
            {
                HeapAllocator.Free(rawMemoryBlock);
            }
        }

        [Fact]
        public void ConcurrentLeasing_ShouldMaintainStrictInterlockedIntegrity()
        {
            const int parallelWorkerCount = 8;
            const int elementsPerWorker = 500;
            const int totalExpectedElements = parallelWorkerCount * elementsPerWorker;

            int elementSize = sizeof(MockTransform);
            int totalBytes = totalExpectedElements * elementSize;

            void* rawMemoryBlock = HeapAllocator.Allocate(totalBytes, 8);

            try
            {
                // Instantiate the structural struct arena interface instance
                var arena = UnmanagedLinearArena<MockTransform>.CreateOverPointer(
                    rawMemoryBlock,
                    totalBytes
                );

                // Hammer the allocation loop lock-free from multiple background tasks
                Parallel.For(
                    0,
                    parallelWorkerCount,
                    w =>
                    {
                        for (int i = 0; i < elementsPerWorker; i++)
                        {
                            MockTransform* element = arena.Lease(1);
                            Assert.True(
                                element != null,
                                "Arena atomic tracking slipped and prematurely hit exhaustion."
                            );

                            // Stamp a unique ID marker straight down to the unmanaged memory lane
                            element->X = (w * 10000) + i;
                        }
                    }
                );

                // Verify index tracked total bytes matches exactly
                Assert.Equal(totalBytes, arena.AllocatedBytes);
            }
            finally
            {
                HeapAllocator.Free(rawMemoryBlock);
            }
        }

        [Fact]
        public void TrimBackingMemory_ShouldExecuteSafely_OnValidKernelAllocations()
        {
            // Request dynamic hardware sizes: allocating 4 structural pages
            int systemPageSize = PageAllocator.SystemPageSize;
            int totalBytes = systemPageSize * 4;

            // Allocate a true Virtual Address block via OS Syscall
            void* virtualBlockPtr = PageAllocator.ReserveAddressSpace(totalBytes);
            PageAllocator.CommitVirtualRange(virtualBlockPtr, totalBytes);

            try
            {
                var arena = UnmanagedLinearArena<byte>.CreateOverPointer(
                    virtualBlockPtr,
                    totalBytes,
                    AllocationType.KernelVirtual
                );

                // Use exactly 1 page out of the 4 pages reserved
                arena.Lease(systemPageSize);
                Assert.Equal(systemPageSize, arena.AllocatedBytes);

                // Act: Trigger the dynamic page alignment calculation pass
                // Should cleanly determine that 3 trailing pages are unused weight and drop them
                arena.TrimBackingMemory();

                // Memory remains valid and accessible at the active lease address range
                byte* validMemorySlice = arena.RawBufferStart;
                validMemorySlice[0] = 0xAA;
                validMemorySlice[systemPageSize - 1] = 0xBB;

                Assert.Equal(0xAA, validMemorySlice[0]);
                Assert.Equal(0xBB, validMemorySlice[systemPageSize - 1]);
            }
            finally
            {
                if (virtualBlockPtr != null)
                {
                    PageAllocator.ReleaseVirtual(virtualBlockPtr, totalBytes);
                }
            }
        }
    }
}
