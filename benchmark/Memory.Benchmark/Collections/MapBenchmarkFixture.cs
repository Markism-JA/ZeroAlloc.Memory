using Memory.Allocators;
using Memory.Collections.FlatMap;
using Memory.Collections.VirtualMap;

namespace Memory.Benchmark.Collections;

/// <summary>
/// Shared fixture state and key-generation helpers for <see cref="FlatMap{TKey, TValue}"/>
/// and <see cref="VirtualMap{TKey, TValue}"/> benchmarks.
/// </summary>
public unsafe struct MapBenchmarkFixture
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

    public ulong* Keys;
    public ulong* MissingKeys;
    public EntityState SampleState;

    public void AllocateKeys(int keyCount)
    {
        Keys = (ulong*)HeapAllocator.Allocate(keyCount * sizeof(ulong), 64);
        MissingKeys = (ulong*)HeapAllocator.Allocate(keyCount * sizeof(ulong), 64);

        // SplitMix64 / Golden ratio sequence for deterministic pseudo-random spread
        for (int i = 0; i < keyCount; i++)
        {
            Keys[i] = (ulong)(i + 1) * 0x9E3779B97F4A7C15UL;
            MissingKeys[i] = ((ulong)(i + 1) * 0x9E3779B97F4A7C15UL) ^ 0x8000000000000000UL;
        }

        SampleState = new EntityState
        {
            Health = 100,
            Mana = 50,
            PosX = 12.5f,
            PosY = 0.0f,
            PosZ = 44.2f,
        };
    }

    public void FreeKeys()
    {
        if (Keys != null)
        {
            HeapAllocator.Free(Keys);
            Keys = null;
        }

        if (MissingKeys != null)
        {
            HeapAllocator.Free(MissingKeys);
            MissingKeys = null;
        }
    }

    public void PopulateFlatMaps(
        int keyCount,
        out FlatMap<ulong, EntityState> flatMap,
        out NativeFlatMap<ulong, EntityState>* nativeFlatMap,
        out Dictionary<ulong, EntityState> managedDict
    )
    {
        flatMap = new FlatMap<ulong, EntityState>(keyCount);
        nativeFlatMap = NativeFlatMap<ulong, EntityState>.Allocate(keyCount);
        managedDict = new Dictionary<ulong, EntityState>(keyCount);

        for (int i = 0; i < keyCount; i++)
        {
            SampleState.EntityId = Keys[i];
            flatMap.TryAdd(Keys[i], in SampleState);
            nativeFlatMap->TryAdd(Keys[i], in SampleState);
            managedDict[Keys[i]] = SampleState;
        }
    }

    public void PopulateVirtualMaps(
        int keyCount,
        out VirtualMap<ulong, EntityState> virtualMap,
        out NativeVirtualMap<ulong, EntityState>* nativeVirtualMap,
        out FlatMap<ulong, EntityState> flatMap,
        out NativeFlatMap<ulong, EntityState>* nativeFlatMap,
        out Dictionary<ulong, EntityState> managedDict
    )
    {
        virtualMap = new VirtualMap<ulong, EntityState>(keyCount);
        nativeVirtualMap = NativeVirtualMap<ulong, EntityState>.Allocate(keyCount);
        PopulateFlatMaps(keyCount, out flatMap, out nativeFlatMap, out managedDict);

        for (int i = 0; i < keyCount; i++)
        {
            SampleState.EntityId = Keys[i];
            virtualMap.TryAdd(Keys[i], SampleState);
            nativeVirtualMap->TryAdd(Keys[i], SampleState);
        }
    }
}
