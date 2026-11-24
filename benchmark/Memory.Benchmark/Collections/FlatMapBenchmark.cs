using BenchmarkDotNet.Attributes;
using Memory.Collections.FlatMap;
using EntityState = Memory.Benchmark.Collections.MapBenchmarkFixture.EntityState;

namespace Memory.Benchmark.Collections;

/// <summary>
/// Read-only and idempotent in-place mutation benchmarks for <see cref="FlatMap{TKey, TValue}"/>
/// and <see cref="NativeFlatMap{TKey, TValue}"/> using <see cref="GlobalSetupAttribute"/> so
/// BenchmarkDotNet auto-scales invocation counts without <see cref="IterationSetupAttribute"/> overhead.
/// </summary>
[MemoryDiagnoser]
public unsafe class FlatMapBenchmark
{
    [Params(10_000, 100_000)]
    public int KeyCount;

    private MapBenchmarkFixture _fixture;
    private FlatMap<ulong, EntityState> _flatMap = null!;
    private NativeFlatMap<ulong, EntityState>* _nativeFlatMap;
    private Dictionary<ulong, EntityState> _managedDict = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixture.AllocateKeys(KeyCount);
        _fixture.PopulateFlatMaps(KeyCount, out _flatMap, out _nativeFlatMap, out _managedDict);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _flatMap.Dispose();
        if (_nativeFlatMap != null)
        {
            NativeFlatMap<ulong, EntityState>.Free(_nativeFlatMap);
            _nativeFlatMap = null;
        }
        _managedDict.Clear();
        _fixture.FreeKeys();
    }

    // =========================================================================
    //  LOOKUPS: HIT
    // =========================================================================

    [Benchmark(Baseline = true)]
    public int Lookup_Hit_FlatMap()
    {
        int found = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_flatMap.TryGetValue(_fixture.Keys[i], out _))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int Lookup_Hit_NativeFlatMap()
    {
        int found = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_nativeFlatMap->TryGetValue(_fixture.Keys[i], out _))
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

    // =========================================================================
    //  LOOKUPS: MISS
    // =========================================================================

    [Benchmark]
    public int Lookup_Miss_FlatMap()
    {
        int missed = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (!_flatMap.TryGetValue(_fixture.MissingKeys[i], out _))
            {
                missed++;
            }
        }
        return missed;
    }

    [Benchmark]
    public int Lookup_Miss_NativeFlatMap()
    {
        int missed = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (!_nativeFlatMap->TryGetValue(_fixture.MissingKeys[i], out _))
            {
                missed++;
            }
        }
        return missed;
    }

    [Benchmark]
    public int Lookup_Miss_ManagedDictionary()
    {
        int missed = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (!_managedDict.TryGetValue(_fixture.MissingKeys[i], out _))
            {
                missed++;
            }
        }
        return missed;
    }

    // =========================================================================
    //  IN-PLACE MUTATION (Zero-Copy Ref vs Struct Re-assignment)
    // =========================================================================

    [Benchmark]
    public void InPlace_Mutation_FlatMap()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            ref var val = ref _flatMap.GetValueRefOrAddDefault(_fixture.Keys[i], out _, out _);
            val.Health += 1;
        }
    }

    [Benchmark]
    public void InPlace_Mutation_NativeFlatMap()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            ref var val = ref _nativeFlatMap->GetValueRefOrAddDefault(
                _fixture.Keys[i],
                out _,
                out _
            );
            val.Health += 1;
        }
    }

    [Benchmark]
    public void InPlace_Mutation_ManagedDictionary()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            var val = _managedDict[_fixture.Keys[i]];
            val.Health += 1;
            _managedDict[_fixture.Keys[i]] = val;
        }
    }

    // =========================================================================
    //  FULL TABLE ITERATION (Cache-Contiguous Sweep)
    // =========================================================================

    [Benchmark]
    public ulong Full_Table_Iteration_FlatMap()
    {
        ulong sum = 0;
        foreach (ref readonly var entry in _flatMap)
        {
            sum += entry.Key;
        }
        return sum;
    }

    [Benchmark]
    public ulong Full_Table_Iteration_NativeFlatMap()
    {
        ulong sum = 0;
        FlatMapEntry<ulong, EntityState>* entries = _nativeFlatMap->EntriesPtr;
        int capacity = _nativeFlatMap->Capacity;

        for (int i = 0; i < capacity; i++)
        {
            if (entries[i].State != 0)
            {
                sum += entries[i].Key;
            }
        }
        return sum;
    }

    [Benchmark]
    public ulong Full_Table_Iteration_ManagedDictionary()
    {
        ulong sum = 0;
        foreach (var pair in _managedDict)
        {
            sum += pair.Key;
        }
        return sum;
    }
}
