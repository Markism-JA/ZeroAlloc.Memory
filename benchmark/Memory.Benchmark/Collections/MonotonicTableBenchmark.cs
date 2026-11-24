using BenchmarkDotNet.Attributes;
using Memory.Collections.MonotonicTable;
using EntityState = Memory.Benchmark.Collections.MapBenchmarkFixture.EntityState;

namespace Memory.Benchmark.Collections;

/// <summary>
/// Lookup and sequence-dominated upsert benchmarks for <see cref="MonotonicFlatMap{TKey, TValue}"/>,
/// <see cref="NativeMonotonicFlatMap{TKey, TValue}"/>, <see cref="MonotonicVirtualMap{TKey, TValue}"/>,
/// and <see cref="NativeMonotonicVirtualMap{TKey, TValue}"/>.
/// </summary>
[MemoryDiagnoser]
public unsafe class MonotonicTableBenchmark
{
    [Params(10_000, 100_000)]
    public int KeyCount;

    private MapBenchmarkFixture _fixture;
    private MonotonicFlatMap<ulong, EntityState> _flatMap = null!;
    private NativeMonotonicFlatMap<ulong, EntityState>* _nativeFlatMap;
    private MonotonicVirtualMap<ulong, EntityState> _virtualMap = null!;
    private NativeMonotonicVirtualMap<ulong, EntityState>* _nativeVirtualMap;
    private Dictionary<ulong, EntityState> _managedDict = null!;
    private ulong _sequence = 2;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixture.AllocateKeys(KeyCount);

        _flatMap = new MonotonicFlatMap<ulong, EntityState>(KeyCount);
        _nativeFlatMap = NativeMonotonicFlatMap<ulong, EntityState>.Allocate(KeyCount);
        _virtualMap = new MonotonicVirtualMap<ulong, EntityState>(KeyCount);
        _nativeVirtualMap = NativeMonotonicVirtualMap<ulong, EntityState>.Allocate(KeyCount);
        _managedDict = new Dictionary<ulong, EntityState>(KeyCount);

        for (int i = 0; i < KeyCount; i++)
        {
            ulong key = _fixture.Keys[i];
            _fixture.SampleState.EntityId = key;
            _flatMap.TryPut(key, in _fixture.SampleState, sequence: 1);
            _nativeFlatMap->TryPut(key, in _fixture.SampleState, sequence: 1);
            _virtualMap.TryPut(in key, in _fixture.SampleState, sequence: 1);
            _nativeVirtualMap->TryPut(in key, in _fixture.SampleState, sequence: 1);
            _managedDict[key] = _fixture.SampleState;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _flatMap.Dispose();
        if (_nativeFlatMap != null)
        {
            NativeMonotonicFlatMap<ulong, EntityState>.Free(_nativeFlatMap);
            _nativeFlatMap = null;
        }

        _virtualMap.Dispose();
        if (_nativeVirtualMap != null)
        {
            NativeMonotonicVirtualMap<ulong, EntityState>.Free(_nativeVirtualMap);
            _nativeVirtualMap = null;
        }

        _managedDict.Clear();
        _fixture.FreeKeys();
    }

    [Benchmark(Baseline = true)]
    public int Lookup_Hit_MonotonicFlatMap()
    {
        int found = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_flatMap.TryGet(_fixture.Keys[i], out _, out _))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int Lookup_Hit_NativeMonotonicFlatMap()
    {
        int found = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_nativeFlatMap->TryGet(_fixture.Keys[i], out _, out _))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int Lookup_Hit_MonotonicVirtualMap()
    {
        int found = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_virtualMap.TryGet(in _fixture.Keys[i], out _, out _))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int Lookup_Hit_NativeMonotonicVirtualMap()
    {
        int found = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_nativeVirtualMap->TryGet(in _fixture.Keys[i], out _, out _))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int Lookup_Hit_ManagedDictionary()
    {
        int found = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_managedDict.TryGetValue(_fixture.Keys[i], out _))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int TryPut_NewerSequence_MonotonicFlatMap()
    {
        ulong seq = ++_sequence;
        int updated = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_flatMap.TryPut(_fixture.Keys[i], in _fixture.SampleState, seq) == MonotonicResult.Advanced)
            {
                updated++;
            }
        }
        return updated;
    }

    [Benchmark]
    public int TryPut_StaleSequence_Rejected_MonotonicFlatMap()
    {
        int rejected = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_flatMap.TryPut(_fixture.Keys[i], in _fixture.SampleState, sequence: 0) == MonotonicResult.Stale)
            {
                rejected++;
            }
        }
        return rejected;
    }

    [Benchmark]
    public int TryPut_NewerSequence_MonotonicVirtualMap()
    {
        ulong seq = ++_sequence;
        int updated = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_virtualMap.TryPut(in _fixture.Keys[i], in _fixture.SampleState, seq) == MonotonicResult.Advanced)
            {
                updated++;
            }
        }
        return updated;
    }

    [Benchmark]
    public int TryPut_StaleSequence_Rejected_MonotonicVirtualMap()
    {
        int rejected = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_virtualMap.TryPut(in _fixture.Keys[i], in _fixture.SampleState, sequence: 0) == MonotonicResult.Stale)
            {
                rejected++;
            }
        }
        return rejected;
    }
}
