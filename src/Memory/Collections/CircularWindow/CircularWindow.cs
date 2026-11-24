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

    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeWindow != null ? _nativeWindow->Capacity : 0;
    }

    public int Mask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeWindow != null ? _nativeWindow->Mask : 0;
    }

    public long WriteCursor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _nativeWindow != null ? _nativeWindow->WriteCursor : 0;
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(in T item)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeWindow->Append(in item);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendBatch(T* items, int count)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        _nativeWindow->AppendBatch(items, count);
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadLatest(T* dest, int maxCount)
    {
        if (_isDisposed != 0)
            ThrowDisposed();

        return _nativeWindow->ReadLatest(dest, maxCount);
    }

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
