using System.Runtime.InteropServices;
using Memory.Collections.BoundedMpsc;
using Memory.Collections.MirroredMpsc;
using Memory.Collections.MirroredSpsc;
using Memory.Collections.Spsc;

namespace Memory.Tests.Stress;

[Trait("Category", "Stress")]
public sealed class RingConcurrencyStressTests
{
    [Fact]
    public async Task SpscRing_HighVolumeWrapAround_PreservesOrderAndValues()
    {
        const int capacity = 256;
        const int totalItems = 500_000;

        using var ring = new SpscRing<ulong>(capacity);
        var startBarrier = new ManualResetEventSlim(false);
        ulong? mismatch = null;

        var producer = Task.Run(() =>
        {
            startBarrier.Wait();
            for (ulong i = 0; i < totalItems; i++)
            {
                var spin = new SpinWait();
                while (!ring.TryEnqueue(in i))
                {
                    spin.SpinOnce();
                }
            }
        });

        var consumer = Task.Run(() =>
        {
            startBarrier.Wait();
            var spin = new SpinWait();
            for (ulong expected = 0; expected < totalItems; expected++)
            {
                ulong value;
                while (!ring.TryDequeue(out value))
                {
                    spin.SpinOnce();
                }

                if (value != expected)
                {
                    mismatch ??= expected;
                    break;
                }
            }
        });

        startBarrier.Set();
        await Task.WhenAll(producer, consumer);

        Assert.Null(mismatch);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public async Task BoundedMpscRing_MultiProducerContention_ZeroLostOrCorruptedItems()
    {
        const int producerCount = 4;
        const int itemsPerProducer = 50_000;
        const int totalItems = producerCount * itemsPerProducer;

        using var ring = new BoundedMpscRing<ulong>(elementCapacity: 1024);
        var startBarrier = new ManualResetEventSlim(false);
        var producers = new Task[producerCount];

        for (int p = 0; p < producerCount; p++)
        {
            int producerId = p;
            producers[p] = Task.Run(() =>
            {
                startBarrier.Wait();
                var spin = new SpinWait();
                for (int seq = 0; seq < itemsPerProducer; seq++)
                {
                    ulong packed = ((ulong)(uint)producerId << 32) | (uint)seq;
                    while (!ring.TryWrite(in packed))
                    {
                        spin.SpinOnce();
                    }
                }
            });
        }

        var nextExpectedSeq = new int[producerCount];
        int consumed = 0;
        string? error = null;

        var consumer = Task.Run(() =>
        {
            startBarrier.Wait();
            var spin = new SpinWait();
            while (consumed < totalItems)
            {
                if (!ring.TryRead(out ulong packed))
                {
                    spin.SpinOnce();
                    continue;
                }

                int pid = (int)(packed >> 32);
                int seq = (int)(uint)packed;

                if (pid < 0 || pid >= producerCount)
                {
                    error = $"Invalid producer id {pid}";
                    break;
                }

                if (seq != nextExpectedSeq[pid])
                {
                    error = $"Producer {pid}: expected seq {nextExpectedSeq[pid]}, got {seq}";
                    break;
                }

                nextExpectedSeq[pid]++;
                consumed++;
            }
        });

        startBarrier.Set();
        await Task.WhenAll([.. producers, consumer]);

        Assert.Null(error);
        Assert.Equal(totalItems, consumed);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public async Task MirroredSpscRing_VariableSizeWrapAroundStress_PreservesContiguousPayloads()
    {
        const int totalFrames = 40_000;
        using var ring = new MirroredSpscRing(byteCapacity: 65_536);
        var startBarrier = new ManualResetEventSlim(false);
        string? error = null;

        var producer = Task.Run(() =>
        {
            startBarrier.Wait();
            Span<byte> buffer = stackalloc byte[1024];
            var spin = new SpinWait();

            for (int seq = 0; seq < totalFrames; seq++)
            {
                int payloadLen = 16 + ((seq * 73) % 992); // 16..1007 bytes, forces frequent wrap-around
                uint checksum = ComputePattern(seq, payloadLen, buffer);

                MemoryMarshal.Write(buffer, in seq);
                MemoryMarshal.Write(buffer.Slice(4), in payloadLen);
                MemoryMarshal.Write(buffer.Slice(8), in checksum);

                unsafe
                {
                    fixed (byte* ptr = buffer)
                    {
                        while (!ring.TryWrite(ptr, payloadLen))
                        {
                            spin.SpinOnce();
                        }
                    }
                }
            }
        });

        var consumer = Task.Run(() =>
        {
            startBarrier.Wait();
            var spin = new SpinWait();

            for (int expectedSeq = 0; expectedSeq < totalFrames; expectedSeq++)
            {
                Memory.Collections.MirroredMpsc.BlockView view;
                while (!ring.TryRead(out view))
                {
                    spin.SpinOnce();
                }

                var span = view.AsSpan();
                int seq = MemoryMarshal.Read<int>(span);
                int len = MemoryMarshal.Read<int>(span.Slice(4));
                uint checksum = MemoryMarshal.Read<uint>(span.Slice(8));

                if (seq != expectedSeq || len != view.Length || checksum != VerifyPattern(seq, span))
                {
                    error = $"Corrupted frame at seq={expectedSeq}: got seq={seq}, len={len}/{view.Length}";
                    ring.AdvanceConsumer(in view);
                    break;
                }

                ring.AdvanceConsumer(in view);
            }
        });

        startBarrier.Set();
        await Task.WhenAll(producer, consumer);

        Assert.Null(error);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public async Task MirroredMpscRing_MultiProducerVariableSizeStress_ZeroCorruption()
    {
        const int producerCount = 4;
        const int framesPerProducer = 15_000;
        const int totalFrames = producerCount * framesPerProducer;

        using var ring = new MirroredMpscRing(byteCapacity: 65_536);
        var startBarrier = new ManualResetEventSlim(false);
        var producers = new Task[producerCount];

        for (int p = 0; p < producerCount; p++)
        {
            int producerId = p;
            producers[p] = Task.Run(() =>
            {
                startBarrier.Wait();
                Span<byte> buffer = stackalloc byte[512];
                var spin = new SpinWait();

                for (int seq = 0; seq < framesPerProducer; seq++)
                {
                    int payloadLen = 16 + (((seq * 37) + producerId * 19) % 480); // 16..495 bytes
                    uint checksum = ComputePattern((producerId << 20) ^ seq, payloadLen, buffer);

                    MemoryMarshal.Write(buffer, in producerId);
                    MemoryMarshal.Write(buffer.Slice(4), in seq);
                    MemoryMarshal.Write(buffer.Slice(8), in payloadLen);
                    MemoryMarshal.Write(buffer.Slice(12), in checksum);

                    while (!ring.TryWrite(buffer.Slice(0, payloadLen)))
                    {
                        spin.SpinOnce();
                    }
                }
            });
        }

        var nextExpectedSeq = new int[producerCount];
        int consumed = 0;
        string? error = null;

        var consumer = Task.Run(() =>
        {
            startBarrier.Wait();
            var spin = new SpinWait();

            while (consumed < totalFrames)
            {
                if (!ring.TryRead(out Memory.Collections.MirroredMpsc.BlockView view))
                {
                    spin.SpinOnce();
                    continue;
                }

                var span = view.AsSpan();
                int pid = MemoryMarshal.Read<int>(span);
                int seq = MemoryMarshal.Read<int>(span.Slice(4));
                int len = MemoryMarshal.Read<int>(span.Slice(8));
                uint checksum = MemoryMarshal.Read<uint>(span.Slice(12));

                if (pid < 0 || pid >= producerCount || seq != nextExpectedSeq[pid] || len != view.Length)
                {
                    error = $"Frame mismatch: pid={pid}, seq={seq}, len={len}/{view.Length}";
                    ring.AdvanceConsumer(in view);
                    break;
                }

                uint expectedChecksum = VerifyPattern((pid << 20) ^ seq, span, headerBytes: 16);
                if (checksum != expectedChecksum)
                {
                    error = $"Checksum mismatch at pid={pid}, seq={seq}";
                    ring.AdvanceConsumer(in view);
                    break;
                }

                nextExpectedSeq[pid]++;
                consumed++;
                ring.AdvanceConsumer(in view);
            }
        });

        startBarrier.Set();
        await Task.WhenAll([.. producers, consumer]);

        Assert.Null(error);
        Assert.Equal(totalFrames, consumed);
        Assert.True(ring.IsEmpty);
    }

    private static uint ComputePattern(int seed, int length, Span<byte> buffer)
    {
        uint hash = 2166136261u;
        for (int i = 16; i < length; i++)
        {
            byte b = (byte)((seed * 31 + i * 17) & 0xFF);
            buffer[i] = b;
            hash = (hash ^ b) * 16777619u;
        }
        return hash;
    }

    private static uint VerifyPattern(int seed, ReadOnlySpan<byte> span, int headerBytes = 16)
    {
        uint hash = 2166136261u;
        for (int i = headerBytes; i < span.Length; i++)
        {
            byte b = span[i];
            hash = (hash ^ b) * 16777619u;
        }
        return hash;
    }
}
