## Summary

<!-- Describe what this PR changes and why. Link the related issue if applicable. -->

## Checklist

- [ ] Built cleanly with zero warnings (`dotnet build --no-incremental -c Release`, including `CS1591` XML docs)
- [ ] All tests pass (`dotnet test -c Release --no-build`)
- [ ] Verified compatibility with both `net10.0` and `netstandard2.1`
- [ ] Added/updated unit or concurrency tests for behavior, `unsafe`, or lock-free changes
- [ ] Added BenchmarkDotNet numbers if claiming performance improvements
