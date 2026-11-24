using BenchmarkDotNet.Attributes;
using Memory.Allocators;
using Memory.Collections.FlatMap;

namespace Memory.Benchmark.Collections;

[MemoryDiagnoser]
[ShortRunJob]
public unsafe class FlatMapBenchmark
{
    public struct EntityState
    {
        public ulong EntityId;
        public int Health;
        public int Mana;
        public float PosX;
        public float PosY;
        public float PosZ;
    }

    [Params(10_000, 100_000)]
    public int KeyCount;

    private FlatMap<ulong, EntityState> _flatMap = null!;
    private NativeFlatMap<ulong, EntityState>* _nativeFlatMap;
    private Dictionary<ulong, EntityState> _managedDict = null!;

    private ulong* _keys;
    private ulong* _missingKeys;
    private EntityState _sampleState;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _keys = (ulong*)HeapAllocator.Allocate(KeyCount * sizeof(ulong), 64);
        _missingKeys = (ulong*)HeapAllocator.Allocate(KeyCount * sizeof(ulong), 64);

        for (int i = 0; i < KeyCount; i++)
        {
            // SplitMix64 / Golden ratio sequence to test pseudo-random spread
            _keys[i] = (ulong)(i + 1) * 0x9E3779B97F4A7C15UL;
            _missingKeys[i] = ((ulong)(i + 1) * 0x9E3779B97F4A7C15UL) ^ 0x8000000000000000UL;
        }

        _sampleState = new EntityState
        {
            Health = 100,
            Mana = 50,
            PosX = 12.5f,
            PosY = 0.0f,
            PosZ = 44.2f,
        };
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_keys != null)
        {
            HeapAllocator.Free(_keys);
            _keys = null;
        }

        if (_missingKeys != null)
        {
            HeapAllocator.Free(_missingKeys);
            _missingKeys = null;
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _flatMap = new FlatMap<ulong, EntityState>(KeyCount);
        _nativeFlatMap = NativeFlatMap<ulong, EntityState>.Allocate(KeyCount);
        _managedDict = new Dictionary<ulong, EntityState>(KeyCount);

        for (int i = 0; i < KeyCount; i++)
        {
            _sampleState.EntityId = _keys[i];
            _flatMap.TryAdd(_keys[i], in _sampleState);
            _nativeFlatMap->TryAdd(_keys[i], in _sampleState);
            _managedDict[_keys[i]] = _sampleState;
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _flatMap.Dispose();
        NativeFlatMap<ulong, EntityState>.Free(_nativeFlatMap);
        _managedDict.Clear();
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
            if (_flatMap.TryGetValue(_keys[i], out _))
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
            if (_nativeFlatMap->TryGetValue(_keys[i], out _))
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
            if (_managedDict.TryGetValue(_keys[i], out _))
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
            if (!_flatMap.TryGetValue(_missingKeys[i], out _))
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
            if (!_nativeFlatMap->TryGetValue(_missingKeys[i], out _))
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
            if (!_managedDict.TryGetValue(_missingKeys[i], out _))
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
            ref var val = ref _flatMap.GetValueRefOrAddDefault(_keys[i], out _, out _);
            val.Health += 1;
        }
    }

    [Benchmark]
    public void InPlace_Mutation_NativeFlatMap()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            ref var val = ref _nativeFlatMap->GetValueRefOrAddDefault(_keys[i], out _, out _);
            val.Health += 1;
        }
    }

    [Benchmark]
    public void InPlace_Mutation_ManagedDictionary()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            var val = _managedDict[_keys[i]];
            val.Health += 1;
            _managedDict[_keys[i]] = val;
        }
    }

    // =========================================================================
    //  DELETION & PROBE RECOVERY (Backward-Shift Deletion vs Tombstones)
    // =========================================================================

    [Benchmark]
    public int BackwardShift_Removal_FlatMap()
    {
        int removed = 0;
        for (int i = 0; i < KeyCount; i += 2)
        {
            if (_flatMap.Remove(_keys[i]))
            {
                removed++;
            }
        }
        return removed;
    }

    [Benchmark]
    public int BackwardShift_Removal_NativeFlatMap()
    {
        int removed = 0;
        for (int i = 0; i < KeyCount; i += 2)
        {
            if (_nativeFlatMap->Remove(_keys[i]))
            {
                removed++;
            }
        }
        return removed;
    }

    [Benchmark]
    public int Removal_ManagedDictionary()
    {
        int removed = 0;
        for (int i = 0; i < KeyCount; i += 2)
        {
            if (_managedDict.Remove(_keys[i]))
            {
                removed++;
            }
        }
        return removed;
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
