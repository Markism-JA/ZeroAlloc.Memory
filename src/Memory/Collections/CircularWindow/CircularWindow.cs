using System.Runtime.CompilerServices;

namespace Memory.Collections.CircularWindow;

/// <summary>
/// Managed RAII lifecycle wrapper over <see cref="NativeCircularWindow{T}"/>.
/// Exposes a high-performance, non-blocking rewind window with per-slot monotonic sequence validation.
/// </summary>
/// <typeparam name="T">The unmanaged element type stored in the rewind window.</typeparam>
public sealed unsafe class CircularWindow<T> : IDisposable
    where T : unmanaged
{
    private NativeCircularWindow<T>* _nativeWindow;
    private int _isDisposed;

    #region State & Metrics

    /// <summary>Gets the total element capacity of the circular window.</summary>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeWindow != null ? _nativeWindow->Capacity : 0;
    }

    /// <summary>Gets the bitmask (<c>Capacity - 1</c>) used for power-of-two index wrapping.</summary>
    public int Mask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeWindow != null ? _nativeWindow->Mask : 0;
    }

    /// <summary>Gets the monotonic write cursor sequence counter.</summary>
    public long WriteCursor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeWindow != null ? _nativeWindow->WriteCursor : 0;
    }

    /// <summary>Gets the raw unmanaged pointer to the underlying slot array.</summary>
    public CircularWindowSlot<T>* SlotsPtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_isDisposed != 0)
                ThrowDisposed();

            return _nativeWindow->SlotsPtr;
        }
    }

    /// <summary>Gets the raw unmanaged pointer to the monotonic write cursor.</summary>
    public long* CursorPtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_isDisposed != 0)
                ThrowDisposed();

            return _nativeWindow->CursorPtr;
        }
    }

    #endregion

    /// <summary>Initializes a new <see cref="CircularWindow{T}"/> with at least <paramref name="capacity"/> slots (rounded up to a power of two).</summary>
    public CircularWindow(int capacity = 16384)
    {
        _nativeWindow = NativeCircularWindow<T>.Allocate(capacity);
        _isDisposed = 0;
    }

    /// <summary>
    /// Returns the raw pointer to the underlying native control block for C-ABI interop and unmanaged embedding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeCircularWindow<T>* DangerousGetNativePointer()
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeWindow;
    }

    #region Append Operations

    /// <summary>Appends a single element to the window, overwriting the oldest slot when full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(in T item)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeWindow->Append(in item);
    }

    /// <summary>Appends <paramref name="count"/> contiguous unmanaged elements to the window.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendBatch(T* items, int count)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeWindow->AppendBatch(items, count);
    }

    /// <summary>Appends a span of elements to the window.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendBatch(ReadOnlySpan<T> items)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        if (items.IsEmpty)
            return;

        fixed (T* ptr = items)
        {
            _nativeWindow->AppendBatch(ptr, items.Length);
        }
    }

    #endregion

    #region Read & Query Operations

    /// <summary>Reads up to <paramref name="maxCount"/> recent consistent elements into <paramref name="dest"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadLatest(T* dest, int maxCount)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeWindow->ReadLatest(dest, maxCount);
    }

    /// <summary>Reads up to <c>destination.Length</c> recent consistent elements into <paramref name="destination"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadLatest(Span<T> destination)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        if (destination.IsEmpty)
            return 0;

        fixed (T* destPtr = destination)
        {
            return _nativeWindow->ReadLatest(destPtr, destination.Length);
        }
    }

    /// <summary>Scans the window and copies elements matching <paramref name="predicate"/> into <paramref name="dest"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int QueryFiltered(
        T* dest,
        int maxCount,
        void* filterContext,
        delegate* <void*, T*, int> predicate
    )
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeWindow->QueryFiltered(dest, maxCount, filterContext, predicate);
    }

    /// <summary>Scans the window and copies elements matching <paramref name="predicate"/> into <paramref name="destination"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int QueryFiltered(
        Span<T> destination,
        void* filterContext,
        delegate* <void*, T*, int> predicate
    )
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        if (destination.IsEmpty)
            return 0;

        fixed (T* destPtr = destination)
        {
            return _nativeWindow->QueryFiltered(
                destPtr,
                destination.Length,
                filterContext,
                predicate
            );
        }
    }

    #endregion

    /// <summary>Releases the underlying native circular window and its backing buffer.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        NativeCircularWindow<T>* window = _nativeWindow;
        _nativeWindow = null;

        if (window != null)
        {
            NativeCircularWindow<T>.Free(window);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed()
    {
        throw new ObjectDisposedException(nameof(CircularWindow<T>));
    }
}
