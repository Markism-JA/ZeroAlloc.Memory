# ZeroAlloc.Memory

[![CI](https://github.com/Markism-JA/ZeroAlloc.Memory/actions/workflows/ci.yml/badge.svg)](https://github.com/Markism-JA/ZeroAlloc.Memory/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Memory.svg)](https://www.nuget.org/packages/ZeroAlloc.Memory)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)

Zero-allocation unmanaged memory allocators, linear arenas, virtual-memory mirrored ring buffers, and native collections for **.NET 10** and **`netstandard2.1`** (Unity / client / server).

Compiled as **`AnyCPU`** and tested in CI across **64-bit `x64` and `ARM64`** (`linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`).

---

## Origin & Intent

`ZeroAlloc.Memory` was originally built as the memory and IPC foundation for a high-tickrate distributed game/simulation engine (*Echoes of Bathala*) where:
* **Gen0/Gen2 GC pauses** in hot simulation and networking loops were unacceptable,
* **Variable-length ring buffers** needed contiguous `Span<byte>` reads/writes across wrap-around boundaries without split copies, and
* **One shared codebase** had to run identically on `.NET 10` Linux servers (`x64`/`ARM64`) and `netstandard2.1` Unity clients.

---

## Installation

```bash
dotnet add package ZeroAlloc.Memory
```

---

## When to Use (and When NOT to Use)

| Use `ZeroAlloc.Memory` When… | Do **NOT** Use When… |
| :--- | :--- |
| Standard `ArrayPool<T>`, `Dictionary<K,V>`, or `Channel<T>` causes measurable GC pauses in hot loops. | Standard .NET BCL collections already meet your latency budget. |
| You store `unmanaged` structs or raw byte frames with deterministic tick/frame lifetimes. | You need to store managed reference types (`class`, `string`). |
| You have **strictly 1P/1C** (`SpscRing`, `MirroredSpscRing`) or **NP/1C** (`BoundedMpscRing`, `MirroredMpscRing`) thread topologies. | You have **multiple concurrent consumers** (MPMC is intentionally excluded). |

See the full **[Collections & Arenas Decision Guide](./docs/collections-guide.md)** and **[Architecture & Memory Model](./docs/architecture.md)**.

---

## Safety Contract & Known Limitations

1. **64-Bit Process Required:** Built as `AnyCPU` for `x64` and `ARM64` portability; 32-bit processes (`x86`/`arm32`) are unsupported and throw `PlatformNotSupportedException` in `PageAllocator`.
2. **Manual Lifetime (`IDisposable`):** Value-type native handles and wrappers do not rely on GC finalizers for hot-path cleanup. Failing to call `Dispose()` leaks unmanaged/virtual memory.
3. **Thread-Topology Guarantees:** `SpscRing` and `MirroredSpscRing` are **strictly Single-Producer / Single-Consumer**. Calling `TryEnqueue`/`TryWrite` from multiple threads requires `BoundedMpscRing` or `MirroredMpscRing`.
4. **Debug vs. Release Guards:** `UnmanagedPage` / `DebugMemoryGuard` trailing canaries (`0xDEADBEEF`) catch buffer overruns in `Debug` builds and compile out to zero overhead in `Release`.

---

## Quick Start

```csharp
using Memory.Arena;
using Memory.Collections.Spsc;

// 1. Frame/tick-scoped linear arena (0 GC allocations)
using var arena = new UnmanagedLinearArena(capacityBytes: 64 * 1024);
int* buffer = arena.Allocate<int>(128);
buffer[0] = 42;

// 2. Cache-padded SPSC ring buffer (strictly 1 producer, 1 consumer)
using var ring = new SpscRing<long>(capacity: 1024);
ring.TryEnqueue(123456L);
ring.TryDequeue(out long value);
```

---

## Benchmarks (`.NET 10 X64`, `0 B` Managed Allocation)

![FlatMap vs Dictionary Latency](./docs/benchmarks/flatmap_vs_dictionary.png)

* **`FlatMap` / `NativeFlatMap` vs `Dictionary<K,V>` (`10,000` keys):** `276 µs` vs `759 µs` on hits (**2.75× faster**), `175 µs` vs `855 µs` on in-place mutation (**4.88× faster**), `0 B` allocated. *(Note: dense full-table iteration remains faster in `Dictionary` — see full tradeoffs.)*
* **`UnmanagedLinearArena` (`10,000` batch slices):** `4,101 ns` (`0.41 ns/item`, `0 B` allocated).
* **`SpscRing` (`64B` zero-copy round-trip):** `269.8 ns`, `0 B` allocated.

Full BenchmarkDotNet tables & reproduction commands: **[docs/benchmarks/README.md](./docs/benchmarks/README.md)**

---

## Building, Testing & Benchmarking

```bash
# Run full unit & concurrency stress suite (223 tests)
dotnet test -c Release

# Verify netstandard2.1 compatibility
dotnet build src/Memory/Memory.csproj -c Release -f netstandard2.1

# Run BenchmarkDotNet suite
dotnet run --project benchmark/Memory.Benchmark -c Release -- --filter *
```

---

## License

[MIT](./LICENSE) © 2026 Markism-JA
