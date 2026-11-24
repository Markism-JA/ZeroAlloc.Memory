using BenchmarkDotNet.Attributes;
using Memory.Collections.FlatMap;
using Memory.Collections.VirtualMap;
using EntityState = Memory.Benchmark.Collections.MapBenchmarkFixture.EntityState;

namespace Memory.Benchmark.Collections;

/// <summary>
/// Destructive deletion benchmarks for <see cref="VirtualMap{TKey, TValue}"/>,
/// <see cref="NativeVirtualMap{TKey, TValue}"/>, and <see cref="FlatMap{TKey, TValue}"/>,
/// isolated with <see cref="IterationSetupAttribute"/> so each iteration starts with a freshly populated table.
/// </summary>
[MemoryDiagnoser]
public unsafe class VirtualMapRemovalBenchmark
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
    public void GlobalSetup() => _fixture.AllocateKeys(KeyCount);

    [GlobalCleanup]
    public void GlobalCleanup() => _fixture.FreeKeys();

    [IterationSetup]
    public void IterationSetup() =>
        _fixture.PopulateVirtualMaps(
            KeyCount,
            out _virtualMap,
            out _nativeVirtualMap,
            out _flatMap,
            out _nativeFlatMap,
            out _managedDict);

    [IterationCleanup]
    public void IterationCleanup()
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
    }

    [Benchmark(Baseline = true)]
    public int BackwardShift_Removal_VirtualMap()
    {
        int removed = 0;
        for (int i = 0; i < KeyCount; i += 2)
        {
            if (_virtualMap.Remove(in _fixture.Keys[i]))
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
            if (_nativeVirtualMap->Remove(in _fixture.Keys[i]))
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
            if (_flatMap.Remove(_fixture.Keys[i]))
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
            if (_managedDict.Remove(_fixture.Keys[i]))
            {
                removed++;
            }
        }
        return removed;
    }
}
