# Architecture & Memory Model

`ZeroAlloc.Memory` is structured in three clean layers so high-level collections stay decoupled from OS virtual memory and heap syscalls:

```text
Layer 3: Collections      SpscRing, BoundedMpscRing, MirroredSpscRing, MirroredMpscRing,
                          FlatMap, VirtualMap, MonotonicFlatMap, MonotonicVirtualMap, CircularWindow
                              ▲
Layer 2: Arenas & Layout  UnmanagedLinearArena, UnmanagedArrayTrack, UnmanagedPage, UnmanagedVirtualPage
                              ▲
Layer 1: Allocators       HeapAllocator, PageAllocator, PageAllocator.Mirrored (Windows / Linux / macOS)
```

---

## 1. OS Virtual Memory & Mirrored Ring Buffers (`PageAllocator.Mirrored`)

Standard circular ring buffers hit a painful edge case when storing variable-length frames (e.g., network packets or serialized messages): a frame near the end of the buffer wraps around to index `0`, splitting the payload into two non-contiguous slices.

`PageAllocator.Mirrored` eliminates split-copy wrap-around at the **MMU hardware page-table level**:
1. Reserves `2 × alignedSize` of contiguous virtual address space (`PROT_NONE` on POSIX, `MEM_RESERVE_PLACEHOLDER` on Windows).
2. Allocates a single anonymous shared-memory backing (`memfd_create` on Linux, `mkstemp`+`unlink` on macOS, `CreateFileMappingW` on Windows).
3. Maps the exact same physical pages into `[base, base + alignedSize)` and `[base + alignedSize, base + 2 * alignedSize)`.

Result: Reading or writing past the end of the ring seamlessly wraps into the start of the physical buffer through **one contiguous pointer / `Span<byte>`**.

---

## 2. Cache-Line Padding & ARM64 Alignment

* **`PaddedSequence`:** Producer (`_tail`) and consumer (`_head`) sequence counters are padded to avoid false sharing across CPU cores.
* **Shadow Counter Caching:** SPSC and MPSC rings cache the remote sequence counter locally and only perform cross-core volatile reads when the cached window appears full/empty.
* **ARM64 Natural Alignment:** On `x64`, unaligned 64-bit loads/stores succeed silently in hardware; on `ARM64`, `Volatile.Read`/`Write` emits `ldar`/`stlr` acquire-release instructions that fault (`DataMisalignedException`) on unaligned addresses. All `BlockHeader` structs and initial ring sequence offsets (`8` in `NativeMirroredMpscRing`) are strictly 8-byte aligned.

---

## 3. Debug Safety vs. Release Zero-Overhead

* `UnmanagedPage` and `DebugMemoryGuard` place a `0xDEADBEEF` trailing canary after allocated payload bounds.
* In **`Debug`** builds, buffer overruns throw `InvalidProgramException` on `AssertIntegrity()` and `Dispose()`.
* In **`Release`** builds, conditional checks strip cleanly to zero runtime overhead.
