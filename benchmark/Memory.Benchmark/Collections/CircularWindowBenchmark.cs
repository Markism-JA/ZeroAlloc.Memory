using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Memory.Allocators;
using Memory.Collections.CircularWindow;

namespace Memory.Benchmark.Collections;

[MemoryDiagnoser]
[ShortRunJob]
public unsafe class CircularWindowBenchmarks
{
    /// <summary>
    /// 32-byte cache-friendly test payload representing a realistic simulation/telemetry frame.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct BenchmarkPacket(long timestampNs, ulong entityId, uint eventType)
    {
        public long TimestampNs = timestampNs;
        public ulong EntityId = entityId;
        public uint EventType = eventType;
        public float PositionX = 10.5f;
        public float PositionY = 20.0f;
        public float PositionZ = 30.25f;
    }

    private const int _windowCapacity = 4096;
    private const int _batchSize = 32;

    private NativeCircularWindow<BenchmarkPacket>* _nativeWindow;
    private CircularWindow<BenchmarkPacket> _managedWindow = null!;

    private BenchmarkPacket* _singlePacket;
    private BenchmarkPacket* _batchPackets;
    private BenchmarkPacket* _queryBuffer;
    private BenchmarkPacket[] _managedQueryArray = null!;

    private long _filterTimestampCutoff;

    [GlobalSetup]
    public void Setup()
    {
        // 1. Initialize both Tier-1 native pointer and Tier-2 managed lifecycle wrapper
        _nativeWindow = NativeCircularWindow<BenchmarkPacket>.Allocate(_windowCapacity);
        _managedWindow = new CircularWindow<BenchmarkPacket>(_windowCapacity);

        // 2. Allocate 64-byte cache-aligned test buffers via HeapAllocator
        _singlePacket = (BenchmarkPacket*)HeapAllocator.Allocate(sizeof(BenchmarkPacket), 64);
        _batchPackets = (BenchmarkPacket*)
            HeapAllocator.Allocate(_batchSize * sizeof(BenchmarkPacket), 64);
        _queryBuffer = (BenchmarkPacket*)
            HeapAllocator.Allocate(_batchSize * sizeof(BenchmarkPacket), 64);
        _managedQueryArray = new BenchmarkPacket[_batchSize];

        *_singlePacket = new BenchmarkPacket(timestampNs: 50_000, entityId: 1001, eventType: 0x01);

        for (int i = 0; i < _batchSize; i++)
        {
            _batchPackets[i] = new BenchmarkPacket(
                timestampNs: (i + 1) * 1000,
                entityId: (ulong)(2000 + i),
                eventType: (uint)(i % 2 == 0 ? 0x02 : 0x01)
            );
        }

        // 3. Pre-populate windows up to capacity to benchmark saturated lossy overwrites
        for (int i = 0; i < _windowCapacity; i++)
        {
            var packet = new BenchmarkPacket(
                timestampNs: (i + 1) * 500,
                entityId: (ulong)i,
                eventType: (uint)(i % 4)
            );

            _nativeWindow->Append(in packet);
            _managedWindow.Append(in packet);
        }

        // Cutoff matches the middle of the window for historical scanning tests
        _filterTimestampCutoff = (_windowCapacity / 2) * 500;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeCircularWindow<BenchmarkPacket>.Free(_nativeWindow);
        _nativeWindow = null;

        _managedWindow.Dispose();

        if (_singlePacket != null)
        {
            HeapAllocator.Free(_singlePacket);
            _singlePacket = null;
        }

        if (_batchPackets != null)
        {
            HeapAllocator.Free(_batchPackets);
            _batchPackets = null;
        }

        if (_queryBuffer != null)
        {
            HeapAllocator.Free(_queryBuffer);
            _queryBuffer = null;
        }
    }

    // =========================================================================
    //  APPEND / INGESTION (Seqlock Serialization & Cursor Advance)
    // =========================================================================

    [Benchmark(Baseline = true, Description = "Native: Append Single (Per-slot seqlock write)")]
    public void Native_Append_Single()
    {
        _nativeWindow->Append(in *_singlePacket);
    }

    [Benchmark(Description = "Native: AppendBatch 32 (Contiguous burst write)")]
    public void Native_AppendBatch_32()
    {
        _nativeWindow->AppendBatch(_batchPackets, _batchSize);
    }

    [Benchmark(Description = "Static IPC: AppendRaw 32 (Direct Partition Slots & Cursor)")]
    public void Static_AppendRaw_Batch_32()
    {
        NativeCircularWindow<BenchmarkPacket>.AppendRaw(
            slots: _nativeWindow->SlotsPtr,
            writeCursorPtr: _nativeWindow->CursorPtr,
            mask: _nativeWindow->Mask,
            items: _batchPackets,
            count: _batchSize
        );
    }

    // =========================================================================
    //  CHRONOLOGICAL READS (Non-Blocking Seqlock Validation)
    // =========================================================================

    [Benchmark(Description = "Native: ReadLatest 32 (Reverse-scan + Seqlock check)")]
    public int Native_ReadLatest_32()
    {
        return _nativeWindow->ReadLatest(_queryBuffer, _batchSize);
    }

    [Benchmark(Description = "Managed: ReadLatest Span 32 (Safe Span Facade)")]
    public int Managed_ReadLatest_Span_32()
    {
        return _managedWindow.ReadLatest(_managedQueryArray.AsSpan());
    }

    // =========================================================================
    //  HISTORICAL FILTERED QUERIES (Reverse Chronological Sweep + Cutoff)
    // =========================================================================

    [Benchmark(Description = "Native: QueryFiltered (C-ABI Function Pointer + Terminal Cutoff)")]
    public int Native_QueryFiltered()
    {
        long cutoff = _filterTimestampCutoff;

        return _nativeWindow->QueryFiltered(
            dest: _queryBuffer,
            maxCount: _batchSize,
            filterContext: &cutoff,
            predicate: &PredicateMatchesEvenType
        );
    }

    // =========================================================================
    //  ZERO-COPY POINT INSPECTION (Tail Peek)
    // =========================================================================

    [Benchmark(Description = "Zero-Copy: Tail Slot Inspection")]
    public ulong ZeroCopy_TailInspection()
    {
        long cursor = Volatile.Read(ref *_nativeWindow->CursorPtr);
        int tailIndex = (int)((cursor - 1) & _nativeWindow->Mask);
        return _nativeWindow->SlotsPtr[tailIndex].Value.EntityId;
    }

    #region Predicate Callbacks

    private static int PredicateMatchesEvenType(void* context, BenchmarkPacket* item)
    {
        long cutoff = *(long*)context;

        // Terminal cutoff: ring is monotonically ordered by timestamp
        if (item->TimestampNs < cutoff)
            return -1;

        // Return 1 for match (even types), 0 for mismatch
        return (item->EventType & 1) == 0 ? 1 : 0;
    }

    #endregion
}
