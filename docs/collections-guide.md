# Collections & Arenas Guide: When to Use (and When NOT to Use)

> **Rule of Thumb:** If `Span<T>`, `ArrayPool<T>`, `Dictionary<TKey, TValue>`, or `System.Threading.Channels` already meets your latency and GC budget, **use the standard .NET BCL first.** Reach for `ZeroAlloc.Memory` when GC pauses, Gen0/Gen2 promotion, or split-copy ring-buffer wrapping are measurable bottlenecks.

---

## 1. Ring Buffers (Lock-Free IPC & Pipelines)

| Type | Thread Model | Payload Type | When to Use | When **NOT** to Use |
| :--- | :--- | :--- | :--- | :--- |
| **`SpscRing<T>`** | **1 Producer, 1 Consumer** | Fixed-size `unmanaged` `T` | Ultra-fast single-producer pipelines (audio, simulation ticks, single socket reader → worker) | **NEVER** with 2+ producers or 2+ consumers (data corruption). Use `BoundedMpscRing` for multi-producer. |
| **`BoundedMpscRing<T>`** | **N Producers, 1 Consumer** | Fixed-size `unmanaged` `T` | Multiple worker/network threads funneling fixed-size structs/events into one main loop | Do **not** use if you have multiple consumers, or if payloads vary wildly in byte length. |
| **`MirroredSpscRing`** | **1 Producer, 1 Consumer** | Variable-length `byte*` / `Span<byte>` | Network frames, variable-length serialized packets where zero-copy contiguous `Span<byte>` slices are needed across wrap-around | Do **not** use with multiple producers, or for tiny 8-byte structs (fixed `SpscRing<T>` avoids header overhead). |
| **`MirroredMpscRing`** | **N Producers, 1 Consumer** | Variable-length `byte*` / `Span<byte>` | Multi-socket / multi-worker ingress feeding variable-length frames into one central consumer | Do **not** use on 32-bit processes or environments where OS virtual-memory mapping is restricted. |
| **`CircularWindow<T>`** | Single-threaded | Fixed-size `unmanaged` `T` | Sliding time/history window (e.g., last *N* tick samples or moving averages) | Not thread-safe; do not share concurrently without external synchronization. |

---

## 2. Unmanaged Maps & Lookup Tables

| Type | Key / Value Constraint | Deletion Support | When to Use | When **NOT** to Use |
| :--- | :--- | :--- | :--- | :--- |
| **`FlatMap<TKey, TValue>`** | `unmanaged` | Yes | Fast, cache-friendly unmanaged hash map with fixed capacity | Do **not** store managed references (`class`, `string`) or exceed planned capacity without resizing. |
| **`VirtualMap<TKey, TValue>`** | `unmanaged` | Yes | Large hash maps backed by virtual memory (`Reserve` + demand-commit pages as table grows) | Overkill for tiny maps (`< 1,000` entries) — `FlatMap` is simpler and faster. |
| **`MonotonicFlatMap<TKey, TValue>`** | `unmanaged` | **No** (reset/clear only) | Append-only per-frame/per-generation lookup tables cleared wholesale at end of tick | Do **not** use if you need to remove individual keys mid-lifecycle. |
| **`MonotonicVirtualMap<TKey, TValue>`** | `unmanaged` | **No** (reset/clear only) | Large append-only generational maps that scale via virtual memory pages | Do **not** use if individual key deletion is required. |

---

## 3. Arenas & Page Allocators

| Type | Lifetime Model | When to Use | When **NOT** to Use |
| :--- | :--- | :--- | :--- |
| **`UnmanagedLinearArena`** | Bump-allocate many items, `Reset()` or `Dispose()` all at once | Per-frame / per-tick scratch buffers, transient pathfinding/physics lists | **Do NOT use** for objects with mixed, long-lived lifetimes (you cannot free individual allocations). |
| **`UnmanagedArrayTrack<T>`** | Arena-backed contiguous array growth | Scratch lists of `T` inside a linear arena without GC arrays | Same as `UnmanagedLinearArena` — bulk lifetime only. |
| **`UnmanagedPage` / `UnmanagedVirtualPage`** | Explicit page-carving handle | Custom allocators, ECS chunk backing, or low-level memory pools | Application-level code should prefer `UnmanagedLinearArena` or `NativeMemory`. |
