using System.Runtime.InteropServices;
using Memory.Native.Posix;
using Memory.Native.Posix.Enums;
using Memory.Native.Windows;
using Memory.Native.Windows.Enums;
#if NET6_0_OR_GREATER
using NativeUInt = nuint;
using NativeInt = nint;
#else
using NativeUInt = System.UIntPtr;
using NativeInt = System.IntPtr;
#endif

namespace Memory.Allocators;

public unsafe partial struct PageAllocator
{
    /// <summary>
    /// Allocates a virtual memory block where the exact same physical pages are mirrored back-to-back sequentially in virtual address space.
    /// </summary>
    /// <param name="size">The size of a single buffer window in bytes.</param>
    /// <param name="nativeMappingHandle">When this method returns, contains the OS native mapping handle required for cleanup.</param>
    /// <returns>A pointer to the start of the first mirrored buffer window, or <c>null</c> if memory mapping failed.</returns>
    public static byte* CreateMirroredBuffer(int size, out void* nativeMappingHandle)
    {
        nativeMappingHandle = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            int mask = Kernel32.ALLOCATION_GRANULARITY - 1;
            int alignedSize = (size + mask) & ~mask;
            return CreateMirroredBufferWindows(alignedSize, out nativeMappingHandle);
        }
        else
        {
            int mask = SystemPageSize - 1;
            int alignedSize = (size + mask) & ~mask;
            return CreateMirroredBufferLinux(alignedSize, out nativeMappingHandle);
        }
    }

    /// <summary>
    /// Unmaps and releases a mirrored virtual ring buffer and destroys its OS mapping handles.
    /// </summary>
    /// <param name="bufferStart">The base pointer to the start of the mirrored buffer.</param>
    /// <param name="size">The capacity size of a single buffer window in bytes.</param>
    /// <param name="nativeMappingHandle">The OS native handle returned during buffer creation.</param>
    public static void FreeMirroredBuffer(byte* bufferStart, long size, void* nativeMappingHandle)
    {
        if (bufferStart == null)
            return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            long mask = Kernel32.ALLOCATION_GRANULARITY - 1;
            long alignedSize = (size + mask) & ~mask;
            FreeMirroredBufferWindows(bufferStart, alignedSize, nativeMappingHandle);
        }
        else
        {
            long mask = SystemPageSize - 1;
            long alignedSize = (size + mask) & ~mask;
            FreeMirroredBufferLinux(bufferStart, alignedSize, nativeMappingHandle);
        }
    }

    #region Windows Placeholder Implementation

    private static byte* CreateMirroredBufferWindows(int alignedSize, out void* nativeMappingHandle)
    {
        nativeMappingHandle = null;

        void* mapping = Kernel32.CreateFileMappingW(
            Kernel32.INVALID_HANDLE_VALUE,
            null,
            WindowsPageProtection.PAGE_READWRITE,
            0,
            (uint)alignedSize,
            null
        );

        if (mapping == null)
            return null;

        NativeUInt totalSize = (NativeUInt)(uint)(alignedSize * 2);
        void* hProcess = Kernel32.GetCurrentProcess();

        // 1. Atomically reserve 2x contiguous address space as placeholder
        void* placeholder = Kernel32.VirtualAlloc2(
            Process: hProcess,
            BaseAddress: null,
            Size: totalSize,
            AllocationType: WindowsMemAllocFlags.MEM_RESERVE
                | WindowsMemAllocFlags.MEM_RESERVE_PLACEHOLDER,
            PageProtection: WindowsPageProtection.PAGE_NOACCESS,
            ExtendedParameters: null,
            ParameterCount: 0
        );

        if (placeholder == null)
        {
            Kernel32.CloseHandle(mapping);
            return null;
        }

        // 2. Split into two consecutive 1x placeholders
        bool split = Kernel32.VirtualFree(
            placeholder,
            (NativeUInt)(uint)alignedSize,
            WindowsMemAllocFlags.MEM_RELEASE | WindowsMemAllocFlags.MEM_PRESERVE_PLACEHOLDER
        );

        if (!split)
        {
            Kernel32.VirtualFree(placeholder, (NativeUInt)0, WindowsMemAllocFlags.MEM_RELEASE);
            Kernel32.CloseHandle(mapping);
            return null;
        }

        byte* baseAddress = (byte*)placeholder;

        // 3. Map View A into the first placeholder
        void* viewA = Kernel32.MapViewOfFile3(
            FileMapping: mapping,
            Process: hProcess,
            BaseAddress: baseAddress,
            Offset: 0,
            NumberOfBytesToMap: (NativeUInt)(uint)alignedSize,
            AllocationType: WindowsMemAllocFlags.MEM_REPLACE_PLACEHOLDER,
            PageProtection: WindowsPageProtection.PAGE_READWRITE,
            ExtendedParameters: null,
            ParameterCount: 0
        );

        // 4. Map View B into the adjacent placeholder
        void* viewB = Kernel32.MapViewOfFile3(
            FileMapping: mapping,
            Process: hProcess,
            BaseAddress: baseAddress + alignedSize,
            Offset: 0,
            NumberOfBytesToMap: (NativeUInt)(uint)alignedSize,
            AllocationType: WindowsMemAllocFlags.MEM_REPLACE_PLACEHOLDER,
            PageProtection: WindowsPageProtection.PAGE_READWRITE,
            ExtendedParameters: null,
            ParameterCount: 0
        );

        if (viewA == null || viewB == null)
        {
            if (viewA != null)
                Kernel32.UnmapViewOfFile(viewA);
            if (viewB != null)
                Kernel32.UnmapViewOfFile(viewB);
            Kernel32.CloseHandle(mapping);
            return null;
        }

        nativeMappingHandle = mapping;
        return baseAddress;
    }

    private static void FreeMirroredBufferWindows(
        byte* bufferStart,
        long alignedSize,
        void* nativeMappingHandle
    )
    {
        Kernel32.UnmapViewOfFile(bufferStart);
        Kernel32.UnmapViewOfFile(bufferStart + alignedSize);

        if (nativeMappingHandle != null)
        {
            Kernel32.CloseHandle(nativeMappingHandle);
        }
    }

    #endregion

    #region Linux Memfd Implementation

    private static byte* CreateMirroredBufferLinux(int alignedSize, out void* nativeMappingHandle)
    {
        nativeMappingHandle = null;

        byte* name =
            stackalloc byte[] {
                (byte)'e',
                (byte)'c',
                (byte)'h',
                (byte)'o',
                (byte)'e',
                (byte)'s',
                (byte)'_',
                (byte)'v',
                (byte)'i',
                (byte)'r',
                (byte)'t',
                (byte)'u',
                (byte)'a',
                (byte)'l',
                (byte)'_',
                (byte)'r',
                (byte)'i',
                (byte)'n',
                (byte)'g',
                0,
            };

        int fd = Libc.MemfdCreate(name, 0);
        if (fd < 0)
            return null;

        if (Libc.Ftruncate(fd, (NativeInt)alignedSize) != 0)
        {
            Libc.Close(fd);
            return null;
        }

        byte* placeholder = (byte*)
            Libc.Mmap(
                null,
                (NativeUInt)(uint)(alignedSize * 2),
                PosixMemoryProt.PROT_NONE,
                PosixMemoryFlags.MAP_PRIVATE | PosixMemoryFlags.MAP_ANONYMOUS,
                -1,
                (NativeInt)0
            );

        if (placeholder == Libc.MAP_FAILED)
        {
            Libc.Close(fd);
            return null;
        }

        void* viewA = Libc.Mmap(
            placeholder,
            (NativeUInt)(uint)alignedSize,
            PosixMemoryProt.PROT_READ | PosixMemoryProt.PROT_WRITE,
            PosixMemoryFlags.MAP_SHARED | PosixMemoryFlags.MAP_FIXED,
            fd,
            (NativeInt)0
        );

        void* viewB = Libc.Mmap(
            placeholder + alignedSize,
            (NativeUInt)(uint)alignedSize,
            PosixMemoryProt.PROT_READ | PosixMemoryProt.PROT_WRITE,
            PosixMemoryFlags.MAP_SHARED | PosixMemoryFlags.MAP_FIXED,
            fd,
            (NativeInt)0
        );

        if (viewA == Libc.MAP_FAILED || viewB == Libc.MAP_FAILED)
        {
            Libc.Munmap(placeholder, (NativeUInt)(uint)(alignedSize * 2));
            Libc.Close(fd);
            return null;
        }

        nativeMappingHandle = (void*)(NativeInt)fd;
        return placeholder;
    }

    private static void FreeMirroredBufferLinux(
        byte* bufferStart,
        long alignedSize,
        void* nativeMappingHandle
    )
    {
        ulong totalVirtualRange = (ulong)(alignedSize * 2);
        Libc.Munmap(bufferStart, (NativeUInt)totalVirtualRange);

        if (nativeMappingHandle != null)
        {
            Libc.Close((int)(NativeInt)nativeMappingHandle);
        }
    }

    #endregion
}
