# Benchmark Results & Performance Analysis

Measured with **BenchmarkDotNet v0.15.8** on **Linux (Arch Linux)**, `11th Gen Intel Core i3-1115G4` (CPU governor set to `performance`, Intel Turbo Boost disabled at `1.01–1.59 GHz`, NMI watchdog and ASLR disabled for deterministic low-jitter measurement), `.NET 10.0.2 (X64 RyuJIT x86-64-v4)`, `[MemoryDiagnoser]`.

Reproduce locally:

```bash
dotnet run --project benchmark/Memory.Benchmark -c Release -- --filter "*"
```

### How to Read These Numbers

* **All 17 suites run under `DefaultJob`** with `[MemoryDiagnoser]`.
* **Destructive `[IterationSetup]` suites** (`FlatMapRemovalBenchmark`, `VirtualMapRemovalBenchmark`, and `KernelPageAllocatorBenchmark.Commit_Decommit_Loop`) run with `InvocationCount=1` so each iteration starts from a clean state.
* **Multi-threaded benchmarks** (`SpscRingBenchmark.Concurrent_Thread_Pipeline_*`, `BoundedMpscRingBenchmark.Contended_4Producers_*`, `MirroredMpscBenchmark.Mpsc_Concurrent_Drain_*`, `UnmanagedLinearArenaBenchmark.Parallel_MultiThreaded_Lease_Contention`) use pre-spawned persistent worker threads with `0 B` per-iteration harness allocation.

---

## 1. Associative Maps (`FlatMap`, `VirtualMap`, `MonotonicTable`)

![FlatMap vs Dictionary Latency](./flatmap_vs_dictionary.png)

### Why It Behaves This Way

1. **L2/L3-Resident Working Sets (`10,000` keys):** `NativeFlatMap` (`62.89 µs`) and `FlatMap` (`63.78 µs`) beat `Dictionary<K,V>` (`101.16 µs`) by **~1.6×** on lookup hits and **~2.4×** on in-place struct mutation (`69.89 µs` vs `165.39 µs`). Open-addressed inline slots avoid pointer chasing (`_buckets` → `_entries`) and `GetValueRefOrAddDefault` returns a direct `ref TValue` so callers mutate fields in place instead of copying a `28 B` struct out and back.
2. **Cache-Spill Regime (`100,000` keys):** Once the 50%-load-factor inline table (`262,144` slots ≈ `10.5 MB`) exceeds CPU L3 cache on this 2-core i3, `Dictionary<K,V>` (`1,715.20 µs`) overtakes `NativeFlatMap` (`2,241.07 µs`) on pure point lookups because `Dictionary` separates compact `int[] _buckets` from dense `_entries`.
3. **Dense Iteration Trade-Off:** `Dictionary` iterates a densely packed `_entries` array (`217.18 µs` at `100,000` keys), whereas open-addressed tables (`FlatMap`, `VirtualMap`) must scan every capacity slot including empty gaps (`1,574.32 µs`).
4. **Managed Wrapper (`FlatMap`) vs Raw Pointer (`NativeFlatMap`):** With `[GlobalSetup]` auto-scaling, `FlatMap` (`63.78 µs`) and `NativeFlatMap` (`62.89 µs`) match within ~1% — the managed RAII wrapper adds virtually zero lookup overhead while guaranteeing safe `Dispose()`.

### 1.1 `FlatMap` & `NativeFlatMap` vs `Dictionary<ulong, EntityState>`

| Method | `10,000` Keys Mean | `10,000` Ratio | `100,000` Keys Mean | `100,000` Ratio | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: |
| `Lookup_Hit_FlatMap` | 63.78 µs | 1.00 | 2,542.80 µs | 1.01 | 0 B |
| `Lookup_Hit_NativeFlatMap` | **62.89 µs** | **0.99** | 2,241.07 µs | 0.89 | 0 B |
| `Lookup_Hit_ManagedDictionary` | 101.16 µs | 1.59 | **1,715.20 µs** | **0.68** | 0 B |
| `Lookup_Miss_FlatMap` | **46.04 µs** | **0.72** | 3,059.30 µs | 1.21 | 0 B |
| `Lookup_Miss_NativeFlatMap` | 81.18 µs | 1.27 | 2,958.99 µs | 1.17 | 0 B |
| `Lookup_Miss_ManagedDictionary` | 55.95 µs | 0.88 | **2,159.44 µs** | **0.85** | 0 B |
| `InPlace_Mutation_FlatMap` | 83.28 µs | 1.31 | 2,550.50 µs | 1.01 | 0 B |
| `InPlace_Mutation_NativeFlatMap` | **69.89 µs** | **1.10** | **2,476.60 µs** | **0.98** | 0 B |
| `InPlace_Mutation_ManagedDictionary` | 165.39 µs | 2.59 | 2,485.88 µs | 0.98 | 0 B |
| `Full_Table_Iteration_FlatMap` | 129.47 µs | 2.03 | 1,569.24 µs | 0.62 | 0 B |
| `Full_Table_Iteration_NativeFlatMap` | 138.86 µs | 2.18 | 1,574.32 µs | 0.62 | 0 B |
| `Full_Table_Iteration_ManagedDictionary` | **21.15 µs** | **0.33** | **217.18 µs** | **0.09** | 0 B |

### 1.2 `FlatMapRemovalBenchmark` (`InvocationCount=1`, `[IterationSetup]`)

| Method | `10,000` Keys Mean | `100,000` Keys Mean | Allocated |
| :--- | ---: | ---: | ---: |
| `BackwardShift_Removal_FlatMap` | 260.1 µs | 2,651.7 µs | 0 B |
| `BackwardShift_Removal_NativeFlatMap` | 251.2 µs | 2,622.3 µs | 0 B |
| `Removal_ManagedDictionary` | **192.1 µs** | **1,506.7 µs** | 0 B |

### 1.3 `VirtualMap` & `NativeVirtualMap` vs `FlatMap` & `Dictionary`

| Method | `10,000` Keys Mean | `100,000` Keys Mean | Allocated |
| :--- | ---: | ---: | ---: |
| `Lookup_Hit_VirtualMap` | 64.72 µs | 2,245.58 µs | 0 B |
| `Lookup_Hit_NativeVirtualMap` | 63.65 µs | 2,169.51 µs | 0 B |
| `Lookup_Hit_FlatMap` | **62.99 µs** | 2,231.33 µs | 0 B |
| `Lookup_Hit_NativeFlatMap` | 63.46 µs | 2,201.63 µs | 0 B |
| `Lookup_Hit_ManagedDictionary` | 100.48 µs | **1,779.87 µs** | 0 B |
| `Lookup_Miss_VirtualMap` | 105.00 µs | 3,061.22 µs | 0 B |
| `Lookup_Miss_NativeVirtualMap` | 82.03 µs | 3,009.68 µs | 0 B |
| `Lookup_Miss_FlatMap` | **44.88 µs** | 2,964.74 µs | 0 B |
| `Lookup_Miss_NativeFlatMap` | 80.55 µs | 2,962.35 µs | 0 B |
| `Lookup_Miss_ManagedDictionary` | 135.66 µs | **2,161.88 µs** | 0 B |
| `InPlace_Mutation_VirtualMap` | 80.34 µs | 2,759.36 µs | 0 B |
| `InPlace_Mutation_NativeVirtualMap` | 77.29 µs | 2,699.24 µs | 0 B |
| `InPlace_Mutation_FlatMap` | 75.81 µs | 2,571.71 µs | 0 B |
| `InPlace_Mutation_NativeFlatMap` | **68.81 µs** | **2,459.88 µs** | 0 B |
| `InPlace_Mutation_ManagedDictionary` | 164.62 µs | 2,505.67 µs | 0 B |
| `Full_Table_Iteration_VirtualMap` | 129.07 µs | 1,562.99 µs | 0 B |
| `Full_Table_Iteration_NativeVirtualMap` | 139.16 µs | 1,575.34 µs | 0 B |
| `Full_Table_Iteration_FlatMap` | 129.38 µs | 1,566.36 µs | 0 B |
| `Full_Table_Iteration_ManagedDictionary` | **19.82 µs** | **215.27 µs** | 0 B |

### 1.4 `VirtualMapRemovalBenchmark` (`InvocationCount=1`, `[IterationSetup]`)

| Method | `10,000` Keys Mean | `100,000` Keys Mean | Allocated |
| :--- | ---: | ---: | ---: |
| `BackwardShift_Removal_VirtualMap` | 278.5 µs | 2,689.9 µs | 0 B |
| `BackwardShift_Removal_NativeVirtualMap` | 250.7 µs | 2,641.1 µs | 0 B |
| `BackwardShift_Removal_FlatMap` | 270.1 µs | 2,662.4 µs | 0 B |
| `Removal_ManagedDictionary` | **184.5 µs** | **1,611.0 µs** | 0 B |

### 1.5 `MonotonicTableBenchmark` (`MonotonicFlatMap` & `MonotonicVirtualMap`)

| Method | `10,000` Keys Mean | `100,000` Keys Mean | Allocated |
| :--- | ---: | ---: | ---: |
| `Lookup_Hit_MonotonicFlatMap` | 67.41 µs | 2,496.62 µs | 0 B |
| `Lookup_Hit_NativeMonotonicFlatMap` | **63.76 µs** | 2,336.36 µs | 0 B |
| `Lookup_Hit_MonotonicVirtualMap` | 70.57 µs | 2,446.61 µs | 0 B |
| `Lookup_Hit_NativeMonotonicVirtualMap` | 66.44 µs | 2,369.05 µs | 0 B |
| `Lookup_Hit_ManagedDictionary` | 100.74 µs | **1,797.01 µs** | 0 B |
| `TryPut_NewerSequence_MonotonicFlatMap` | 86.42 µs | 3,041.41 µs | 0 B |
| `TryPut_StaleSequence_Rejected_MonotonicFlatMap` | 80.57 µs | 2,651.61 µs | 0 B |
| `TryPut_NewerSequence_MonotonicVirtualMap` | 89.95 µs | 3,230.86 µs | 0 B |
| `TryPut_StaleSequence_Rejected_MonotonicVirtualMap` | 82.45 µs | 2,868.72 µs | 0 B |

---

## 2. Ring Buffers & Circular Windows

![Ring Buffer Round-Trip Latency](./ring_roundtrip_latency.png)

### Why It Behaves This Way

1. **Atomic Locks & Topology:** Fixed-size `SpscRing` (**`5.68 ns`**) is the fastest ring because it uses plain `Volatile` head/tail ordering with zero `Interlocked.CompareExchange` (`LOCK CMPXCHG`) and no variable-length framing header. `MirroredSpscRing` (`9.41 ns`) adds minimal framing overhead for variable-length virtual wrap-around, while MPSC rings (`BoundedMpscRing` `13.52 ns`, `MirroredMpscRing` `13.69 ns`) pay ~4–8 ns for atomic slot reservation across multiple producers.
2. **MPSC Contention vs BCL (`BoundedMpscRingBenchmark`):** Under 4-producer contention (`1,000` items across 4 persistent threads), `BoundedMpscRing` completes in **`78.41 µs`** (`0 B`) vs **`114.49 µs`** for `ConcurrentQueue<T>` (1.46× slower) and **`126.46 µs`** for bounded `Channel<T>` (1.61× slower). Uncontended single round-trip is **6.46× faster** than `Channel<T>` (`13.52 ns` vs `87.28 ns`).
3. **Zero-Copy View vs Full Payload Copy (`MirroredSpscBenchmark`):** Reading a zero-copy `BlockView` scales linearly (`9.67 ns` at `64 B` → `19.09 ns` at `512 B` → `43.85 ns` at `1,400 B`), while copying the entire payload out (`Native_Single_Packet_FullCopy_RoundTrip`) adds only the expected `memcpy` cost (`10.84 ns` → `28.51 ns` → `60.78 ns`).
4. **Seqlock `CircularWindow`:** Overwrites the oldest slot with lock-free per-slot sequence counters (`6.97 ns` append, `0.37 ns` tail inspection).

### 2.1 `SpscRingBenchmark`

| Method | Mean | Error | Allocated |
| :--- | ---: | ---: | ---: |
| `RoundTrip_Copy_64B` | **5.683 ns** | 0.098 ns | 0 B |
| `RoundTrip_Copy_512B` | 33.053 ns | 0.617 ns | 0 B |
| `RoundTrip_ZeroCopyPointer_64B` | 6.214 ns | 0.175 ns | 0 B |
| `RoundTrip_ZeroCopyPointer_512B` | **32.553 ns** | 0.462 ns | 0 B |
| `Batch_ScalarEnqueue_And_Drain_64B` (`64` items) | 436.629 ns | 2.472 ns | 0 B |
| `Batch_BulkEnqueue_And_Drain_64B` (`64` items) | **134.550 ns** | 2.668 ns | 0 B |
| `Concurrent_Thread_Pipeline_Throughput_Copy` (`100k` items) | 5,351.7 µs | 269.6 µs | 0 B |
| `Concurrent_Thread_Pipeline_Throughput_ZeroCopyPointer` (`100k` items) | 5,205.9 µs | 439.0 µs | 0 B |

### 2.2 `BoundedMpscRingBenchmark` vs `Channel<T>` & `ConcurrentQueue<T>`

| Method | Mean | Ratio | Allocated |
| :--- | ---: | ---: | ---: |
| `Managed_Single_RoundTrip` (`64 B`) | **13.52 ns** | **1.00** | 0 B |
| `Native_DirectPointer_RoundTrip` (`64 B`) | 15.14 ns | 1.12 | 0 B |
| `Channel_TryWrite_TryRead_RoundTrip` (`64 B`) | 87.28 ns | 6.46 | 0 B |
| `Batch_Write_And_Drain` (`64` items) | 1,078.34 ns | 79.75 | 0 B |
| `Contended_4Producers_BoundedMpsc` (`1,000` items) | **78,409.40 ns** | **1.00×** | 0 B |
| `Contended_4Producers_ConcurrentQueue` (`1,000` items) | 114,489.67 ns | 1.46× | 0 B |
| `Contended_4Producers_Channel` (`1,000` items) | 126,456.45 ns | 1.61× | 0 B |

### 2.3 `MirroredSpscBenchmark`

| Method | `64 B` Mean | `512 B` Mean | `1,400 B` Mean | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| `Managed_Single_Packet_RoundTrip` | **9.413 ns** | 19.205 ns | 45.402 ns | 0 B |
| `Native_Single_Packet_RoundTrip` | 9.666 ns | **19.088 ns** | 43.853 ns | 0 B |
| `Native_Single_Packet_FullCopy_RoundTrip` | 10.844 ns | 28.508 ns | 60.780 ns | 0 B |
| `Managed_WriteScope_ZeroCopy_RoundTrip` | 10.923 ns | 34.533 ns | 44.730 ns | 0 B |
| `Native_ReserveCommit_ZeroCopy_RoundTrip` | 10.956 ns | 20.101 ns | **43.675 ns** | 0 B |
| `Batch_Write_And_Drain` (`32` packets) | 322.752 ns | 706.378 ns | 1,511.756 ns | 0 B |

### 2.4 `MirroredMpscBenchmark`

| Method | `64 B` Mean | `512 B` Mean | `1,400 B` Mean | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| `Managed_Single_Packet_RoundTrip` | 13.69 ns | 27.11 ns | 55.15 ns | 0 B |
| `Native_DirectPointer_Single_Packet_RoundTrip` | **13.37 ns** | **25.92 ns** | **52.29 ns** | 0 B |
| `Framing_TwoSegment_Write_And_Read` | 13.76 ns | 31.66 ns | 52.78 ns | 0 B |
| `Burst_Drain_SingleBySingle_32Packets` | 586.63 ns | 983.90 ns | 1,896.54 ns | 0 B |
| `Burst_Drain_BatchReader_32Packets` | 542.72 ns | **915.17 ns** | 1,934.95 ns | 0 B |
| `Burst_Drain_Managed_DrainBatch_32Packets` | **518.89 ns** | 949.43 ns | **1,888.82 ns** | 0 B |
| `Burst_Drain_Native_DrainBatch_32Packets` | 521.13 ns | 951.26 ns | 1,898.38 ns | 0 B |
| `Mpsc_Concurrent_Drain_1By1` (`4` producers, `1,000` items) | 78.04 µs | **92.06 µs** | **107.08 µs** | 0 B |
| `Mpsc_Concurrent_Drain_Batch_Devirtualized` (`4` producers, `1,000` items) | **77.59 µs** | 102.72 µs | 110.15 µs | 0 B |

### 2.5 `CircularWindowBenchmarks`

| Method | Mean | Allocated |
| :--- | ---: | ---: |
| `Native: Append Single (Per-slot seqlock write)` | **6.97 ns** | 0 B |
| `Native: AppendBatch 32 (Contiguous burst write)` | 252.72 ns | 0 B |
| `Static IPC: AppendRaw 32 (Direct Partition Slots & Cursor)` | **96.65 ns** | 0 B |
| `Native: ReadLatest 32 (Reverse-scan + Seqlock check)` | 440.24 ns | 0 B |
| `Managed: ReadLatest Span 32 (Safe Span Facade)` | 446.25 ns | 0 B |
| `Native: QueryFiltered (C-ABI Function Pointer + Terminal Cutoff)` | 914.52 ns | 0 B |
| `Zero-Copy: Tail Slot Inspection` | **0.37 ns** | 0 B |

---

## 3. Arenas & Page Layouts

### Why It Behaves This Way

* **Batch Arena Leasing vs `ArrayPool<T>.Shared`:** `Lease_Batch_Slices` carves a 100-element slice in **`8.08 ns`** (`0 B`), matching/beating `ArrayPool<T>.Shared.Rent/Return` (`9.88 ns`). At 1,000–10,000 elements, `ArrayPool` does fewer calls (`86.1 ns` / `854.7 ns`) while `UnmanagedLinearArena` gives deterministic contiguous unmanaged pointers with instant `Reset()` (`128.4 µs` for 10,000 individual atomic leases).
* **Dirty vs Zeroed Carving:** `UnmanagedPage.Carve` (dirty) stays constant at **`~1.60 µs` per 1,000 carves** (`1.60 ns/carve`) regardless of slice size (`32 B` to `512 B`), while `CarveZeroed` scales with total zeroed bytes (`3.83 µs` → `13.14 µs`).
* **Pre-Committed vs Demand-Commit Virtual Pages:** `UnmanagedVirtualPage` pre-committed fast path takes **`~670 ns` per 500 carves** (`1.34 ns/carve`), whereas first-touch demand-commit pays the OS kernel commit cost (`8.15 µs` → `272.3 µs`).

### 3.1 `UnmanagedLinearArenaBenchmark`

| Method | `100` Leases | `1,000` Leases | `10,000` Leases | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| `Lease_Batch_Slices` (`100`/batch) | **8.08 ns** | 123.82 ns | 1,278.06 ns | 0 B |
| `ArrayPool_Shared_Batch_RentReturn` (`100`/batch) | 9.88 ns | **86.06 ns** | **854.68 ns** | 0 B |
| `Lease_SingleThreaded_Cycle` | 1,276.76 ns | 12,839.49 ns | 128,395.19 ns | 0 B |
| `Lease_And_TrimBackingMemory` | 632.06 ns | 632.10 ns | 637.24 ns | 0 B |
| `Parallel_MultiThreaded_Lease_Contention` (`4` threads) | 11.73 µs | 70.21 µs | 568.91 µs | 0 B |

### 3.2 `UnmanagedArrayTrackBenchmark`

| Method | `1,000` Elements | `10,000` Elements | `100,000` Elements | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| `Sequential_Read_Sum` | 1.377 µs | 14.170 µs | 137.618 µs | 0 B |
| `Sequential_Write_Mutate` | 1.398 µs | 13.892 µs | 139.817 µs | 0 B |
| `Random_Access_Read_Sum` | 1.328 µs | 13.505 µs | 134.997 µs | 0 B |

### 3.3 `UnmanagedPageBenchmark` (`1,000` carves)

| SliceSize / Alignment | `Carve_Dirty_Sequential_1000` | `CarveZeroed_Sequential_1000` | `Carve_And_Write_Payload_1000` | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| `32 B` / `16 B` | 1,596.28 ns | 3,833.39 ns | 1,773.94 ns | 0 B |
| `128 B` / `64 B` | 1,581.74 ns | 9,274.49 ns | 6,031.03 ns | 0 B |
| `512 B` / `64 B` | 1,602.47 ns | 13,133.20 ns | 5,946.25 ns | 0 B |

`Carve_PassedByValue_StackCopy`: **`2.13 ns`**; `Reset_And_AssertIntegrity_Lifecycle`: **`1.62 ns`**.

### 3.4 `UnmanagedVirtualPageBenchmark` (`500` carves)

| SliceSize / VirtualCapacity | `Carve_PreCommitted_FastPath_500` | `CarveZeroed_PreCommitted_FastPath_500` | `Carve_With_DemandCommit_Kernel_Pass` | `PassiveDecommitReclaim_Lifecycle` | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: |
| `64 B` / `2 MiB` | 670.09 ns | 1,976.93 ns | 8,154.84 ns | 4.82 ns | 0 B |
| `512 B` / `16 MiB` | 669.91 ns | 5,951.40 ns | 38,130.59 ns | 4.72 ns | 0 B |
| `4096 B` / `16 MiB` | 679.78 ns | 80,665.67 ns | 278,849.64 ns | 1,470.16 ns | 0 B |

---

## 4. Allocators

* **`HeapAllocator`** (`13.72 ns` for `64 B` align-16) matches `NativeMemory.AlignedAlloc` (`13.42 ns`) while remaining portable across `netstandard2.1` (via its OS-direct fallback at `22.67 ns`).
* **`PageAllocator`** virtual reserve/release stays flat at **`1.53–1.71 µs`** from `1` to `256` pages, while touching physical pages (`256` pages = `1 MiB`) takes `267.29 µs` due to kernel zero-fill page faults.
* **`AllocatorMirroredRingBenchmark`** shows `~7.0 µs` mirrored allocation setup and **`4.63–4.68 ns`** cross-boundary reads regardless of ring size (`64 KiB` to `4 MiB`).

### 4.1 `NativeHeapAllocatorBenchmark` (`64 B` & `4096 B` alloc+free)

| Method | `64 B` (`Align 16`) | `64 B` (`Align 64`) | `4096 B` (`Align 16`) | `4096 B` (`Align 64`) | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: |
| `NativeMemory_AlignedAlloc_Free` | 13.42 ns | 14.81 ns | 53.34 ns | 58.85 ns | 0 B |
| `NativeHeapAllocator_PrimaryDispatch` | 13.72 ns | 15.74 ns | 51.96 ns | 71.66 ns | 0 B |
| `NativeHeapAllocator_Fallback_OSDirect` | 22.67 ns | 23.80 ns | 55.47 ns | 76.58 ns | 0 B |
| `Marshal_AllocHGlobal_Free` | 11.95 ns | 12.66 ns | 48.55 ns | 49.23 ns | 0 B |

### 4.2 `KernelPageAllocatorBenchmark`

| Method | `1 Page` | `16 Pages` | `256 Pages` | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| `Reserve_And_ReleaseAddressSpace` | 1.525 µs | 1.705 µs | 1.654 µs | 0 B |
| `Reserve_Commit_And_Release` | 2.352 µs | 2.284 µs | 2.182 µs | 0 B |
| `Commit_And_Touch_Physical_Pages` | 4.050 µs | 20.053 µs | 267.293 µs | 0 B |
| `Commit_Decommit_Loop` (`InvocationCount=1`) | 5.200 µs | 5.550 µs | 4.674 µs | 0 B |

### 4.3 `AllocatorMirroredRingBenchmark`

| Method | `64 KiB` | `1 MiB` | `4 MiB` | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| `Create_And_Free_MirroredBuffer` | 7,013.9 ns | 6,953.5 ns | 7,035.5 ns | 0 B |
| `Write_Primary_Read_Mirrored` | **4.66 ns** | **4.63 ns** | **4.68 ns** | 0 B |
| `ZeroCopy_CrossBoundary_Contiguous_Blit` | 239.7 ns | 241.0 ns | 240.1 ns | 0 B |
