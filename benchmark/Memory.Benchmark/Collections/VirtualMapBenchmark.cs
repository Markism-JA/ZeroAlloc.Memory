using BenchmarkDotNet.Attributes;
using Memory.Collections.FlatMap;
using Memory.Collections.VirtualMap;
using EntityState = Memory.Benchmark.Collections.MapBenchmarkFixture.EntityState;

namespace Memory.Benchmark.Collections;

/// <summary>
/// Read-only and idempotent in-place mutation benchmarks for <see cref="VirtualMap{TKey, TValue}"/>
/// and <see cref="NativeVirtualMap{TKey, TValue}"/> using <see cref="GlobalSetupAttribute"/> so
/// BenchmarkDotNet auto-scales invocation counts without <see cref="IterationSetupAttribute"/> overhead.
/// </summary>
[MemoryDiagnoser]
public unsafe class VirtualMapBenchmark
{
    [Params(10_000, 100_000)]
    public int KeyCount;

    private MapBenchmarkFixture _fixture;
    private VirtualMap<ulong, EntityState> _virtualMap = null!;
    private NativeVirtualMap<ulong, EntityState>* _nativeVirtualMap;
    private FlatMap<ulong, EntityState> _flatMap = null!;
    private NativeFlatMap<ulong, EntityState>* _nativeFlatMap;
    private Dictionary<ulong, EntityState> _managedDict = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixture.AllocateKeys(KeyCount);
        _fixture.PopulateVirtualMaps(
            KeyCount,
            out _virtualMap,
            out _nativeVirtualMap,
            out _flatMap,
            out _nativeFlatMap,
            out _managedDict);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _virtualMap.Dispose();
        if (_nativeVirtualMap != null)
        {
            NativeVirtualMap<ulong, EntityState>.Free(_nativeVirtualMap);
            _nativeVirtualMap = null;
        }

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
    //  POINT LOOKUPS: HIT (Single Item Retrieval Latency)
    // =========================================================================

    [Benchmark(Baseline = true)]
    public int Lookup_Hit_VirtualMap()
    {
        int foundCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_virtualMap.TryGetValue(in _fixture.Keys[i], out _))
            {
                foundCount++;
            }
        }
        return foundCount;
    }

    [Benchmark]
    public int Lookup_Hit_NativeVirtualMap()
    {
        int foundCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_nativeVirtualMap->TryGetValue(in _fixture.Keys[i], out _))
            {
                foundCount++;
            }
        }
        return foundCount;
    }

    [Benchmark]
    public int Lookup_Hit_FlatMap()
    {
        int foundCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_flatMap.TryGetValue(_fixture.Keys[i], out _))
            {
                foundCount++;
            }
        }
        return foundCount;
    }

    [Benchmark]
    public int Lookup_Hit_NativeFlatMap()
    {
        int foundCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_nativeFlatMap->TryGetValue(_fixture.Keys[i], out _))
            {
                foundCount++;
            }
        }
        return foundCount;
    }

    [Benchmark]
    public int Lookup_Hit_ManagedDictionary()
    {
        int foundCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_managedDict.TryGetValue(_fixture.Keys[i], out _))
            {
                foundCount++;
            }
        }
        return foundCount;
    }

    // =========================================================================
    //  POINT LOOKUPS: MISS (Non-Existent Keys / Probe Bound Path)
    // =========================================================================

    [Benchmark]
    public int Lookup_Miss_VirtualMap()
    {
        int missedCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (!_virtualMap.TryGetValue(in _fixture.MissingKeys[i], out _))
            {
                missedCount++;
            }
        }
        return missedCount;
    }

    [Benchmark]
    public int Lookup_Miss_NativeVirtualMap()
    {
        int missedCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (!_nativeVirtualMap->TryGetValue(in _fixture.MissingKeys[i], out _))
            {
                missedCount++;
            }
        }
        return missedCount;
    }

    [Benchmark]
    public int Lookup_Miss_FlatMap()
    {
        int missedCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (!_flatMap.TryGetValue(_fixture.MissingKeys[i], out _))
            {
                missedCount++;
            }
        }
        return missedCount;
    }

    [Benchmark]
    public int Lookup_Miss_NativeFlatMap()
    {
        int missedCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (!_nativeFlatMap->TryGetValue(_fixture.MissingKeys[i], out _))
            {
                missedCount++;
            }
        }
        return missedCount;
    }

    [Benchmark]
    public int Lookup_Miss_ManagedDictionary()
    {
        int missedCount = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (!_managedDict.TryGetValue(_fixture.MissingKeys[i], out _))
            {
                missedCount++;
            }
        }
        return missedCount;
    }

    // =========================================================================
    //  MUTATION: REF ACCESS & IN-PLACE WRITE (Zero-Copy Ref vs Copy Re-assignment)
    // =========================================================================

    [Benchmark]
    public void InPlace_Mutation_VirtualMap()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            ref var val = ref _virtualMap.GetValueRefOrAddDefault(in _fixture.Keys[i], out _, out _);
            val.Health += 1;
        }
    }

    [Benchmark]
    public void InPlace_Mutation_NativeVirtualMap()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            ref var val = ref _nativeVirtualMap->GetValueRefOrAddDefault(in _fixture.Keys[i], out _, out _);
            val.Health += 1;
        }
    }

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
            ref var val = ref _nativeFlatMap->GetValueRefOrAddDefault(_fixture.Keys[i], out _, out _);
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
    //  FULL TABLE ITERATION (Cache-Contiguous Memory Sweep)
    // =========================================================================

    [Benchmark]
    public ulong Full_Table_Iteration_VirtualMap()
    {
        ulong sum = 0;
        foreach (ref readonly var entry in _virtualMap)
        {
            sum += entry.Key;
        }
        return sum;
    }

    [Benchmark]
    public ulong Full_Table_Iteration_NativeVirtualMap()
    {
        ulong sum = 0;
        VirtualMapEntry<ulong, EntityState>* slots = _nativeVirtualMap->SlotsPtr;
        int capacity = _nativeVirtualMap->Capacity;

        for (int i = 0; i < capacity; i++)
        {
            if (slots[i].State != 0)
            {
                sum += slots[i].Key;
            }
        }
        return sum;
    }

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
