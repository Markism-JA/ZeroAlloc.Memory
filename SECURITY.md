# Security Policy

## Reporting a Vulnerability

`ZeroAlloc.Memory` uses `unsafe` code, raw pointers, and OS virtual-memory APIs (`VirtualAlloc`, `mmap`, `shm_open`). Memory-safety bugs (buffer overruns, use-after-free, alignment/bounds issues, or cross-process handle leaks) should **not** be reported in public GitHub issues.

Please report potential security or memory-safety vulnerabilities privately via **GitHub Private Vulnerability Reporting**:

- Go to the repository's **Security** tab -> **Advisories** -> **Report a vulnerability**.

When reporting, please include:
- Affected version (`ZeroAlloc.Memory` version / commit)
- OS and CPU architecture (e.g., Windows x64, Linux ARM64)
- Target framework (`net10.0` or `netstandard2.1`)
- A minimal reproduction or description of the memory-safety impact

You will receive an acknowledgment as soon as possible, and a fix will be coordinated before public disclosure.
