# ZeroAlloc.Memory

[![CI](https://github.com/Markism-JA/ZeroAlloc.Memory/actions/workflows/ci.yml/badge.svg)](https://github.com/Markism-JA/ZeroAlloc.Memory/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Memory.svg)](https://www.nuget.org/packages/ZeroAlloc.Memory)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)

Zero-allocation unmanaged memory allocators, linear arenas, virtual-memory mirrored ring buffers, and native collections for **.NET 10** and **`netstandard2.1`** (Unity / client / server).

Built as **`AnyCPU`** and tested across **64-bit `x64` and `ARM64`** (Linux, Windows, and macOS / Apple Silicon).

---

## Installation

```bash
dotnet add package ZeroAlloc.Memory
```

> **Runtime Requirement:** `ZeroAlloc.Memory` is compiled as `AnyCPU` for maximum portability across `x64` and `ARM64`, and requires a **64-bit process** (`Environment.Is64BitProcess == true`).

---

## What’s Included

* **Allocators (`Memory.Allocators`)**
  * `HeapAllocator` — Cache-line aligned unmanaged heap allocations (`NativeMemory.AlignedAlloc` on .NET 6+, Unity `UnsafeUtility.Malloc`, or OS fallbacks).
  * `PageAllocator` & `PageAllocator.Mirrored` — OS virtual memory reserve/commit/decommit and double-mapped circular ring-buffer virtual memory across Windows, Linux, and macOS (`4KB` and `16KB` page sizes).
* **Arenas (`Memory.Arena`)**
  * `UnmanagedLinearArena` — Fast bump-pointer arena allocator for frame/tick-scoped scratch allocations with zero GC pressure.
  * `UnmanagedArrayTrack` — Contiguous unmanaged array tracks backed by arenas.
* **Lock-Free & Native Collections (`Memory.Collections`)**
  * `SpscRing` / `NativeSpscRing` — Cache-line padded single-producer/single-consumer ring buffer.
  * `MirroredSpscRing` & `MirroredMpscRing` — Virtual-memory mirrored variable-length block ring buffers (contiguous wrap-around reads/writes without split copies).
  * `BoundedMpscRing` — Bounded multi-producer/single-consumer ring buffer.
  * `FlatMap` / `VirtualMap` / `MonotonicFlatMap` / `MonotonicVirtualMap` — Open-addressed unmanaged hash maps and monotonic lookup tables.
  * `CircularWindow` — Fixed-capacity sliding window over unmanaged memory.
* **Layout & Utilities (`Memory.Layout`, `Memory.Utilities`)**
  * `UnmanagedPage` / `UnmanagedVirtualPage` — Explicit page-carving primitives.
  * `BitOperation`, `MemoryLayout`, `DebugMemoryGuard`.

---

## Quick Start

```csharp
using Memory.Arena;
using Memory.Collections.Spsc;

// 1. Frame/tick-scoped linear arena (0 GC allocations)
var arena = new UnmanagedLinearArena(capacityBytes: 64 * 1024);
try
{
    int* buffer = arena.Allocate<int>(128);
    buffer[0] = 42;
}
finally
{
    arena.Dispose();
}

// 2. Cache-padded SPSC ring buffer
using var ring = new SpscRing<long>(capacity: 1024);
ring.TryEnqueue(123456L);
ring.TryDequeue(out long value);
```

---

## Building, Testing & Benchmarking

```bash
# Run full unit & integration test suite
dotnet test -c Release

# Verify netstandard2.1 compatibility
dotnet build src/Memory/Memory.csproj -c Release -f netstandard2.1

# Run BenchmarkDotNet suite
dotnet run --project benchmark/Memory.Benchmark -c Release -- --filter *
```

---

## License

[MIT](./LICENSE) © 2026 Markism-JA
