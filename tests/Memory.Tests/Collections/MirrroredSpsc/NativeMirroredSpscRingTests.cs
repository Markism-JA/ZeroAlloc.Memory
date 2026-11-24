using Memory.Collections.MirroredMpsc;
using Memory.Collections.MirroredSpsc;

namespace Memory.Tests.Collections.MirrroredSpsc;

public unsafe class NativeMirroredSpscRingTests
{
    private const int _testCapacity = 64 * 1024; // 64 KB hardware boundary
    #region Lifecycle & Allocator Semantics

    [Fact]
    public void Allocate_And_Free_DirectLifecycle_Succeeds()
    {
        NativeMirroredSpscRing* ring = NativeMirroredSpscRing.Allocate(_testCapacity);
        try
        {
            Assert.True(ring != null);
            Assert.True(ring->IsEmpty);
            Assert.Equal(_testCapacity, ring->Capacity);
            Assert.Equal(0, ring->OccupiedBytes);
            Assert.Equal(_testCapacity, ring->FreeBytes);
            Assert.Equal(0, ring->HeadSequence);
            Assert.Equal(0, ring->TailSequence);
        }
        finally
        {
            NativeMirroredSpscRing.Free(ring);
        }
    }

    [Fact]
    public void Allocate_NegativeOrZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeMirroredSpscRing.Allocate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeMirroredSpscRing.Allocate(-4096));
    }

    [Fact]
    public void Free_NullPointer_SafeNoOp()
    {
        // Must not crash or fault on null
        NativeMirroredSpscRing.Free(null);
    }

    #endregion

    #region Native Two-Phase Reservations (Raw Tickets)

    [Fact]
    public void RawTicket_Reserve_Commit_PublishesFrame()
    {
        NativeMirroredSpscRing* ring = NativeMirroredSpscRing.Allocate(_testCapacity);
        try
        {
            const int payloadSize = 16;
            bool reserved = ring->TryReserve(
                payloadSize,
                out byte* dest,
                out SpscWriteTicket ticket
            );
            Assert.True(reserved);
            Assert.True(dest != null);

            // Populate uncommitted payload memory
            *(ulong*)dest = 0x1122334455667788UL;
            *(ulong*)(dest + 8) = 0x99AABBCCDDEEFF00UL;

            // Consumer must still see an empty ring
            Assert.True(ring->IsEmpty);
            Assert.False(ring->TryRead(out _));

            // Commit ticket
            ring->CommitWrite(in ticket);

            Assert.False(ring->IsEmpty);
            Assert.True(ring->TryRead(out BlockView view));
            Assert.Equal(payloadSize, view.Length);
            Assert.Equal(0x1122334455667788UL, *(ulong*)view.Payload);
            Assert.Equal(0x99AABBCCDDEEFF00UL, *(ulong*)(view.Payload + 8));

            ring->AdvanceConsumer(in view);
            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredSpscRing.Free(ring);
        }
    }

    [Fact]
    public void RawTicket_AbortReserve_DoesNotAdvanceTail()
    {
        NativeMirroredSpscRing* ring = NativeMirroredSpscRing.Allocate(_testCapacity);
        try
        {
            long initialTail = ring->TailSequence;

            Assert.True(ring->TryReserve(32, out byte* dest, out SpscWriteTicket ticket));
            Assert.True(dest != null);

            // Abort the reservation
            ring->AbortReserve(in ticket);

            // A redundant CommitWrite on an aborted ticket must safely NO-OP
            ring->CommitWrite(in ticket);

            Assert.Equal(initialTail, ring->TailSequence);
            Assert.True(ring->IsEmpty);
            Assert.False(ring->TryRead(out _));

            // Subsequent direct write must succeed at the original tail position
            int value = 0xCAFE;
            Assert.True(ring->TryWrite((byte*)&value, sizeof(int)));
            Assert.True(ring->TryRead(out BlockView view));
            Assert.Equal(0xCAFE, *(int*)view.Payload);
            ring->AdvanceConsumer(in view);
        }
        finally
        {
            NativeMirroredSpscRing.Free(ring);
        }
    }

    #endregion

    #region Native Amortized Batch Writes (Working Tail Register)

    [Fact]
    public void UncommittedWrites_AccumulateInWorkingTail_PublishedOnCommitBatch()
    {
        NativeMirroredSpscRing* ring = NativeMirroredSpscRing.Allocate(_testCapacity);
        try
        {
            long initialTail = ring->GetProducerTail();
            long workingTail = initialTail;

            const int count = 4;
            for (int i = 0; i < count; i++)
            {
                int val = (i + 1) * 111;
                bool ok = ring->TryWriteUncommitted(
                    (byte*)&val,
                    sizeof(int),
                    null,
                    0,
                    ref workingTail
                );
                Assert.True(ok);
            }

            // TailSequence has not moved; ring appears empty to consumer
            Assert.Equal(initialTail, ring->TailSequence);
            Assert.True(ring->IsEmpty);

            // Publish all 4 items at once with a single Store-Release
            ring->CommitBatch(workingTail);

            Assert.Equal(workingTail, ring->TailSequence);
            Assert.False(ring->IsEmpty);

            for (int i = 0; i < count; i++)
            {
                Assert.True(ring->TryRead(out BlockView view));
                Assert.Equal((i + 1) * 111, *(int*)view.Payload);
                ring->AdvanceConsumer(in view);
            }

            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredSpscRing.Free(ring);
        }
    }

    [Fact]
    public void UncommittedReservations_WriteInPlace_PublishedOnCommitBatch()
    {
        NativeMirroredSpscRing* ring = NativeMirroredSpscRing.Allocate(_testCapacity);
        try
        {
            long workingTail = ring->GetProducerTail();

            for (int i = 0; i < 3; i++)
            {
                bool reserved = ring->TryReserveUncommitted(
                    sizeof(int),
                    ref workingTail,
                    out byte* dest
                );
                Assert.True(reserved);
                *(int*)dest = i + 10;
            }

            Assert.True(ring->IsEmpty);

            ring->CommitBatch(workingTail);

            for (int i = 0; i < 3; i++)
            {
                Assert.True(ring->TryRead(out BlockView view));
                Assert.Equal(i + 10, *(int*)view.Payload);
                ring->AdvanceConsumer(in view);
            }

            Assert.True(ring->IsEmpty);
        }
        finally
        {
            NativeMirroredSpscRing.Free(ring);
        }
    }

    #endregion

    #region Manual CommitRead Advancement

    [Fact]
    public void CommitRead_AdvancesHeadDirectlyByByteCount()
    {
        NativeMirroredSpscRing* ring = NativeMirroredSpscRing.Allocate(_testCapacity);
        try
        {
            int val = 42;
            Assert.True(ring->TryWrite((byte*)&val, sizeof(int)));

            Assert.True(ring->TryRead(out BlockView view));
            Assert.Equal(42, *(int*)view.Payload);

            // Instead of AdvanceConsumer(in view), advance directly by AlignedTotalSize
            ring->CommitRead(view.AlignedTotalSize);

            Assert.True(ring->IsEmpty);
            Assert.Equal(0, ring->OccupiedBytes);
        }
        finally
        {
            NativeMirroredSpscRing.Free(ring);
        }
    }

    #endregion
}
