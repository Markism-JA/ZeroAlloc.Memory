using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Collections.Spsc;

/// <summary>
/// Pure unmanaged kernel implementation of a zero-allocation Single-Producer Single-Consumer (SPSC) ring buffer.
/// Allocated entirely in native heap memory with 64-byte alignment boundaries.
/// Can be passed across unmanaged C-ABI boundaries as a raw native pointer without GCHandle roots.
/// </summary>
/// <typeparam name="T">The unmanaged element type stored in the ring buffer.</typeparam>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSpscRing<T>
    where T : unmanaged
{
    private readonly int _capacity;
    private readonly int _mask;
    private readonly T* _buffer;

    private PaddedSequence _head;
    private PaddedSequence _tail;

    private PaddedSequence _cachedHead;
    private PaddedSequence _cachedTail;

    /// <summary>
    /// Gets the structural capacity of the ring buffer.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the current number of unconsumed elements in the ring buffer.
    /// Safe for concurrent observation across producer and consumer cores.
    /// </summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long head = Volatile.Read(ref _head.Value);
            long tail = Volatile.Read(ref _tail.Value);
            long count = tail - head;

            if (count < 0)
                return 0;
            if (count > _capacity)
                return _capacity;
            return (int)count;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the ring buffer contains no unconsumed elements.
    /// Evaluates in O(1) time without mutating consumer cache states or allocating pointers.
    /// </summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Acquire-read head and tail sequences directly
            long head = Volatile.Read(ref _head.Value);
            long tail = Volatile.Read(ref _tail.Value);
            return head >= tail;
        }
    }

    /// <summary>
    /// Allocates a new instance of <see cref="NativeSpscRing{T}"/> and its backing buffer on the native heap.
    /// Both the control block and memory buffer are aligned to 64-byte cache line boundaries.
    /// </summary>
    public static NativeSpscRing<T>* Allocate(int capacity = 4096)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

        int roundedCapacity = (int)BitOperation.RoundUpToPowerOf2((uint)capacity);
        int totalBytes = roundedCapacity * sizeof(T);

        T* buffer = (T*)HeapAllocator.Allocate(totalBytes, 64);
        if (buffer == null)
        {
            throw new OutOfMemoryException(
                $"[Memory Crash] Failed to allocate native buffer for {nameof(NativeSpscRing<T>)}."
            );
        }

        NativeMemoryBufferUtility.ZeroMemory(buffer, totalBytes);

        // Allocate the control block itself on a 64-byte cache boundary
        var ring = (NativeSpscRing<T>*)HeapAllocator.Allocate(sizeof(NativeSpscRing<T>), 64);

        if (ring == null)
        {
            HeapAllocator.Free(buffer);
            throw new OutOfMemoryException(
                $"[Memory Crash] Failed to allocate control block for {nameof(NativeSpscRing<T>)}."
            );
        }

        *ring = new NativeSpscRing<T>(roundedCapacity, buffer);
        return ring;
    }

    private NativeSpscRing(int capacity, T* buffer)
    {
        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = buffer;

        _head.Value = 0;
        _tail.Value = 0;
        _cachedHead.Value = 0;
        _cachedTail.Value = 0;
    }

    /// <summary>Enqueues a single item by value on the single producer thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        long currentTail = _tail.Value;
        long cachedHead = _cachedHead.Value;

        if (currentTail - cachedHead >= _capacity)
        {
            cachedHead = Volatile.Read(ref _head.Value);
            _cachedHead.Value = cachedHead;

            if (currentTail - cachedHead >= _capacity)
                return false;
        }

        _buffer[currentTail & _mask] = item;

        Volatile.Write(ref _tail.Value, currentTail + 1);
        return true;
    }

    /// <summary>Dequeues a single item on the single consumer thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        long currentHead = _head.Value;
        long cachedTail = _cachedTail.Value;

        if (currentHead >= cachedTail)
        {
            cachedTail = Volatile.Read(ref _tail.Value);
            _cachedTail.Value = cachedTail;

            if (currentHead >= cachedTail)
            {
                item = default;
                return false;
            }
        }

        item = _buffer[currentHead & _mask];

        Volatile.Write(ref _head.Value, currentHead + 1);
        return true;
    }

    /// <summary>Enqueues a span of elements using up to two contiguous block copies.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int EnqueueBatch(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty)
            return 0;

        long currentTail = _tail.Value;
        long cachedHead = _cachedHead.Value;
        int count = items.Length;

        if (currentTail + count - cachedHead > _capacity)
        {
            cachedHead = Volatile.Read(ref _head.Value);
            _cachedHead.Value = cachedHead;

            int available = _capacity - (int)(currentTail - cachedHead);
            count = Math.Min(count, available);
            if (count <= 0)
                return 0;
        }

        int tailIndex = (int)(currentTail & _mask);
        int firstChunk = Math.Min(count, _capacity - tailIndex);
        int secondChunk = count - firstChunk;

        fixed (T* srcPtr = items)
        {
            Buffer.MemoryCopy(
                srcPtr,
                _buffer + tailIndex,
                firstChunk * sizeof(T),
                firstChunk * sizeof(T)
            );

            if (secondChunk > 0)
            {
                Buffer.MemoryCopy(
                    srcPtr + firstChunk,
                    _buffer,
                    secondChunk * sizeof(T),
                    secondChunk * sizeof(T)
                );
            }
        }

        Volatile.Write(ref _tail.Value, currentTail + count);
        return count;
    }

    /// <summary>Gets a direct pointer to the next readable element without advancing the head.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetReadPointer(out T* item)
    {
        long currentHead = _head.Value;
        long cachedTail = _cachedTail.Value;

        if (currentHead >= cachedTail)
        {
            cachedTail = Volatile.Read(ref _tail.Value);
            _cachedTail.Value = cachedTail;

            if (currentHead >= cachedTail)
            {
                item = null;
                return false;
            }
        }

        item = _buffer + (currentHead & _mask);
        return true;
    }

    /// <summary>Publishes one consumed element on the consumer head.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead()
    {
        Volatile.Write(ref _head.Value, _head.Value + 1);
    }

    /// <summary>Publishes <paramref name="count"/> consumed elements on the consumer head.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead(int count)
    {
        Volatile.Write(ref _head.Value, _head.Value + count);
    }

    /// <summary>Drains available elements into <paramref name="destinationBuffer"/> using up to two block copies.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Drain(Span<T> destinationBuffer)
    {
        if (destinationBuffer.IsEmpty)
            return 0;

        long currentHead = _head.Value;
        long cachedTail = _cachedTail.Value;

        if (currentHead >= cachedTail)
        {
            cachedTail = Volatile.Read(ref _tail.Value);
            _cachedTail.Value = cachedTail;

            if (currentHead >= cachedTail)
                return 0;
        }

        int available = (int)(cachedTail - currentHead);
        int toRead = Math.Min(available, destinationBuffer.Length);

        int headIndex = (int)(currentHead & _mask);
        int firstChunk = Math.Min(toRead, _capacity - headIndex);
        int secondChunk = toRead - firstChunk;

        fixed (T* destPtr = destinationBuffer)
        {
            Buffer.MemoryCopy(
                _buffer + headIndex,
                destPtr,
                firstChunk * sizeof(T),
                firstChunk * sizeof(T)
            );

            if (secondChunk > 0)
            {
                Buffer.MemoryCopy(
                    _buffer,
                    destPtr + firstChunk,
                    secondChunk * sizeof(T),
                    secondChunk * sizeof(T)
                );
            }
        }

        Volatile.Write(ref _head.Value, currentHead + toRead);
        return toRead;
    }

    /// <summary>Gets a direct pointer to the next writable slot without advancing the tail.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetWritePointer(out T* destination)
    {
        long currentTail = _tail.Value;
        long cachedHead = _cachedHead.Value;

        if (currentTail - cachedHead >= _capacity)
        {
            cachedHead = Volatile.Read(ref _head.Value);
            _cachedHead.Value = cachedHead;

            if (currentTail - cachedHead >= _capacity)
            {
                destination = null;
                return false;
            }
        }

        destination = _buffer + (currentTail & _mask);
        return true;
    }

    /// <summary>Publishes one written element on the producer tail.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitWrite()
    {
        Volatile.Write(ref _tail.Value, _tail.Value + 1);
    }

    /// <summary>
    /// Frees the unmanaged backing buffer and the control block back to the native heap.
    /// </summary>
    public static void Free(NativeSpscRing<T>* ring)
    {
        if (ring == null)
            return;

        if (ring->_buffer != null)
        {
            HeapAllocator.Free(ring->_buffer);
        }

        HeapAllocator.Free(ring);
    }
}
