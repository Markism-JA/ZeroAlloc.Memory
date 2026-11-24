using System.Runtime.InteropServices;
using Memory.Native.Windows.Enums;
using Memory.Native.Windows.Structs;

namespace Memory.Native.Windows;

internal static unsafe partial class Kernel32
{
    private const string Kernel32Lib = "kernel32.dll";
    private const string KernelBaseLib = "kernelbase.dll";

    public static readonly void* INVALID_HANDLE_VALUE = (void*)(-1);
    public static readonly void* CURRENT_PROCESS_HANDLE = (void*)(-1);
    public const int ALLOCATION_GRANULARITY = 64 * 1024;

#if NET7_0_OR_GREATER
    [LibraryImport(Kernel32Lib, EntryPoint = "VirtualAlloc", SetLastError = true)]
    public static partial void* VirtualAlloc(
        void* lpAddress,
        nuint dwSize,
        WindowsMemAllocFlags flAllocationType,
        WindowsPageProtection flProtect
    );

    [LibraryImport(Kernel32Lib, EntryPoint = "VirtualFree", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFree(
        void* lpAddress,
        nuint dwSize,
        WindowsMemAllocFlags dwFreeType
    );

    [LibraryImport(KernelBaseLib, EntryPoint = "VirtualAlloc2", SetLastError = true)]
    public static partial void* VirtualAlloc2(
        void* Process,
        void* BaseAddress,
        nuint Size,
        WindowsMemAllocFlags AllocationType,
        WindowsPageProtection PageProtection,
        void* ExtendedParameters,
        uint ParameterCount
    );

    [LibraryImport(KernelBaseLib, EntryPoint = "MapViewOfFile3", SetLastError = true)]
    public static partial void* MapViewOfFile3(
        void* FileMapping,
        void* Process,
        void* BaseAddress,
        ulong Offset,
        nuint NumberOfBytesToMap,
        WindowsMemAllocFlags AllocationType,
        WindowsPageProtection PageProtection,
        void* ExtendedParameters,
        uint ParameterCount
    );

    [LibraryImport(Kernel32Lib, EntryPoint = "CreateFileMappingW", SetLastError = true)]
    public static partial void* CreateFileMappingW(
        void* hFile,
        void* lpAttributes,
        WindowsPageProtection flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        void* lpName
    );

    [LibraryImport(Kernel32Lib, EntryPoint = "UnmapViewOfFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(void* lpBaseAddress);

    [LibraryImport(Kernel32Lib, EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(void* hObject);

    [LibraryImport(Kernel32Lib, EntryPoint = "GetCurrentProcess")]
    public static partial void* GetCurrentProcess();

    [LibraryImport(Kernel32Lib, EntryPoint = "GetSystemInfo")]
    public static partial void GetSystemInfo(SystemInfo* lpSystemInfo);
#else
    [DllImport(Kernel32Lib, EntryPoint = "VirtualAlloc", SetLastError = true)]
    public static extern void* VirtualAlloc(
        void* lpAddress,
        UIntPtr dwSize,
        WindowsMemAllocFlags flAllocationType,
        WindowsPageProtection flProtect
    );

    [DllImport(Kernel32Lib, EntryPoint = "VirtualFree", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFree(
        void* lpAddress,
        UIntPtr dwSize,
        WindowsMemAllocFlags dwFreeType
    );

    [DllImport(
        KernelBaseLib,
        EntryPoint = "VirtualAlloc2",
        SetLastError = true,
        ExactSpelling = true
    )]
    public static extern void* VirtualAlloc2(
        void* Process,
        void* BaseAddress,
        UIntPtr Size,
        WindowsMemAllocFlags AllocationType,
        WindowsPageProtection PageProtection,
        void* ExtendedParameters,
        uint ParameterCount
    );

    [DllImport(
        KernelBaseLib,
        EntryPoint = "MapViewOfFile3",
        SetLastError = true,
        ExactSpelling = true
    )]
    public static extern void* MapViewOfFile3(
        void* FileMapping,
        void* Process,
        void* BaseAddress,
        ulong Offset,
        UIntPtr NumberOfBytesToMap,
        WindowsMemAllocFlags AllocationType,
        WindowsPageProtection PageProtection,
        void* ExtendedParameters,
        uint ParameterCount
    );

    [DllImport(
        Kernel32Lib,
        EntryPoint = "CreateFileMappingW",
        SetLastError = true,
        ExactSpelling = true
    )]
    public static extern void* CreateFileMappingW(
        void* hFile,
        void* lpAttributes,
        WindowsPageProtection flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        void* lpName
    );

    [DllImport(
        Kernel32Lib,
        EntryPoint = "UnmapViewOfFile",
        SetLastError = true,
        ExactSpelling = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnmapViewOfFile(void* lpBaseAddress);

    [DllImport(Kernel32Lib, EntryPoint = "CloseHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(void* hObject);

    [DllImport(Kernel32Lib, EntryPoint = "GetCurrentProcess", ExactSpelling = true)]
    public static extern void* GetCurrentProcess();

    [DllImport(Kernel32Lib, EntryPoint = "GetSystemInfo", ExactSpelling = true)]
    public static extern void GetSystemInfo(SystemInfo* lpSystemInfo);
#endif
}
