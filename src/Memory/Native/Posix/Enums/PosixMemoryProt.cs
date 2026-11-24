namespace Memory.Native.Posix.Enums;

[Flags]
internal enum PosixMemoryProt : int
{
    PROT_NONE = 0x0,
    PROT_READ = 0x1,
    PROT_WRITE = 0x2,
    PROT_EXEC = 0x4,
}
