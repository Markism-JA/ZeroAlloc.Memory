using System.Runtime.CompilerServices;

namespace Memory.Collections.Spsc;

/// <summary>
/// A high-performance, zero-allocation Single-Producer Single-Consumer (SPSC) circular ring buffer.
/// Employs 64-byte cache-line padded sequence counters, unmanaged native memory allocations,
/// shadow counter caching to eliminate cross-core cache invalidation loops, and dual-slice batch operations.
/// <para><b>API Selection &amp; Dispatch Matrix:</b></para>
/// <list type="bullet">
///   <item>
///     <description><b>Point-to-Point Value Copy (<see cref="TryEnqueue"/>):</b> Best for streaming, event-driven pipelines where individual unmanaged structs arrive sequentially with time gaps and already exist in registers or the local stack frame.</description>
///   </item>
///   <item>
///     <description><b>Two-Phase Zero-Copy (<see cref="TryGetWritePointer"/> &amp; <see cref="CommitWrite"/>):</b> Best when constructing payloads in-place (e.g., direct socket deserialization, binary writers) to eliminate intermediate stack allocations and defensive copies.</description>
///   </item>
///   <item>
///     <description><b>Bulk Vectorized Transfer (<see cref="EnqueueBatch"/> &amp; <see cref="Drain"/>):</b> Best when transferring pre-assembled contiguous slices (e.g., tick snapshots, packet bursts, stackalloc buffers). Amortizes hardware memory barriers down to a single release per batch and maximizes throughput via 1–2 SIMD block copies.</description>
///   </item>
/// </list>
/// </summary>
/// <typeparam name="T">The unmanaged element type stored in the ring buffer.</typeparam>
public sealed unsafe class SpscRing<T> : IDisposable
    where T : unmanaged
{
    private NativeSpscRing<T>* _nativeRing;
    private int _isDisposed; // 0 = active, 1 = disposed

    /// <summary>
    /// Gets the total structural capacity of the ring buffer in elements.
    /// </summary>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing != null ? _nativeRing->Capacity : 0;
    }

    /// <summary>
    /// Gets a value indicating whether the ring buffer contains no unconsumed elements.
    /// Returns true if uninitialized or disposed.
    /// </summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeRing == null || _nativeRing->IsEmpty;
    }

    /// <summary>
    /// Returns the raw pointer to the underlying native control block.
    /// Enables zero-trampoline unmanaged C-ABI sink dispatch without GCHandle.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeSpscRing<T>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
        {
            ThrowDisposed();
        }

        return _nativeRing;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpscRing{T}"/> class with the specified element capacity.
    /// </summary>
    /// <param name="capacity">The desired initial capacity in elements. Rounded up to the nearest power of 2.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="capacity"/> is less than or equal to zero.</exception>
    /// <exception cref="OutOfMemoryException">Thrown if the underlying native heap allocation fails.</exception>
    public SpscRing(int capacity = 4096)
    {
        _nativeRing = NativeSpscRing<T>.Allocate(capacity);
        _isDisposed = 0;
    }

    /// <summary>
    /// Concurrently enqueues a single item by value onto the tail of the ring buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        return _nativeRing->TryEnqueue(in item);
    }

    /// <summary>
    /// Concurrently dequeues a single item from the head of the ring buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        return _nativeRing->TryDequeue(out item);
    }

    /// <summary>
    /// Ingests a batch of elements sequentially into the ring buffer using vectorized block copies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int EnqueueBatch(ReadOnlySpan<T> items)
    {
        return _nativeRing->EnqueueBatch(items);
    }

    /// <summary>
    /// Acquires an unmanaged pointer to the next readable slot at the head of the ring buffer without advancing the consumer counter.
    /// Enables zero-copy read operations. Must be paired with <see cref="CommitRead()"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetReadPointer(out T* item)
    {
        return _nativeRing->TryGetReadPointer(out item);
    }

    /// <summary>
    /// Advances the consumer head sequence counter by 1, releasing the slot back to the producer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead()
    {
        _nativeRing->CommitRead();
    }

    /// <summary>
    /// Advances the consumer head sequence counter by the specified number of processed slots.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead(int count)
    {
        _nativeRing->CommitRead(count);
    }

    /// <summary>
    /// Drains available elements from the ring buffer into the specified destination span in bulk.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Drain(Span<T> destinationBuffer)
    {
        return _nativeRing->Drain(destinationBuffer);
    }

    /// <summary>
    /// Reserves a zero-copy pointer to the next writable slot at the tail of the buffer without publishing it.
    /// Must be paired with a subsequent call to <see cref="CommitWrite"/> once data initialization is complete.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetWritePointer(out T* destination)
    {
        return _nativeRing->TryGetWritePointer(out destination);
    }

    /// <summary>
    /// Publishes the previously reserved write slot, advancing the producer sequence counter with a release memory barrier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitWrite()
    {
        _nativeRing->CommitWrite();
    }

    /// <summary>
    /// Releases all unmanaged native memory back to the system heap and invalidates the ring buffer.
    /// Safe to invoke multiple times.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        NativeSpscRing<T>* ring = _nativeRing;
        _nativeRing = null;

        if (ring != null)
        {
            NativeSpscRing<T>.Free(ring);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(SpscRing<T>));
    }
}
