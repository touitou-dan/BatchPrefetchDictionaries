// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Derived from dotnet/runtime; modified for this repository (internals exposed
// for benchmarking). See NOTICE.md for attribution.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BatchPrefetchDictionaries;

/// <summary>
/// Generic benchmark copy of the core dotnet/runtime Dictionary&lt;TKey,TValue&gt; hash-table
/// implementation. The scalar Add/TryGetValue path preserves the BCL generic comparer split,
/// bucket/entry layout, 1-based buckets, collision loop checks, and fast-mod bucket selection.
/// </summary>
public sealed partial class BclCopiedDictionary<TKey, TValue>
    where TKey : notnull
{
    private const int StartOfFreeList = -3;

    private int[]? _buckets;
    private Entry[]? _entries;
    private ulong _fastModMultiplier;
    private int _count;
    private int _freeList;
    private int _freeCount;
    private int _version;
    private IEqualityComparer<TKey>? _comparer;

    public BclCopiedDictionary(int capacity = 0, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (capacity > 0)
        {
            Initialize(capacity);
        }

        if (!typeof(TKey).IsValueType)
        {
            _comparer = comparer ?? EqualityComparer<TKey>.Default;

            if (typeof(TKey) == typeof(string) &&
                BclNonRandomizedStringEqualityComparer.GetStringComparer(_comparer) is IEqualityComparer<string> stringComparer)
            {
                _comparer = (IEqualityComparer<TKey>)stringComparer;
            }
        }
        else if (comparer is not null && comparer != EqualityComparer<TKey>.Default)
        {
            _comparer = comparer;
        }
    }

    public int Count => _count - _freeCount;

    public IEqualityComparer<TKey> Comparer => _comparer ?? EqualityComparer<TKey>.Default;

    public void Add(TKey key, TValue value)
    {
        TryInsert(key, value, InsertionBehavior.ThrowOnExisting);
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ref TValue valueRef = ref FindValue(key);
        if (!Unsafe.IsNullRef(ref valueRef))
        {
            value = valueRef;
            return true;
        }

        value = default;
        return false;
    }

    internal ref TValue FindValue(TKey key)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        ref Entry entry = ref Unsafe.NullRef<Entry>();
        if (_buckets != null)
        {
            Debug.Assert(_entries != null, "expected entries to be != null");
            IEqualityComparer<TKey>? comparer = _comparer;
            if (typeof(TKey).IsValueType && comparer == null)
            {
                uint hashCode = (uint)key.GetHashCode();
                int i = GetBucket(hashCode);
                Entry[]? entries = _entries;
                uint collisionCount = 0;

                i--;
                do
                {
                    if ((uint)i >= (uint)entries.Length)
                    {
                        goto ReturnNotFound;
                    }

                    entry = ref entries[i];
                    if (entry.hashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entry.key, key))
                    {
                        goto ReturnFound;
                    }

                    i = entry.next;
                    collisionCount++;
                }
                while (collisionCount <= (uint)entries.Length);

                goto ConcurrentOperation;
            }
            else
            {
                Debug.Assert(comparer is not null);
                uint hashCode = (uint)comparer.GetHashCode(key);
                int i = GetBucket(hashCode);
                Entry[]? entries = _entries;
                uint collisionCount = 0;

                i--;
                do
                {
                    if ((uint)i >= (uint)entries.Length)
                    {
                        goto ReturnNotFound;
                    }

                    entry = ref entries[i];
                    if (entry.hashCode == hashCode && comparer.Equals(entry.key, key))
                    {
                        goto ReturnFound;
                    }

                    i = entry.next;
                    collisionCount++;
                }
                while (collisionCount <= (uint)entries.Length);

                goto ConcurrentOperation;
            }
        }

        goto ReturnNotFound;

    ConcurrentOperation:
        throw new InvalidOperationException("Concurrent operations are not supported.");
    ReturnFound:
        ref TValue foundValue = ref entry.value;
    Return:
        return ref foundValue;
    ReturnNotFound:
        foundValue = ref Unsafe.NullRef<TValue>();
        goto Return;
    }

    private int Initialize(int capacity)
    {
        int size = BclHashHelpers.GetPrime(capacity);
        int[] buckets = new int[size];
        Entry[] entries = new Entry[size];

        _freeList = -1;
        _fastModMultiplier = BclHashHelpers.GetFastModMultiplier((uint)size);
        _buckets = buckets;
        _entries = entries;

        return size;
    }

    private bool TryInsert(TKey key, TValue value, InsertionBehavior behavior)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (_buckets == null)
        {
            Initialize(0);
        }

        Debug.Assert(_buckets != null);
        Entry[]? entries = _entries;
        Debug.Assert(entries != null, "expected entries to be non-null");

        IEqualityComparer<TKey>? comparer = _comparer;
        Debug.Assert(comparer is not null || typeof(TKey).IsValueType);
        uint hashCode = (uint)((typeof(TKey).IsValueType && comparer == null) ? key.GetHashCode() : comparer!.GetHashCode(key));

        uint collisionCount = 0;
        ref int bucket = ref GetBucket(hashCode);
        int i = bucket - 1;

        if (typeof(TKey).IsValueType && comparer == null)
        {
            while (true)
            {
                if ((uint)i >= (uint)entries.Length)
                {
                    break;
                }

                if (entries[i].hashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].key, key))
                {
                    if (behavior == InsertionBehavior.OverwriteExisting)
                    {
                        entries[i].value = value;
                        return true;
                    }

                    if (behavior == InsertionBehavior.ThrowOnExisting)
                    {
                        throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
                    }

                    return false;
                }

                i = entries[i].next;

                collisionCount++;
                if (collisionCount > (uint)entries.Length)
                {
                    throw new InvalidOperationException("Concurrent operations are not supported.");
                }
            }
        }
        else
        {
            Debug.Assert(comparer is not null);
            while (true)
            {
                if ((uint)i >= (uint)entries.Length)
                {
                    break;
                }

                if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                {
                    if (behavior == InsertionBehavior.OverwriteExisting)
                    {
                        entries[i].value = value;
                        return true;
                    }

                    if (behavior == InsertionBehavior.ThrowOnExisting)
                    {
                        throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
                    }

                    return false;
                }

                i = entries[i].next;

                collisionCount++;
                if (collisionCount > (uint)entries.Length)
                {
                    throw new InvalidOperationException("Concurrent operations are not supported.");
                }
            }
        }

        int index;
        if (_freeCount > 0)
        {
            index = _freeList;
            Debug.Assert((StartOfFreeList - entries[_freeList].next) >= -1, "shouldn't overflow because `next` cannot underflow");
            _freeList = StartOfFreeList - entries[_freeList].next;
            _freeCount--;
        }
        else
        {
            int count = _count;
            if (count == entries.Length)
            {
                Resize();
                bucket = ref GetBucket(hashCode);
            }

            index = count;
            _count = count + 1;
            entries = _entries;
        }

        ref Entry entry = ref entries![index];
        entry.hashCode = hashCode;
        entry.next = bucket - 1;
        entry.key = key;
        entry.value = value;
        bucket = index + 1;
        _version++;

        if (!typeof(TKey).IsValueType &&
            collisionCount > BclHashHelpers.HashCollisionThreshold &&
            _comparer is BclNonRandomizedStringEqualityComparer)
        {
            Resize(entries.Length, forceNewHashCodes: true);
        }

        return true;
    }

    private void Resize() => Resize(BclHashHelpers.ExpandPrime(_count), forceNewHashCodes: false);

    private void Resize(int newSize, bool forceNewHashCodes)
    {
        Debug.Assert(_entries != null);
        Debug.Assert(newSize >= _entries.Length);

        Entry[] entries = new Entry[newSize];
        int count = _count;
        Array.Copy(_entries, entries, count);

        if (!typeof(TKey).IsValueType && forceNewHashCodes)
        {
            Debug.Assert(_comparer is BclNonRandomizedStringEqualityComparer);
            _comparer = (IEqualityComparer<TKey>)EqualityComparer<string>.Default;

            for (int i = 0; i < count; i++)
            {
                if (entries[i].next >= -1)
                {
                    entries[i].hashCode = (uint)_comparer.GetHashCode(entries[i].key);
                }
            }
        }

        _buckets = new int[newSize];
        _fastModMultiplier = BclHashHelpers.GetFastModMultiplier((uint)newSize);
        for (int i = 0; i < count; i++)
        {
            if (entries[i].next >= -1)
            {
                ref int bucket = ref GetBucket(entries[i].hashCode);
                entries[i].next = bucket - 1;
                bucket = i + 1;
            }
        }

        _entries = entries;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int GetBucket(uint hashCode)
    {
        int[] buckets = _buckets!;
        return ref buckets[GetBucketIndex(hashCode, buckets.Length)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetBucketIndex(uint hashCode, int bucketLength)
    {
        return Environment.Is64BitProcess
            ? (int)BclHashHelpers.FastMod(hashCode, (uint)bucketLength, _fastModMultiplier)
            : (int)(hashCode % (uint)bucketLength);
    }

    private enum InsertionBehavior : byte
    {
        None = 0,
        OverwriteExisting = 1,
        ThrowOnExisting = 2,
    }

    private struct Entry
    {
        public uint hashCode;
        public int next;
        public TKey key;
        public TValue value;
    }

    private abstract class BclNonRandomizedStringEqualityComparer : IEqualityComparer<string?>
    {
        private static readonly BclNonRandomizedStringEqualityComparer WrappedAroundDefaultComparer = new OrdinalComparer(EqualityComparer<string?>.Default);
        private static readonly BclNonRandomizedStringEqualityComparer WrappedAroundStringComparerOrdinal = new OrdinalComparer(StringComparer.Ordinal);

        private readonly IEqualityComparer<string?> _underlyingComparer;

        private BclNonRandomizedStringEqualityComparer(IEqualityComparer<string?> underlyingComparer)
        {
            _underlyingComparer = underlyingComparer;
        }

        public virtual bool Equals(string? x, string? y) => string.Equals(x, y);

        public virtual int GetHashCode(string? obj) => obj is null ? 0 : GetNonRandomizedHashCode(obj);

        public virtual IEqualityComparer<string?> GetUnderlyingEqualityComparer() => _underlyingComparer;

        public static IEqualityComparer<string>? GetStringComparer(object comparer)
        {
            if (ReferenceEquals(comparer, EqualityComparer<string>.Default))
            {
                return WrappedAroundDefaultComparer;
            }

            if (ReferenceEquals(comparer, StringComparer.Ordinal))
            {
                return WrappedAroundStringComparerOrdinal;
            }

            return null;
        }

        private sealed class OrdinalComparer : BclNonRandomizedStringEqualityComparer
        {
            internal OrdinalComparer(IEqualityComparer<string?> wrappedComparer)
                : base(wrappedComparer)
            {
            }

            public override bool Equals(string? x, string? y) => string.Equals(x, y);

            public override int GetHashCode(string? obj)
            {
                Debug.Assert(obj != null);
                return GetNonRandomizedHashCode(obj!);
            }
        }

        internal static unsafe int GetNonRandomizedHashCode(string value)
        {
            fixed (char* src = value)
            {
                uint hash1 = (5381 << 16) + 5381;
                uint hash2 = hash1;

                uint* ptr = (uint*)src;
                int length = value.Length;

                while (length > 2)
                {
                    length -= 4;
                    hash1 = (BitOperations.RotateLeft(hash1, 5) + hash1) ^ ptr[0];
                    hash2 = (BitOperations.RotateLeft(hash2, 5) + hash2) ^ ptr[1];
                    ptr += 2;
                }

                if (length > 0)
                {
                    hash2 = (BitOperations.RotateLeft(hash2, 5) + hash2) ^ ptr[0];
                }

                return (int)(hash1 + (hash2 * 1566083941));
            }
        }
    }
}
