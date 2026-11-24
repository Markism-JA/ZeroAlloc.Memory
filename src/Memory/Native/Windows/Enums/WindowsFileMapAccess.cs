namespace Memory.Native.Windows.Enums;

[Flags]
internal enum WindowsFileMapAccess : uint
{
    FILE_MAP_WRITE = 0x0002,
    FILE_MAP_READ = 0x0004,
    FILE_MAP_ALL_ACCESS = 0x000F001F,
}
