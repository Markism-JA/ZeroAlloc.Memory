using Memory.Allocators;
using Memory.Arena;

namespace Memory.Tests.Arena
{
    public unsafe class UnmanagedArrayTrackTests
    {
        private struct MockEntityData
        {
            public int EntityId;
            public float ValueA;
            public float ValueB;
        }

        [Fact]
        public void CreateOverPointer_ShouldInitializeMetadataCorrectly()
        {
            const int capacity = 100;
            int totalBytes = capacity * sizeof(MockEntityData);

            // Request clean hardware alignment (8 bytes fits the layout validation criteria)
            void* rawMemoryBlock = HeapAllocator.Allocate(totalBytes, 8);

            try
            {
                var track = UnmanagedArrayTrack<MockEntityData>.CreateOverPointer(
                    rawMemoryBlock,
                    capacity
                );

                Assert.True(track.IsCreated);
                Assert.Equal(capacity, track.Length);
                Assert.True(track.RawBufferStart == (byte*)rawMemoryBlock);
            }
            finally
            {
                HeapAllocator.Free(rawMemoryBlock);
            }
        }

        [Fact]
        public void AlignmentCheck_ShouldThrowException_WhenPassedUnalignedAddress()
        {
            const int capacity = 10;
            int totalBytes = capacity * sizeof(MockEntityData);

            // Allocate memory with correct padding
            void* rawMemoryBlock = HeapAllocator.Allocate(totalBytes, 8);

            try
            {
                // Force a misaligned address mutation by shifting forward by 1 single byte
                byte* unalignedAddress = (byte*)rawMemoryBlock + 1;

                Assert.Throws<ArgumentException>(() =>
                {
                    UnmanagedArrayTrack<MockEntityData>.CreateOverPointer(
                        unalignedAddress,
                        capacity
                    );
                });
            }
            finally
            {
                HeapAllocator.Free(rawMemoryBlock);
            }
        }

        [Fact]
        public void GetPointer_ShouldReadWriteDataNativelyWithoutCorruption()
        {
            const int capacity = 5;
            int totalBytes = capacity * sizeof(MockEntityData);
            void* rawMemoryBlock = HeapAllocator.Allocate(totalBytes, 8);

            try
            {
                var track = UnmanagedArrayTrack<MockEntityData>.CreateOverPointer(
                    rawMemoryBlock,
                    capacity
                );

                // Phase 1: Write distinct sequence values directly into raw pointer indices
                for (int i = 0; i < capacity; i++)
                {
                    MockEntityData* elementPtr = track.GetPointer(i);
                    elementPtr->EntityId = 1000 + i;
                    elementPtr->ValueA = i * 1.5f;
                    elementPtr->ValueB = i * 2.5f;
                }

                // Phase 2: Read it back out to verify exact linear pointer offsets match indices
                for (int i = 0; i < capacity; i++)
                {
                    MockEntityData* elementPtr = track.GetPointer(i);

                    Assert.Equal(1000 + i, elementPtr->EntityId);
                    Assert.Equal(i * 1.5f, elementPtr->ValueA);
                    Assert.Equal(i * 2.5f, elementPtr->ValueB);
                }
            }
            finally
            {
                HeapAllocator.Free(rawMemoryBlock);
            }
        }

        [Fact]
        public void DefaultStruct_ReportsNotCreated_AndZeroLength()
        {
            UnmanagedArrayTrack<MockEntityData> uninitialized = default;
            Assert.False(uninitialized.IsCreated);
            Assert.Equal(0, uninitialized.Length);
            Assert.True(uninitialized.RawBufferStart == null);
        }

        [Fact]
        public void CreateOverPointer_ShouldThrow_WhenPointerIsNullOrLengthIsInvalid()
        {
            Assert.Throws<ArgumentNullException>(
                () => UnmanagedArrayTrack<MockEntityData>.CreateOverPointer(null, 4)
            );

            void* rawMemoryBlock = HeapAllocator.Allocate(64, 8);
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => UnmanagedArrayTrack<MockEntityData>.CreateOverPointer(rawMemoryBlock, -1)
                );

                var emptyTrack = UnmanagedArrayTrack<MockEntityData>.CreateOverPointer(
                    rawMemoryBlock,
                    0
                );
                Assert.Equal(0, emptyTrack.Length);
            }
            finally
            {
                HeapAllocator.Free(rawMemoryBlock);
            }
        }
    }
}
