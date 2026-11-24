using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Native.Posix;
using Memory.Native.Posix.Enums;
using Memory.Native.Windows;
using Memory.Native.Windows.Enums;
using Memory.Native.Windows.Structs;
#if NET6_0_OR_GREATER
using NativeUInt = nuint;
using NativeInt = nint;
#else
using NativeUInt = System.UIntPtr;
using NativeInt = System.IntPtr;
#endif

namespace Memory.Allocators;

/// <summary>
/// Provides low-level kernel page allocation and virtual address space management.
/// </summary>
public unsafe partial struct PageAllocator
{
    private const int _scPageSizeLinux = 30;
    private const int _scPageSizeOsx = 29;

    /// <summary>
    /// Gets the host operating system physical page size in bytes (e.g. 4096 on x64/Linux, 16384 on Apple Silicon).
    /// </summary>
    public static readonly int SystemPageSize;

    /// <summary>
    /// Gets the page alignment bitwise mask (<c>SystemPageSize - 1</c>).
    /// </summary>
    public static readonly ulong PageAlignmentMask;

    static PageAllocator()
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("ZeroAlloc.Memory requires a 64-bit (x64 or ARM64) process.");
        }

        int pageSize = 4096;

        try
        {
#if NET8_0_OR_GREATER
            pageSize = Environment.SystemPageSize;
#else
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SystemInfo si = default;
                Kernel32.GetSystemInfo(&si);
                pageSize = (int)si.dwPageSize;
            }
            else
            {
                int scPageSize = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? _scPageSizeOsx
                    : _scPageSizeLinux;
                NativeInt res = Libc.Sysconf(scPageSize);
                long numericResult = (long)(void*)res;
                if (numericResult > 0)
                {
                    pageSize = (int)numericResult;
                }
            }
#endif
        }
        catch
        {
            pageSize = 4096;
        }

        SystemPageSize = pageSize;
        PageAlignmentMask = (ulong)(pageSize - 1);
    }

    /// <summary>
    /// Reserves raw virtual address space boundaries without committing physical RAM or swap memory.
    /// </summary>
    /// <param name="bytes">The size of the virtual address window to reserve in bytes. Will be rounded up to system page size.</param>
    /// <returns>A raw pointer pointing to the base reserved virtual address, or <c>null</c> if reservation failed.</returns>
    public static void* ReserveAddressSpace(int bytes)
    {
        int mask = SystemPageSize - 1;
        int pageAlignedBytes = (bytes + mask) & ~mask;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Kernel32.VirtualAlloc(
                null,
                (NativeUInt)(uint)pageAlignedBytes,
                WindowsMemAllocFlags.MEM_RESERVE,
                WindowsPageProtection.PAGE_NOACCESS
            );
        }
        else
        {
            void* ptr = Libc.Mmap(
                null,
                (NativeUInt)(uint)pageAlignedBytes,
                PosixMemoryProt.PROT_NONE,
                PosixMemoryFlags.MAP_PRIVATE | PosixMemoryFlags.MAP_ANONYMOUS,
                -1,
                (NativeInt)0
            );

            return ptr == Libc.MAP_FAILED ? null : ptr;
        }
    }

    /// <summary>
    /// Commits physical OS page frames to back a previously reserved virtual memory range, making it readable and writable.
    /// </summary>
    /// <param name="ptr">The base pointer to the virtual range to commit.</param>
    /// <param name="bytes">The size of the memory range in bytes. Will be page-aligned.</param>
    /// <returns><c>true</c> if physical OS page frame commitment succeeded; otherwise, <c>false</c>.</returns>
    public static bool CommitVirtualRange(void* ptr, int bytes)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            int mask = SystemPageSize - 1;
            int pageAlignedBytes = (bytes + mask) & ~mask;

            return Kernel32.VirtualAlloc(
                    ptr,
                    (NativeUInt)(uint)pageAlignedBytes,
                    WindowsMemAllocFlags.MEM_COMMIT,
                    WindowsPageProtection.PAGE_READWRITE
                ) != null;
        }
        else
        {
            ulong mask = PageAlignmentMask;
            ulong rawAddress = (ulong)ptr;

            ulong alignedAddress = rawAddress & ~mask;
            void* pageAlignedPtr = (void*)alignedAddress;

            long shiftLoss = (long)(rawAddress - alignedAddress);
            long adjustedBytes = bytes + shiftLoss;
            long pageAlignedBytes = (adjustedBytes + (long)mask) & ~(long)mask;

            return Libc.Mprotect(
                    pageAlignedPtr,
                    (NativeUInt)(uint)pageAlignedBytes,
                    PosixMemoryProt.PROT_READ | PosixMemoryProt.PROT_WRITE
                ) == 0;
        }
    }

    /// <summary>
    /// Decommits physical OS page backing for a virtual range while preserving address space reservations.
    /// </summary>
    /// <param name="ptr">The base address of the memory range to decommit.</param>
    /// <param name="bytes">The size of the memory range in bytes. Must meet or exceed system page size.</param>
    /// <returns><c>true</c> if physical RAM was successfully returned to the kernel; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Decommit(void* ptr, int bytes)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            int mask = SystemPageSize - 1;
            int pageAlignedBytes = bytes & ~mask;
            if (pageAlignedBytes < SystemPageSize)
                return false;

            return Kernel32.VirtualFree(
                ptr,
                (NativeUInt)(uint)pageAlignedBytes,
                WindowsMemAllocFlags.MEM_DECOMMIT
            );
        }
        else
        {
            ulong mask = PageAlignmentMask;
            ulong rawAddress = (ulong)ptr;

            ulong alignedAddress = rawAddress & ~mask;
            void* pageAlignedPtr = (void*)alignedAddress;

            long shiftLoss = (long)(rawAddress - alignedAddress);
            long adjustedBytes = bytes + shiftLoss;
            long pageAlignedBytes = (adjustedBytes + (long)mask) & ~(long)mask;

            if (pageAlignedBytes < SystemPageSize)
                return false;

            Libc.Madvise(
                pageAlignedPtr,
                (NativeUInt)(uint)pageAlignedBytes,
                MadviseAdvice.DontNeed
            );

            return Libc.Mprotect(
                    pageAlignedPtr,
                    (NativeUInt)(uint)pageAlignedBytes,
                    PosixMemoryProt.PROT_NONE
                ) == 0;
        }
    }

    /// <summary>
    /// Releases reserved virtual address space back to the operating system kernel.
    /// </summary>
    /// <param name="ptr">The base address of the reserved virtual memory block.</param>
    /// <param name="bytes">The total reserved size in bytes.</param>
    public static void ReleaseVirtual(void* ptr, int bytes)
    {
        int mask = SystemPageSize - 1;
        int pageAlignedBytes = (bytes + mask) & ~mask;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Kernel32.VirtualFree(ptr, (NativeUInt)0, WindowsMemAllocFlags.MEM_RELEASE);
        }
        else
        {
            Libc.Munmap(ptr, (NativeUInt)(uint)pageAlignedBytes);
        }
    }
}
