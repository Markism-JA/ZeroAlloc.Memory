using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Memory.Utilities;

public static unsafe class DebugMemoryGuard
{
    public const byte PoisonByte = 0xCC;

    /// <summary>
    /// Fills a memory buffer with the poison byte pattern (0xCC) using native .NET Standard 2.1 Spans.
    /// Zero external dependencies, completely removed in RELEASE builds.
    /// </summary>
    [Conditional("DEBUG")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PoisonBuffer(void* ptr, int byteCount)
    {
        if (ptr == null || byteCount <= 0)
            return;

        new Span<byte>(ptr, byteCount).Fill(PoisonByte);
    }

    /// <summary>
    /// Asserts that a struct was not left in its poisoned state before reading.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertNotPoisoned<T>(in T value)
        where T : unmanaged
    {
        fixed (T* ptr = &value)
        {
            byte* bytePtr = (byte*)ptr;
            for (int i = 0; i < sizeof(T); i++)
            {
                if (bytePtr[i] != PoisonByte)
                    return;
            }
        }

        Debug.Fail(
            $"[MemoryGuard] Uninitialized Read Detected! Struct of type '{typeof(T).Name}' was read without being written to."
        );
    }
}
