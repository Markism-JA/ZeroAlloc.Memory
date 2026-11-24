using System.Runtime.CompilerServices;

namespace Memory.Collections.BoundedMpsc;

/// <summary>
/// Defines a generalized, zero-allocation sink for consuming fixed-size unmanaged values by reference.
/// Monomorphized when used as a generic struct constraint on batch drain loops.
/// </summary>
/// <typeparam name="T">The unmanaged payload type.</typeparam>
public interface IValueConsumer<T>
    where T : unmanaged
{
    /// <summary>
    /// Consumes a single unmanaged value.
    /// </summary>
    /// <param name="value">Read-only reference to the payload slot.</param>
    /// <returns><c>true</c> to continue draining; <c>false</c> to halt consumer iteration (backpressure).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Consume(in T value);
}
