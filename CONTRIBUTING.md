# Contributing to ZeroAlloc.Memory

Thank you for your interest in contributing to **ZeroAlloc.Memory**. This is a focused, solo-maintained, low-level unmanaged memory library—keeping changes small, well-tested, and strictly scoped helps keep it reliable.

## Before You Start

- **Open an issue first** before starting work on new collections, allocators, or public API changes so we can align on scope and design.
- **Bug fixes, documentation improvements, and tests** are always welcome via pull request.
- Please note that maintenance happens on a best-effort schedule; PRs outside the library's core scope may be declined.

## Development Requirements

- **.NET SDK:** Matches `global.json` (targets `net10.0` and `netstandard2.1`).
- **Zero warnings:** Builds must succeed with `0 Warning(s)` and `0 Error(s)` in `Release` mode, including `CS1591` XML documentation on all public types and members.
- **Target frameworks:** Changes must compile cleanly on both `net10.0` and `netstandard2.1` (64-bit AnyCPU: `x64` and `ARM64`).

### Local Verification

```bash
dotnet build --no-incremental -c Release
dotnet test -c Release --no-build
```

## Expectations for `unsafe` & Concurrent Code

Because `ZeroAlloc.Memory` deals with raw pointers, virtual-memory mirroring, and lock-free rings:

1. **Tests are mandatory:** PRs touching allocators, arenas, virtual-memory layouts, or concurrent rings must include unit tests (and stress/concurrency tests for thread-safety changes).
2. **No unverified performance claims:** PRs claiming throughput or allocation improvements should include BenchmarkDotNet results from `benchmark/Memory.Benchmark`.
3. **Preserve safety invariants:** Document ownership, alignment, lifetime/disposal rules, and memory-ordering assumptions clearly in XML docs and inline comments.
