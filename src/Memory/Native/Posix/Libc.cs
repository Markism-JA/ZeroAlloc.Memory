using System.Runtime.InteropServices;
using Memory.Native.Posix.Enums;

namespace Memory.Native.Posix;

internal static unsafe partial class Libc
{
    private const string LibName = "libc";

    public static readonly void* MAP_FAILED = (void*)(-1);
    public const int _SC_PAGESIZE = 30;

#if NET7_0_OR_GREATER
    [LibraryImport(LibName, EntryPoint = "mmap", SetLastError = true)]
    public static partial void* Mmap(
        void* addr,
        nuint length,
        PosixMemoryProt prot,
        PosixMemoryFlags flags,
        int fd,
        nint offset
    );

    [LibraryImport(LibName, EntryPoint = "munmap", SetLastError = true)]
    public static partial int Munmap(void* addr, nuint length);

    [LibraryImport(LibName, EntryPoint = "mprotect", SetLastError = true)]
    public static partial int Mprotect(void* addr, nuint len, PosixMemoryProt prot);

    [LibraryImport(LibName, EntryPoint = "madvise", SetLastError = true)]
    public static partial int Madvise(void* addr, nuint length, MadviseAdvice advice);

    [LibraryImport(LibName, EntryPoint = "memfd_create", SetLastError = true)]
    public static partial int MemfdCreate(byte* name, uint flags);

    [LibraryImport(LibName, EntryPoint = "mkstemp", SetLastError = true)]
    public static partial int Mkstemp(byte* template);

    [LibraryImport(LibName, EntryPoint = "unlink", SetLastError = true)]
    public static partial int Unlink(byte* path);

    [LibraryImport(LibName, EntryPoint = "ftruncate", SetLastError = true)]
    public static partial int Ftruncate(int fd, nint length);

    [LibraryImport(LibName, EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport(LibName, EntryPoint = "posix_memalign", SetLastError = true)]
    public static partial int PosixMemalign(void** memptr, nuint alignment, nuint size);

    [LibraryImport(LibName, EntryPoint = "free")]
    public static partial void Free(void* ptr);

    [LibraryImport(LibName, EntryPoint = "sysconf", SetLastError = true)]
    public static partial nint Sysconf(int name);
#else
    [DllImport(LibName, EntryPoint = "mmap", SetLastError = true)]
    public static extern void* Mmap(
        void* addr,
        UIntPtr length,
        PosixMemoryProt prot,
        PosixMemoryFlags flags,
        int fd,
        IntPtr offset
    );

    [DllImport(LibName, EntryPoint = "munmap", SetLastError = true)]
    public static extern int Munmap(void* addr, UIntPtr length);

    [DllImport(LibName, EntryPoint = "mprotect", SetLastError = true)]
    public static extern int Mprotect(void* addr, UIntPtr len, PosixMemoryProt prot);

    [DllImport(LibName, EntryPoint = "madvise", SetLastError = true)]
    public static extern int Madvise(void* addr, UIntPtr length, MadviseAdvice advice);

    [DllImport(LibName, EntryPoint = "memfd_create", SetLastError = true)]
    public static extern int MemfdCreate(byte* name, uint flags);

    [DllImport(LibName, EntryPoint = "mkstemp", SetLastError = true)]
    public static extern int Mkstemp(byte* template);

    [DllImport(LibName, EntryPoint = "unlink", SetLastError = true)]
    public static extern int Unlink(byte* path);

    [DllImport(LibName, EntryPoint = "ftruncate", SetLastError = true)]
    public static extern int Ftruncate(int fd, IntPtr length);

    [DllImport(LibName, EntryPoint = "close", SetLastError = true)]
    public static extern int Close(int fd);

    [DllImport(LibName, EntryPoint = "posix_memalign", SetLastError = true)]
    public static extern int PosixMemalign(void** memptr, UIntPtr alignment, UIntPtr size);

    [DllImport(LibName, EntryPoint = "free")]
    public static extern void Free(void* ptr);

    [DllImport(LibName, EntryPoint = "sysconf", SetLastError = true)]
    public static extern IntPtr Sysconf(int name);
#endif
}
