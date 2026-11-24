namespace Memory.Native.Windows.Enums;

internal enum WindowsPageProtection : uint
{
    PAGE_NOACCESS = 0x01,
    PAGE_READONLY = 0x02,
    PAGE_READWRITE = 0x04,
}
