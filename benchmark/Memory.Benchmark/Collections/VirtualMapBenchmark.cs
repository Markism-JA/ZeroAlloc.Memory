using BenchmarkDotNet.Attributes;
using Memory.Allocators;
using Memory.Collections.FlatMap;
using Memory.Collections.VirtualMap;

namespace Memory.Benchmark.Collections;

[MemoryDiagnoser]
[ShortRunJob]
public unsafe class VirtualMapBenchmark
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

    private VirtualMap<ulong, EntityState> _virtualMap = null!;
    private NativeVirtualMap<ulong, EntityState>* _nativeVirtualMap;

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

        // Pre-generate deterministic keys and non-existent probe keys via SplitMix64
        for (int i = 0; i < KeyCount; i++)
        {
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
        // Re-initialize instances per iteration to test clean state insertion and lookups
        _virtualMap = new VirtualMap<ulong, EntityState>(KeyCount);
        _nativeVirtualMap = NativeVirtualMap<ulong, EntityState>.Allocate(KeyCount);

        _flatMap = new FlatMap<ulong, EntityState>(KeyCount);
        _nativeFlatMap = NativeFlatMap<ulong, EntityState>.Allocate(KeyCount);

        _managedDict = new Dictionary<ulong, EntityState>(KeyCount);

        for (int i = 0; i < KeyCount; i++)
        {
            _sampleState.EntityId = _keys[i];

            _virtualMap.TryAdd(_keys[i], _sampleState);
            _nativeVirtualMap->TryAdd(_keys[i], _sampleState);

            _flatMap.TryAdd(_keys[i], in _sampleState);
            _nativeFlatMap->TryAdd(_keys[i], in _sampleState);

            _managedDict[_keys[i]] = _sampleState;
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _virtualMap.Dispose();
        NativeVirtualMap<ulong, EntityState>.Free(_nativeVirtualMap);

        _flatMap.Dispose();
        NativeFlatMap<ulong, EntityState>.Free(_nativeFlatMap);

        _managedDict.Clear();
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
            if (_virtualMap.TryGetValue(in _keys[i], out _))
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
            if (_nativeVirtualMap->TryGetValue(in _keys[i], out _))
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
            if (_flatMap.TryGetValue(_keys[i], out _))
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
            if (_nativeFlatMap->TryGetValue(_keys[i], out _))
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
            if (_managedDict.TryGetValue(_keys[i], out _))
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
            if (!_virtualMap.TryGetValue(in _missingKeys[i], out _))
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
            if (!_nativeVirtualMap->TryGetValue(in _missingKeys[i], out _))
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
            if (!_flatMap.TryGetValue(_missingKeys[i], out _))
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
            if (!_nativeFlatMap->TryGetValue(_missingKeys[i], out _))
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
            if (!_managedDict.TryGetValue(_missingKeys[i], out _))
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
            ref var val = ref _virtualMap.GetValueRefOrAddDefault(in _keys[i], out _, out _);
            val.Health += 1;
        }
    }

    [Benchmark]
    public void InPlace_Mutation_NativeVirtualMap()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            ref var val = ref _nativeVirtualMap->GetValueRefOrAddDefault(in _keys[i], out _, out _);
            val.Health += 1;
        }
    }

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
    //  DELETION & PROBE RECOVERY (Robin Hood Backward Shift vs Tombstones)
    // =========================================================================

    [Benchmark]
    public int BackwardShift_Removal_VirtualMap()
    {
        int removed = 0;
        for (int i = 0; i < KeyCount; i += 2)
        {
            if (_virtualMap.Remove(in _keys[i]))
            {
                removed++;
            }
        }
        return removed;
    }

    [Benchmark]
    public int BackwardShift_Removal_NativeVirtualMap()
    {
        int removed = 0;
        for (int i = 0; i < KeyCount; i += 2)
        {
            if (_nativeVirtualMap->Remove(in _keys[i]))
            {
                removed++;
            }
        }
        return removed;
    }

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
