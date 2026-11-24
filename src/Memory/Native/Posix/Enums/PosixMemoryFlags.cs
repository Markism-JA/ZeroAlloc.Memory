namespace Memory.Native.Posix.Enums;

[Flags]
internal enum PosixMemoryFlags : int
{
    MAP_SHARED = 0x01,
    MAP_PRIVATE = 0x02,
    MAP_FIXED = 0x10,
    MAP_ANONYMOUS = 0x20,
    MAP_ANON_OSX = 0x1000,
}
