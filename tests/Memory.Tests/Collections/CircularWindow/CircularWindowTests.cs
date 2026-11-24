using System.Runtime.InteropServices;
using Memory.Collections.CircularWindow;

namespace Memory.Tests.Collections.CircularWindow;

public class CircularWindowTests
{
    private const int _defaultCapacity = 256;

    #region 1. Basic Append & ReadLatest (Value Preservation & Chronological Order)

    [Fact]
    public void Append_And_ReadLatest_SingleItem_PreservesPayload()
    {
        using var window = new CircularWindow<TestFrame>(_defaultCapacity);
        Assert.Equal(0, window.WriteCursor);

        var frame = new TestFrame
        {
            Timestamp = 123456789L,
            Data = 0xDEADBEEFCAFEBABEU,
            Checksum = 0xAA55AA55U,
            Id = 42,
        };

        window.Append(in frame);
        Assert.Equal(1, window.WriteCursor);

        Span<TestFrame> dest = stackalloc TestFrame[1];
        int readCount = window.ReadLatest(dest);

        Assert.Equal(1, readCount);
        Assert.Equal(frame.Timestamp, dest[0].Timestamp);
        Assert.Equal(frame.Data, dest[0].Data);
        Assert.Equal(frame.Checksum, dest[0].Checksum);
        Assert.Equal(frame.Id, dest[0].Id);
    }

    [Fact]
    public void AppendBatch_Span_ReadsBackInChronologicalOrder()
    {
        using var window = new CircularWindow<TestFrame>(_defaultCapacity);
        const int count = 10;

        Span<TestFrame> source = stackalloc TestFrame[count];
        for (int i = 0; i < count; i++)
        {
            source[i] = new TestFrame { Timestamp = i * 10, Id = i };
        }

        window.AppendBatch(source);
        Assert.Equal(count, window.WriteCursor);

        Span<TestFrame> readBuffer = stackalloc TestFrame[count];
        int readCount = window.ReadLatest(readBuffer);

        Assert.Equal(count, readCount);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i * 10, readBuffer[i].Timestamp);
            Assert.Equal(i, readBuffer[i].Id);
        }
    }

    [Fact]
    public void ReadLatest_RequestingMoreThanAvailable_ReturnsOnlyAvailableCount()
    {
        using var window = new CircularWindow<TestFrame>(_defaultCapacity);
        const int available = 5;

        for (int i = 0; i < available; i++)
        {
            var frame = new TestFrame { Id = i };
            window.Append(in frame);
        }

        Span<TestFrame> largeBuffer = stackalloc TestFrame[32];
        int readCount = window.ReadLatest(largeBuffer);

        Assert.Equal(available, readCount);
        for (int i = 0; i < available; i++)
        {
            Assert.Equal(i, largeBuffer[i].Id);
        }
    }

    #endregion

    #region 2. Lossy Wrap-Around & Overwrite Semantics

    [Fact]
    public void Append_ExceedingCapacity_OverwritesOldestEntriesRetainingLatestN()
    {
        const int capacity = 64; // Minimum valid capacity
        using var window = new CircularWindow<TestFrame>(capacity);

        const int totalAppends = 100;
        for (int i = 0; i < totalAppends; i++)
        {
            var frame = new TestFrame { Timestamp = i * 1000L, Id = i };
            window.Append(in frame);
        }

        Assert.Equal(totalAppends, window.WriteCursor);

        Span<TestFrame> readBuffer = stackalloc TestFrame[capacity];
        int readCount = window.ReadLatest(readBuffer);

        Assert.Equal(capacity, readCount);

        // Expect exactly the last 64 elements: [36 .. 99]
        const int expectedStartId = totalAppends - capacity;
        for (int i = 0; i < capacity; i++)
        {
            int expectedId = expectedStartId + i;
            Assert.Equal(expectedId, readBuffer[i].Id);
            Assert.Equal(expectedId * 1000L, readBuffer[i].Timestamp);
        }
    }

    #endregion

    #region 3. Predicate Filtered Queries

    [Fact]
    public unsafe void QueryFiltered_MatchesAndReversesToChronologicalOrder()
    {
        using var window = new CircularWindow<TestFrame>(_defaultCapacity);
        for (int i = 0; i < 20; i++)
        {
            var frame = new TestFrame { Timestamp = i, Id = i };
            window.Append(in frame);
        }

        Span<TestFrame> matches = stackalloc TestFrame[10];

        // Filter: match only even IDs
        delegate* <void*, TestFrame*, int> filterEven = &FilterEvenIds;
        int matchedCount = window.QueryFiltered(matches, null, filterEven);

        Assert.Equal(10, matchedCount);
        // Destination must be in chronological order: 0, 2, 4, 6, 8, 10, 12, 14, 16, 18
        for (int i = 0; i < matchedCount; i++)
        {
            Assert.Equal(i * 2, matches[i].Id);
        }
    }

    [Fact]
    public unsafe void QueryFiltered_TerminalCutoff_HaltsBackwardsTraversal()
    {
        using var window = new CircularWindow<TestFrame>(_defaultCapacity);
        for (int i = 0; i < 50; i++)
        {
            var frame = new TestFrame { Timestamp = i * 10, Id = i };
            window.Append(in frame);
        }

        Span<TestFrame> matches = stackalloc TestFrame[50];
        long minTimestampCutoff = 350; // Halt when timestamp < 350

        delegate* <void*, TestFrame*, int> cutoffFilter = &FilterWithTimestampCutoff;
        int matchedCount = window.QueryFiltered(matches, &minTimestampCutoff, cutoffFilter);

        // Items matching >= 350: 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470, 480, 490 -> 15 items
        Assert.Equal(15, matchedCount);
        Assert.Equal(350, matches[0].Timestamp);
        Assert.Equal(490, matches[matchedCount - 1].Timestamp);
    }

    #endregion

    #region 4. Capacity Sizing & Input Validation

    [Fact]
    public void Constructor_InvalidDimensions_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CircularWindow<TestFrame>(0));
        Assert.Throws<ArgumentException>(() => new CircularWindow<TestFrame>(-64));
        Assert.Throws<ArgumentException>(() => new CircularWindow<TestFrame>(32)); // Less than 64
        Assert.Throws<ArgumentException>(() => new CircularWindow<TestFrame>(100)); // Not a power of 2
    }

    [Fact]
    public void Constructor_ExceedingIntMaxValueBytes_ThrowsArgumentOutOfRangeException()
    {
        // TestFrame = 24 bytes -> CircularWindowSlot<TestFrame> = 32 bytes (8B sequence + 24B payload)
        // 67,108,864 * 32B = 2,147,483,648 bytes (> int.MaxValue: 2,147,483,647)
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircularWindow<TestFrame>(67_108_864));
    }

    #endregion

    #region 5. Lifecycle & Disposal

    [Fact]
    public unsafe void DangerousGetNativePointer_ReturnsValidPointer()
    {
        using var window = new CircularWindow<TestFrame>(_defaultCapacity);
        NativeCircularWindow<TestFrame>* native = window.DangerousGetNativePointer();

        Assert.True(native != null);
        Assert.Equal(_defaultCapacity, native->Capacity);
        Assert.Equal(0, native->WriteCursor);
    }

    [Fact]
    public unsafe void Dispose_IsIdempotent_And_ThrowsWhenUsedPostDispose()
    {
        var window = new CircularWindow<TestFrame>(_defaultCapacity);
        window.Dispose();
        window.Dispose(); // Safe no-op

        var frame = new TestFrame();
        Assert.Throws<ObjectDisposedException>(() => window.Append(in frame));
        Assert.Throws<ObjectDisposedException>(() => window.DangerousGetNativePointer());
        Assert.Throws<ObjectDisposedException>(() => window.ReadLatest(Span<TestFrame>.Empty));
    }

    #endregion

    #region 6. Multi-Threaded Stress Test (Isolated await from unsafe)

    [Fact]
    public async Task Concurrent_WriterAndReader_NeverReadsTornFrame()
    {
        const int capacity = 128;
        using var window = new CircularWindow<TestStressPayload>(capacity);
        const int totalWrites = 100_000;

        using var startSignal = new ManualResetEventSlim(false);
        using var writerCompleted = new ManualResetEventSlim(false);

        long tornFramesDetected = 0;

        // Writer task: appends continuously across laps
        Task writerTask = Task.Run(() =>
        {
            startSignal.Wait();

            for (long seq = 1; seq <= totalWrites; seq++)
            {
                var payload = new TestStressPayload
                {
                    HeadCanary = seq,
                    Timestamp = seq * 10,
                    TailCanary = ~seq,
                };

                window.Append(in payload);
            }

            writerCompleted.Set();
        });

        // Reader task: samples recent snapshots continuously
        Task readerTask = Task.Run(() =>
        {
            startSignal.Wait();
            Span<TestStressPayload> readBuffer = stackalloc TestStressPayload[32];

            while (!writerCompleted.IsSet)
            {
                int count = window.ReadLatest(readBuffer);
                for (int i = 0; i < count; i++)
                {
                    long head = readBuffer[i].HeadCanary;
                    long tail = readBuffer[i].TailCanary;

                    if (head != ~tail)
                    {
                        Interlocked.Increment(ref tornFramesDetected);
                    }
                }
            }
        });

        startSignal.Set();

        await Task.WhenAll(writerTask, readerTask);

        Assert.Equal(0, Volatile.Read(ref tornFramesDetected));
        Assert.Equal(totalWrites, window.WriteCursor);
    }

    #endregion

    #region Test Structs & Static Filter Functions

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct TestFrame
    {
        public long Timestamp;
        public ulong Data;
        public uint Checksum;
        public int Id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct TestStressPayload
    {
        public long HeadCanary;
        public long Timestamp;
        public long TailCanary;
    }

    private static unsafe int FilterEvenIds(void* context, TestFrame* item)
    {
        return (item->Id & 1) == 0 ? 1 : 0;
    }

    private static unsafe int FilterWithTimestampCutoff(void* context, TestFrame* item)
    {
        long minTimestamp = *(long*)context;
        if (item->Timestamp < minTimestamp)
            return -1; // Terminal cutoff

        return 1; // Match
    }

    #endregion
}
