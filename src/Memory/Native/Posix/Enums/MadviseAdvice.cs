namespace Memory.Native.Posix.Enums;

internal enum MadviseAdvice : int
{
    Normal = 0,
    Random = 1,
    Sequential = 2,
    WillNeed = 3,
    DontNeed = 4,
    HugePage = 14,
}
