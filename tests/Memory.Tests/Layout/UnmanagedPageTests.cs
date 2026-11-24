using Memory.Layout;

namespace Memory.Tests.Layout
{
    public unsafe class UnmanagedPageTests
    {
        [Fact]
        public void Constructor_ShouldEnforceCacheLinePaddingAndInitializeGuard()
        {
            // Request 10 bytes — should be padded out up to the nearest 64-byte cache line marker
            const int capacity = 10;

            using var page = new UnmanagedPage(capacity);

            // Verify raw base pointer is cleanly aligned to 64 bytes out of our NativeHeapAllocator
            ulong address = (ulong)page.Carve(0, 1);
            Assert.Equal(0UL, address & 63UL);

            // Assert integrity doesn't throw under fresh structural configurations
            page.AssertIntegrity();
        }

        [Theory]
        [InlineData(8, 8)]
        [InlineData(16, 16)]
        [InlineData(32, 32)]
        [InlineData(64, 64)]
        public void Carve_ShouldRespectCustomDynamicAlignmentDemands(int bytes, int alignment)
        {
            using var page = new UnmanagedPage(1024);

            // First carve to throw off the natural alignment offset positioning balance
            page.Carve(3, 1);

            // Act: Carve the actual requested aligned segment block slice
            void* alignedBlockPtr = page.Carve(bytes, alignment);

            // Assert: The calculated address must modulo cleanly to 0 against the alignment criteria
            ulong addressValue = (ulong)alignedBlockPtr;
            ulong alignmentMask = (ulong)(alignment - 1);

            Assert.Equal(0UL, addressValue & alignmentMask);
        }

        [Fact]
        public void Carve_ShouldThrowOutOfMemoryException_WhenCapacityIsExhausted()
        {
            // Initialize a page matching exactly one cache line block size limit boundary
            using var page = new UnmanagedPage(64);

            // Completely consume the available byte landscape space lane
            page.Carve(64, 1);

            // Attempting to carve even 1 additional byte must trigger an immediate safety boundary exception
            Assert.Throws<OutOfMemoryException>(() =>
            {
                page.Carve(1, 1);
            });
        }

        [Fact]
        public void CarveZeroed_ShouldInitializeMemoryToZero_OnFreshAllocation()
        {
            using var page = new UnmanagedPage(256);
            const int bytesNeeded = 128;

            // Act: Carve a zero-initialized block
            void* zeroedPtr = page.CarveZeroed(bytesNeeded);
            byte* bytePtr = (byte*)zeroedPtr;

            // Assert: All bytes in the allocated segment must be strictly 0
            for (int i = 0; i < bytesNeeded; i++)
            {
                Assert.Equal(0, bytePtr[i]);
            }
        }

        [Fact]
        public void CarveZeroed_ShouldWipePreviousData_OnRecycledPage()
        {
            using var page = new UnmanagedPage(256);
            const int bytesNeeded = 128;

            // 1. Fill memory with dirty/garbage data using standard, un-cleared Carve
            void* rawPtr = page.Carve(bytesNeeded);
            byte* bytePtr = (byte*)rawPtr;
            for (int i = 0; i < bytesNeeded; i++)
            {
                bytePtr[i] = 0xAA;
            }

            // 2. Reset the page (drops offset back to 0, leaving heap memory dirty)
            page.Reset();

            // 3. Act: Re-carve the exact same address range using CarveZeroed
            void* zeroedPtr = page.CarveZeroed(bytesNeeded, 16);
            byte* zeroedBytePtr = (byte*)zeroedPtr;

            // Assert: Addresses must match to confirm we are running our test over recycled memory
            Assert.True(
                rawPtr == zeroedPtr,
                "Addresses must match to verify heap address recycling."
            );

            // Assert: Recycled memory must be completely wiped of the previous 0xAA values
            for (int i = 0; i < bytesNeeded; i++)
            {
                Assert.Equal(0, zeroedBytePtr[i]);
            }
        }

        [Fact]
        public void Reset_ShouldRewindCarveOffset_WithoutClearingCanaryGuard()
        {
            using var page = new UnmanagedPage(128);
            void* firstAddress = page.Carve(32, 16);

            // Act: Rewind the internal state metrics allocation register tracking values
            page.Reset();

            // Re-carving after a reset pass should yield the exact identical root tracking address map
            void* recycledAddress = page.Carve(32, 16);

            Assert.True(firstAddress == recycledAddress);
            page.AssertIntegrity(); // Canary must remain uncorrupted and valid
        }

        [Fact]
        public void AssertIntegrity_ShouldCatchStompedCanary_WhenMemoryIsOverrun()
        {
            // Instantiate without the 'using' block pattern so we can manually simulate corruptions safely
            var page = new UnmanagedPage(64);

            try
            {
                // Act: Forcefully break out of the high-level API wrapper boundary line rules.
                // We deliberately locate the exact unmanaged offset where our 0xDEADBEEF canary sits
                // and overwrite it to simulate an out-of-bounds array write stomp.
                byte* memoryHead = (byte*)page.Carve(0, 1);

                // Pad out past the 64-byte total size to access the hidden trailing uint slot direct address
                uint* rawCanaryAddress = (uint*)(memoryHead + 64);

                // Overwrite the memory address
                *rawCanaryAddress = 0x00000000;

                bool isDebugMode = false;
#if DEBUG
                isDebugMode = true;
#endif

                if (isDebugMode)
                {
                    // Assert that the security guard successfully intercepting the corrupted data profile
                    Assert.Throws<InvalidProgramException>(page.AssertIntegrity);

                    // Assert that Dispose also triggers an integrity failure check before freeing to avoid silent leaks
                    Assert.Throws<InvalidProgramException>(() => page.Dispose());
                }
                else
                {
                    // In Release configurations, the compiler omits the conditional compilation checking block loops for speed
                    page.AssertIntegrity(); // Should execute safely as a no-op statement pass
                    page.Dispose(); // Should free the allocation normally without throwing
                }
            }
            finally
            {
                // Absolute structural verification cleanup step in case exception tracking gates bypass assertions
                byte* memoryHead = (byte*)page.Carve(0, 1);
                uint* rawCanaryAddress = (uint*)(memoryHead + 64);
                *rawCanaryAddress = 0xDEADBEEF;

                page.Dispose();
            }
        }
    }
}
