using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Collections.VirtualMap;

/// <summary>
/// Pure unmanaged open-addressing hash table backed by reserved virtual address space.
/// Lazily commits physical OS page frames on-demand without relocating the base pointer.
/// Enforces a strict 50% load factor threshold and Robin Hood backward-shift deletions.
/// </summary>
/// <typeparam name="TKey">The unmanaged key type implementing <see cref="IEquatable{TKey}"/>.</typeparam>
/// <typeparam name="TValue">The unmanaged value type stored in table entries.</typeparam>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct NativeVirtualMap<TKey, TValue>
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : unmanaged
{
    /// <summary>Default minimum key capacity (2,048 keys).</summary>
    public const int DefaultMinKeyCapacity = 2048;

    /// <summary>Default minimum slot capacity (4,096 slots for 50% load factor headroom).</summary>
    public const int DefaultMinSlotCapacity = DefaultMinKeyCapacity * 2; // 4,096 slots (50% load factor headroom)

    /// <summary>Absolute maximum reserved slot count (16,777,216 slots).</summary>
    public const int AbsoluteMaxSlotCount = 1 << 24; // 16,777,216 slots (~1GB virtual limit)
    private const int _cacheLineAlignment = 64;

    private readonly int _maxSlotCapacity;
    private readonly int _totalReservationBytes;
    private readonly VirtualMapEntry<TKey, TValue>* _slots;

    private int _capacity;
    private int _mask;
    private int _committedBytes;
    private int _count;

    #region State & Metrics

    /// <summary>
    /// Gets the current number of active key-value pairs stored in the table.
    /// </summary>
    public readonly int Count => _count;

    /// <summary>
    /// Gets the current active structural capacity in slots (constrained to the active power-of-two hash mask window).
    /// </summary>
    public readonly int Capacity => _capacity;

    /// <summary>
    /// Gets the maximum active key threshold before dynamic growth is triggered (50% of slot capacity).
    /// </summary>
    public readonly int KeyCapacity => _capacity >> 1;

    /// <summary>
    /// Gets the structural virtual limit in slots for this instance.
    /// </summary>
    public readonly int MaxSlotCapacity => _maxSlotCapacity;

    /// <summary>
    /// Gets the total volume of physical RAM currently committed by the operating system in bytes.
    /// </summary>
    public readonly int CommittedBytes => _committedBytes;

    /// <summary>
    /// Gets whether the map currently stores zero entries.
    /// </summary>
    public readonly bool IsEmpty => _count == 0;

    /// <summary>
    /// Gets whether the map has reached its structural capacity limit under the 50% load factor.
    /// </summary>
    public readonly bool IsFull => _capacity >= _maxSlotCapacity && _count >= (_capacity >> 1);

    /// <summary>
    /// Gets the direct pointer to the backing virtual slot array.
    /// </summary>
    public readonly VirtualMapEntry<TKey, TValue>* SlotsPtr => _slots;

    #endregion

    #region Allocation & Teardown

    /// <summary>
    /// Allocates an unmanaged <see cref="NativeVirtualMap{TKey, TValue}"/> control block and reserves virtual address space.
    /// </summary>
    /// <param name="initialKeyCapacity">The desired active key capacity before triggering growth. Scaled to a power of 2 with 50% slot headroom.</param>
    public static NativeVirtualMap<TKey, TValue>* Allocate(
        int initialKeyCapacity = DefaultMinKeyCapacity
    )
    {
        if (initialKeyCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialKeyCapacity),
                "Initial key capacity must be strictly positive."
            );
        }

        // 1. Calculate safe MaxSlotCapacity clamped against 32-bit signed allocation boundaries
        long entrySize = sizeof(VirtualMapEntry<TKey, TValue>);
        long maxSafeSlots = int.MaxValue / entrySize;
        int maxSlotCapacity = (int)
            Math.Min(AbsoluteMaxSlotCount, BitOperation.RoundDownToPowerOf2((ulong)maxSafeSlots));

        // 2. Enforce 50% load factor: calculate required physical slots (2x requested key capacity)
        ulong rawDoubled = (ulong)initialKeyCapacity * 2UL;
        ulong roundedSlots = BitOperation.RoundUpToPowerOf2(rawDoubled);
        int targetSlotCapacity = (int)Math.Max(DefaultMinSlotCapacity, roundedSlots);
        targetSlotCapacity = Math.Min(targetSlotCapacity, maxSlotCapacity);

        int totalReservationBytes = (int)(maxSlotCapacity * entrySize);

        // 3. Reserve virtual address space (0 physical RAM committed)
        var slots = (VirtualMapEntry<TKey, TValue>*)
            PageAllocator.ReserveAddressSpace(totalReservationBytes);

        if (slots == null)
        {
            throw new OutOfMemoryException(
                $"[Kernel Fault] Failed to reserve {totalReservationBytes:N0} bytes of virtual address space."
            );
        }

        // 4. Commit initial physical page range
        int initialBytesNeeded = (int)(targetSlotCapacity * entrySize);
        int pageSizeMask = PageAllocator.SystemPageSize - 1;
        int alignedInitialCommit = (initialBytesNeeded + pageSizeMask) & ~pageSizeMask;

        if (!PageAllocator.CommitVirtualRange(slots, alignedInitialCommit))
        {
            PageAllocator.ReleaseVirtual(slots, totalReservationBytes);
            throw new OutOfMemoryException(
                "[Kernel Fault] Failed to commit initial physical page range."
            );
        }

        NativeMemoryBufferUtility.ZeroMemory(slots, alignedInitialCommit);

        // 5. Allocate control block on the native heap
        var map = (NativeVirtualMap<TKey, TValue>*)
            HeapAllocator.Allocate(sizeof(NativeVirtualMap<TKey, TValue>), _cacheLineAlignment);

        if (map == null)
        {
            PageAllocator.ReleaseVirtual(slots, totalReservationBytes);
            throw new OutOfMemoryException(
                "[Native Crash] Failed to allocate control block for NativeVirtualMap."
            );
        }

        *map = new NativeVirtualMap<TKey, TValue>(
            maxSlotCapacity,
            totalReservationBytes,
            targetSlotCapacity,
            alignedInitialCommit,
            slots
        );

        return map;
    }

    private NativeVirtualMap(
        int maxSlotCapacity,
        int totalReservationBytes,
        int capacity,
        int committedBytes,
        VirtualMapEntry<TKey, TValue>* slots
    )
    {
        _maxSlotCapacity = maxSlotCapacity;
        _totalReservationBytes = totalReservationBytes;
        _capacity = capacity;
        _mask = capacity - 1;
        _committedBytes = committedBytes;
        _count = 0;
        _slots = slots;
    }

    /// <summary>
    /// Releases all reserved virtual address space back to the OS kernel and frees the control block on the native heap.
    /// </summary>
    public static void Free(NativeVirtualMap<TKey, TValue>* map)
    {
        if (map == null)
            return;

        if (map->_slots != null)
        {
            PageAllocator.ReleaseVirtual(map->_slots, map->_totalReservationBytes);
        }

        HeapAllocator.Free(map);
    }

    #endregion

    #region Fast Lookups & Insertions

    /// <summary>
    /// Attempts to retrieve the value associated with the specified key.
    /// Probes terminate immediately on the first empty slot (<c>State == 0</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGetValue(in TKey key, out TValue value)
    {
        if (_count == 0)
        {
            value = default;
            return false;
        }

        uint hash = HashKey(in key);
        int mask = _mask;
        int slot = (int)(hash & (uint)mask);

        while (_slots[slot].State != 0)
        {
            if (_slots[slot].Key.Equals(key))
            {
                value = _slots[slot].Value;
                return true;
            }
            slot = (slot + 1) & mask;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Determines whether the table contains an entry with the specified key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ContainsKey(in TKey key)
    {
        if (_count == 0)
            return false;

        FindSlot(in key, out bool exists);
        return exists;
    }

    /// <summary>
    /// Inserts a key-value pair into the table if the key does not already exist.
    /// Expands the active slot window if the 50% load threshold is crossed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(in TKey key, in TValue value)
    {
        if (_count >= (_capacity >> 1))
        {
            if (!TryGrowCapacity())
                return false;
        }

        int slot = FindSlot(in key, out bool exists);
        if (exists)
            return false;

        _count++;
        _slots[slot].Key = key;
        _slots[slot].Value = value;
        _slots[slot].State = 1;
        return true;
    }

    /// <summary>
    /// Returns a direct unmanaged <see langword="ref"/> to the value associated with the key, or inserts a default entry in place if missing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue GetValueRefOrAddDefault(in TKey key, out bool exists, out bool success)
    {
        if (_count >= (_capacity >> 1))
        {
            if (!TryGrowCapacity())
            {
                int existingSlot = FindSlot(in key, out exists);
                if (exists)
                {
                    success = true;
                    return ref _slots[existingSlot].Value;
                }

                success = false;
                return ref *(TValue*)null;
            }
        }

        int slot = FindSlot(in key, out exists);
        if (exists)
        {
            success = true;
            return ref _slots[slot].Value;
        }

        _count++;
        _slots[slot].Key = key;
        _slots[slot].State = 1;
        _slots[slot].Value = default;
        success = true;
        return ref _slots[slot].Value;
    }

    #endregion

    #region Backward-Shift Deletion (Robin Hood / Knuth)

    /// <summary>
    /// Removes an entry by key using circular backward-shift deletion.
    /// Heals probe chains immediately without creating tombstone markers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(in TKey key)
    {
        if (_count == 0)
            return false;

        int slot = FindSlot(in key, out bool exists);
        if (!exists)
            return false;

        _count--;

        int mask = _mask;
        int i = slot;

        while (true)
        {
            _slots[i].State = 0;
            _slots[i].Key = default;
            _slots[i].Value = default;

            int j = i;

            while (true)
            {
                j = (j + 1) & mask;
                if (_slots[j].State == 0)
                    return true;

                int r = (int)(HashKey(in _slots[j].Key) & (uint)mask);

                // Circular distance check: entry at j can shift to i if i lies on the probe path from r to j
                if (((i - r) & mask) < ((j - r) & mask))
                    break;
            }

            _slots[i] = _slots[j];
            i = j;
        }
    }

    #endregion

    #region Capacity Growth & Physical Page Commitment

    private bool TryGrowCapacity()
    {
        if (_capacity >= _maxSlotCapacity)
            return false;

        int oldCapacity = _capacity;
        int newCapacity = oldCapacity * 2;
        long requiredBytesLong = (long)newCapacity * sizeof(VirtualMapEntry<TKey, TValue>);

        if (requiredBytesLong > int.MaxValue)
            return false;

        int requiredBytes = (int)requiredBytesLong;

        // Demand-commit newly expanded page frames from OS kernel
        if (requiredBytes > _committedBytes)
        {
            int pageSizeMask = PageAllocator.SystemPageSize - 1;
            int bytesToCommit = ((requiredBytes - _committedBytes) + pageSizeMask) & ~pageSizeMask;

            if (!PageAllocator.CommitVirtualRange((byte*)_slots + _committedBytes, bytesToCommit))
            {
                return false;
            }

            _committedBytes += bytesToCommit;
        }

        // Allocate temporary scratch space on C-heap to preserve entries during rehash
        int oldBytes = oldCapacity * sizeof(VirtualMapEntry<TKey, TValue>);
        var oldEntries = (VirtualMapEntry<TKey, TValue>*)HeapAllocator.Allocate(oldBytes, 64);
        if (oldEntries == null)
        {
            return false;
        }

        Buffer.MemoryCopy(_slots, oldEntries, oldBytes, oldBytes);
        NativeMemoryBufferUtility.ZeroMemory(_slots, requiredBytes);

        _capacity = newCapacity;
        _mask = newCapacity - 1;

        // Rehash into a newly expanded capacity window
        for (int i = 0; i < oldCapacity; i++)
        {
            if (oldEntries[i].State == 1)
            {
                ref readonly var entry = ref oldEntries[i];
                int newSlot = (int)(HashKey(in entry.Key) & (uint)_mask);

                while (_slots[newSlot].State != 0)
                {
                    newSlot = (newSlot + 1) & _mask;
                }

                _slots[newSlot] = entry;
            }
        }

        HeapAllocator.Free(oldEntries);
        return true;
    }

    #endregion

    #region Maintenance & OS Memory Reclamation

    /// <summary>
    /// Zeroes all active entries and resets the count without modifying capacity or decommitting physical pages.
    /// </summary>
    public void Clear()
    {
        if (_count == 0)
            return;

        int activeBytes = _capacity * sizeof(VirtualMapEntry<TKey, TValue>);
        NativeMemoryBufferUtility.ZeroMemory(_slots, activeBytes);
        _count = 0;
    }

    /// <summary>
    /// Decommits excess physical RAM back to the operating system, shrinking physical commitments
    /// and active slot capacity back down to the baseline minimum (<see cref="DefaultMinSlotCapacity"/>).
    /// </summary>
    public void PassiveDecommitReclaim()
    {
        Clear();

        int baselineBytes = DefaultMinSlotCapacity * sizeof(VirtualMapEntry<TKey, TValue>);
        int pageSizeMask = PageAllocator.SystemPageSize - 1;
        int alignedBaseline = (baselineBytes + pageSizeMask) & ~pageSizeMask;

        if (_committedBytes > alignedBaseline)
        {
            int bytesToDecommit = _committedBytes - alignedBaseline;
            PageAllocator.Decommit((byte*)_slots + alignedBaseline, bytesToDecommit);
            _committedBytes = alignedBaseline;
        }

        _capacity = DefaultMinSlotCapacity;
        _mask = DefaultMinSlotCapacity - 1;
    }

    #endregion

    #region Internal Probing & Hashing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly int FindSlot(in TKey key, out bool exists)
    {
        uint hash = HashKey(in key);
        int mask = _mask;
        int slot = (int)(hash & (uint)mask);

        while (_slots[slot].State != 0)
        {
            if (_slots[slot].Key.Equals(key))
            {
                exists = true;
                return slot;
            }
            slot = (slot + 1) & mask;
        }

        exists = false;
        return slot;
    }

    /// <summary>Computes a SplitMix64-mixed 32-bit hash for an unmanaged <paramref name="key"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashKey(in TKey key)
    {
        ulong k;
        int size = sizeof(TKey);

        fixed (TKey* ptr = &key)
        {
            if (size == 8)
            {
                k = *(ulong*)ptr;
            }
            else if (size == 4)
            {
                k = *(uint*)ptr;
            }
            else if (size == 2)
            {
                k = *(ushort*)ptr;
            }
            else if (size == 1)
            {
                k = *(byte*)ptr;
            }
            else
            {
                byte* p = (byte*)ptr;
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < size; i++)
                {
                    hash ^= p[i];
                    hash *= 1099511628211UL;
                }
                k = hash;
            }
        }

        // High-entropy 64-bit SplitMix64 finalizer mixer
        k ^= k >> 30;
        k *= 0xBF58476D1CE4E5B9UL;
        k ^= k >> 27;
        k *= 0x94D049BB133111EBUL;
        k ^= k >> 31;
        return (uint)k;
    }

    #endregion
}
