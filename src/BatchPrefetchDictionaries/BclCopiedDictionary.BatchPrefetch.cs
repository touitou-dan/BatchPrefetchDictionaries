// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Dan Touitou (@touitou-dan)

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BatchPrefetchDictionaries;

/// <summary>
/// Batched + software-prefetch hash-find additions to the generic
/// <see cref="BclCopiedDictionary{TKey, TValue}"/>. Everything in this file
/// is novel to this proposal; the sibling <c>BclCopiedDictionary.cs</c> stays
/// close to the upstream MIT-licensed dotnet/runtime <c>Dictionary</c> source.
/// </summary>
public sealed partial class BclCopiedDictionary<TKey, TValue>
    where TKey : notnull
{
    public const int MaxBatchSize = 64;

    /// <summary>
    /// Creates a batched + software-prefetch hash lookup. Two specializations are
    /// supported: <c>TKey == string</c> with the BCL non-randomized string comparer
    /// that <c>new BclCopiedDictionary&lt;string,…&gt;()</c> installs, and a
    /// value-type key (e.g. <c>int</c>) using the default comparer (null, the BCL
    /// value-type fast path). Throws otherwise. See <see cref="PrefetchBatchLookup"/>
    /// for algorithm details.
    /// </summary>
    public PrefetchBatchLookup CreatePrefetchBatchLookup()
    {
        if (typeof(TKey) == typeof(string))
        {
            if (_comparer is not BclNonRandomizedStringEqualityComparer)
            {
                throw new InvalidOperationException("PrefetchBatchLookup requires the BCL non-randomized string comparer.");
            }
        }
        else if (typeof(TKey).IsValueType)
        {
            if (_comparer is not null)
            {
                throw new InvalidOperationException("PrefetchBatchLookup requires the default comparer for value-type keys.");
            }
        }
        else
        {
            throw new InvalidOperationException("PrefetchBatchLookup is specialized for TKey = string or a value-type key.");
        }

        return new PrefetchBatchLookup(this);
    }

    /// <summary>
    /// Batched + software-prefetch hash lookup. <c>FindBatch</c> dispatches by
    /// <c>typeof(TKey)</c> to one of two specializations:
    /// <c>FindStringBatch</c> (string keys — devirtualizes hash + equals straight
    /// to <c>BclNonRandomizedStringEqualityComparer.GetNonRandomizedHashCode</c> and
    /// <c>string.Equals</c>, with a 3-state lane machine Done / EntryPrefetched /
    /// KeyPrefetched so the key object is prefetched before the ordinal compare) and
    /// <c>FindValueTypeBatch</c> (value-type keys — keys live inline in the entry, so
    /// the lane machine collapses to a single chain-walk pass). Hot-loop spans use
    /// <see cref="Unsafe.Add"/> over <see cref="MemoryMarshal.GetReference"/>
    /// / <see cref="MemoryMarshal.GetArrayDataReference"/> to eliminate
    /// per-iteration bounds checks, and pass 1 saves the bucket index so pass 2 does
    /// not recompute <c>GetBucketIndex(hash, buckets.Length)</c>.
    /// PR-friendly: same buckets/entries layout, same comparer semantics
    /// (default BCL comparer), 0 hot-loop allocations, no GC pinning, no <c>nint</c>
    /// stored across safepoints.
    /// </summary>
    public sealed class PrefetchBatchLookup
    {
        private const int DoneEntryIndex = -2;
        private const byte LaneStateDone = 0;
        private const byte LaneStateEntryPrefetched = 1;
        private const byte LaneStateKeyPrefetched = 2;

        private readonly BclCopiedDictionary<TKey, TValue> _dictionary;

        internal PrefetchBatchLookup(BclCopiedDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
        }

        public unsafe int FindBatch(ReadOnlySpan<TKey> keys, Span<TValue> values, Span<bool> found)
        {
            // Dispatch to the appropriate key specialization. typeof(TKey) is a
            // JIT-time constant, so the branch is folded away and only the
            // relevant path is emitted per instantiation.
            if (typeof(TKey) == typeof(string))
            {
                return FindStringBatch(keys, values, found);
            }

            return FindValueTypeBatch(keys, values, found);
        }

        private unsafe int FindStringBatch(ReadOnlySpan<TKey> keys, Span<TValue> values, Span<bool> found)
        {
            if (keys.Length > MaxBatchSize)
            {
                throw new ArgumentException($"Batch size must be at most {MaxBatchSize}.", nameof(keys));
            }

            if (values.Length < keys.Length)
            {
                throw new ArgumentException("Value output span is too short.", nameof(values));
            }

            if (found.Length < keys.Length)
            {
                throw new ArgumentException("Found output span is too short.", nameof(found));
            }

            int[]? buckets = _dictionary._buckets;
            Entry[]? entries = _dictionary._entries;
            if (buckets == null || entries == null)
            {
                values.Slice(0, keys.Length).Clear();
                found.Slice(0, keys.Length).Clear();
                return 0;
            }

            int laneCount = keys.Length;
            int activeCount = laneCount;
            int hitCount = 0;
            ulong fastModMultiplier = _dictionary._fastModMultiplier;
            uint bucketsLength = (uint)buckets.Length;
            uint entriesLength = (uint)entries.Length;

            Span<uint> hashes = stackalloc uint[MaxBatchSize];
            Span<uint> collisionCounts = stackalloc uint[MaxBatchSize];
            Span<int> bucketIndexes = stackalloc int[MaxBatchSize];
            Span<int> entryIndexes = stackalloc int[MaxBatchSize];
            Span<byte> laneStates = stackalloc byte[MaxBatchSize];

            ref TKey keysRef = ref MemoryMarshal.GetReference(keys);
            ref TValue valuesRef = ref MemoryMarshal.GetReference(values);
            ref bool foundRef = ref MemoryMarshal.GetReference(found);
            ref uint hashesRef = ref MemoryMarshal.GetReference(hashes);
            ref uint collisionCountsRef = ref MemoryMarshal.GetReference(collisionCounts);
            ref int bucketIndexesRef = ref MemoryMarshal.GetReference(bucketIndexes);
            ref int entryIndexesRef = ref MemoryMarshal.GetReference(entryIndexes);
            ref byte laneStatesRef = ref MemoryMarshal.GetReference(laneStates);
            ref Entry entriesRef = ref MemoryMarshal.GetArrayDataReference(entries);
            ref int bucketsRef = ref MemoryMarshal.GetArrayDataReference(buckets);

            for (int lane = 0; lane < laneCount; lane++)
            {
                TKey key = Unsafe.Add(ref keysRef, lane);
                if (key == null)
                {
                    throw new ArgumentNullException(nameof(keys));
                }

                uint hashCode = (uint)BclNonRandomizedStringEqualityComparer.GetNonRandomizedHashCode(Unsafe.As<TKey, string>(ref key));
                int bucketIndex = (int)BclHashHelpers.FastMod(hashCode, bucketsLength, fastModMultiplier);

                Unsafe.Add(ref hashesRef, lane) = hashCode;
                Unsafe.Add(ref collisionCountsRef, lane) = 0;
                Unsafe.Add(ref bucketIndexesRef, lane) = bucketIndex;
                Unsafe.Add(ref entryIndexesRef, lane) = DoneEntryIndex;
                Unsafe.Add(ref laneStatesRef, lane) = LaneStateDone;
                Unsafe.Add(ref valuesRef, lane) = default!;
                Unsafe.Add(ref foundRef, lane) = false;

                Prefetch.Address((nint)Unsafe.AsPointer(ref Unsafe.Add(ref bucketsRef, (nint)(uint)bucketIndex)));
            }

            for (int lane = 0; lane < laneCount; lane++)
            {
                int bucketIndex = Unsafe.Add(ref bucketIndexesRef, lane);
                int entryIndex = Unsafe.Add(ref bucketsRef, (nint)(uint)bucketIndex) - 1;
                if (entryIndex >= 0)
                {
                    Unsafe.Add(ref entryIndexesRef, lane) = entryIndex;
                    Unsafe.Add(ref laneStatesRef, lane) = LaneStateEntryPrefetched;
                    Prefetch.Address((nint)Unsafe.AsPointer(ref Unsafe.Add(ref entriesRef, (nint)(uint)entryIndex)));
                }
                else
                {
                    activeCount--;
                }
            }

            while (activeCount > 0)
            {
                for (int lane = 0; lane < laneCount; lane++)
                {
                    byte state = Unsafe.Add(ref laneStatesRef, lane);
                    if (state == LaneStateDone)
                    {
                        continue;
                    }

                    int entryIndex = Unsafe.Add(ref entryIndexesRef, lane);
                    ref Entry entry = ref Unsafe.Add(ref entriesRef, (nint)(uint)entryIndex);

                    if (state == LaneStateEntryPrefetched)
                    {
                        if (entry.hashCode == Unsafe.Add(ref hashesRef, lane))
                        {
                            // Hash match: prefetch key object (object header → string char data line)
                            // and switch lane to KeyPrefetched. Reads entry.key reference but does
                            // not deref through it.
                            object keyObj = entry.key!;
                            Prefetch.Address(Unsafe.As<object, nint>(ref keyObj));
                            Unsafe.Add(ref laneStatesRef, lane) = LaneStateKeyPrefetched;
                            continue;
                        }

                        // Hash miss → advance collision chain.
                        uint collisions = Unsafe.Add(ref collisionCountsRef, lane) + 1;
                        Unsafe.Add(ref collisionCountsRef, lane) = collisions;
                        if (collisions > entriesLength)
                        {
                            throw new InvalidOperationException("Concurrent operations are not supported.");
                        }

                        int nextEntryIndex = entry.next;
                        if (nextEntryIndex >= 0)
                        {
                            Unsafe.Add(ref entryIndexesRef, lane) = nextEntryIndex;
                            // laneState stays EntryPrefetched
                            Prefetch.Address((nint)Unsafe.AsPointer(ref Unsafe.Add(ref entriesRef, (nint)(uint)nextEntryIndex)));
                        }
                        else
                        {
                            Unsafe.Add(ref laneStatesRef, lane) = LaneStateDone;
                            activeCount--;
                        }
                        continue;
                    }

                    // state == LaneStateKeyPrefetched
                    ref TKey entryKey = ref entry.key;
                    string left = Unsafe.As<TKey, string>(ref entryKey);
                    string right = Unsafe.As<TKey, string>(ref Unsafe.Add(ref keysRef, lane));
                    if (string.Equals(left, right))
                    {
                        TValue value = entry.value;
                        Unsafe.Add(ref valuesRef, lane) = value;
                        Unsafe.Add(ref foundRef, lane) = true;
                        Unsafe.Add(ref entryIndexesRef, lane) = DoneEntryIndex;
                        Unsafe.Add(ref laneStatesRef, lane) = LaneStateDone;
                        hitCount++;
                        activeCount--;
                        continue;
                    }

                    {
                        uint collisions = Unsafe.Add(ref collisionCountsRef, lane) + 1;
                        Unsafe.Add(ref collisionCountsRef, lane) = collisions;
                        if (collisions > entriesLength)
                        {
                            throw new InvalidOperationException("Concurrent operations are not supported.");
                        }

                        int nextEntryIndex = entry.next;
                        if (nextEntryIndex >= 0)
                        {
                            Unsafe.Add(ref entryIndexesRef, lane) = nextEntryIndex;
                            Unsafe.Add(ref laneStatesRef, lane) = LaneStateEntryPrefetched;
                            Prefetch.Address((nint)Unsafe.AsPointer(ref Unsafe.Add(ref entriesRef, (nint)(uint)nextEntryIndex)));
                        }
                        else
                        {
                            Unsafe.Add(ref laneStatesRef, lane) = LaneStateDone;
                            activeCount--;
                        }
                    }
                }
            }

            // Intentionally NO post-pass Prefetch.Object(values[i]) loop here.
            // FindBatch is kept strictly equivalent in shape to BCL TryGetValue:
            // it returns the value reference and stops. Whether (and when) to
            // prefetch the value object before the consumer reads its fields is
            // the caller's choice -- and it must be made symmetrically on the
            // baseline too, otherwise the speedup measurement gets credit for
            // work the BCL path could equally do. The benchmark driver issues
            // that prefetch in its own loop, on both baseline and candidate, so
            // the measured speedup is attributable purely to the bucket/entry
            // chase strategy (serial vs prefetched lookahead).

            return hitCount;
        }

        // Value-type-key specialization (e.g. int). Value-type keys live inline in
        // the entry, so there is no separate key heap object to prefetch — the lane
        // state machine collapses to a single "walk the chain" pass. Hash uses
        // key.GetHashCode() and comparison uses EqualityComparer<TKey>.Default
        // (the BCL value-type / null-comparer fast path; both devirtualize for a
        // value-type TKey). Same buckets/entries layout, same comparer semantics,
        // 0 hot-loop allocations. Like FindStringBatch it does NOT prefetch the
        // value object — that is the caller's symmetric responsibility.
        private unsafe int FindValueTypeBatch(ReadOnlySpan<TKey> keys, Span<TValue> values, Span<bool> found)
        {
            if (keys.Length > MaxBatchSize)
            {
                throw new ArgumentException($"Batch size must be at most {MaxBatchSize}.", nameof(keys));
            }

            if (values.Length < keys.Length)
            {
                throw new ArgumentException("Value output span is too short.", nameof(values));
            }

            if (found.Length < keys.Length)
            {
                throw new ArgumentException("Found output span is too short.", nameof(found));
            }

            int[]? buckets = _dictionary._buckets;
            Entry[]? entries = _dictionary._entries;
            if (buckets == null || entries == null)
            {
                values.Slice(0, keys.Length).Clear();
                found.Slice(0, keys.Length).Clear();
                return 0;
            }

            int laneCount = keys.Length;
            int activeCount = laneCount;
            int hitCount = 0;
            ulong fastModMultiplier = _dictionary._fastModMultiplier;
            uint bucketsLength = (uint)buckets.Length;
            uint entriesLength = (uint)entries.Length;

            Span<uint> hashes = stackalloc uint[MaxBatchSize];
            Span<uint> collisionCounts = stackalloc uint[MaxBatchSize];
            Span<int> bucketIndexes = stackalloc int[MaxBatchSize];
            Span<int> entryIndexes = stackalloc int[MaxBatchSize];

            ref TKey keysRef = ref MemoryMarshal.GetReference(keys);
            ref TValue valuesRef = ref MemoryMarshal.GetReference(values);
            ref bool foundRef = ref MemoryMarshal.GetReference(found);
            ref uint hashesRef = ref MemoryMarshal.GetReference(hashes);
            ref uint collisionCountsRef = ref MemoryMarshal.GetReference(collisionCounts);
            ref int bucketIndexesRef = ref MemoryMarshal.GetReference(bucketIndexes);
            ref int entryIndexesRef = ref MemoryMarshal.GetReference(entryIndexes);
            ref Entry entriesRef = ref MemoryMarshal.GetArrayDataReference(entries);
            ref int bucketsRef = ref MemoryMarshal.GetArrayDataReference(buckets);

            for (int lane = 0; lane < laneCount; lane++)
            {
                uint hashCode = (uint)Unsafe.Add(ref keysRef, lane).GetHashCode();
                int bucketIndex = (int)BclHashHelpers.FastMod(hashCode, bucketsLength, fastModMultiplier);

                Unsafe.Add(ref hashesRef, lane) = hashCode;
                Unsafe.Add(ref collisionCountsRef, lane) = 0;
                Unsafe.Add(ref bucketIndexesRef, lane) = bucketIndex;
                Unsafe.Add(ref entryIndexesRef, lane) = DoneEntryIndex;
                Unsafe.Add(ref valuesRef, lane) = default!;
                Unsafe.Add(ref foundRef, lane) = false;

                Prefetch.Address((nint)Unsafe.AsPointer(ref Unsafe.Add(ref bucketsRef, (nint)(uint)bucketIndex)));
            }

            for (int lane = 0; lane < laneCount; lane++)
            {
                int bucketIndex = Unsafe.Add(ref bucketIndexesRef, lane);
                int entryIndex = Unsafe.Add(ref bucketsRef, (nint)(uint)bucketIndex) - 1;
                if (entryIndex >= 0)
                {
                    Unsafe.Add(ref entryIndexesRef, lane) = entryIndex;
                    Prefetch.Address((nint)Unsafe.AsPointer(ref Unsafe.Add(ref entriesRef, (nint)(uint)entryIndex)));
                }
                else
                {
                    activeCount--;
                }
            }

            while (activeCount > 0)
            {
                for (int lane = 0; lane < laneCount; lane++)
                {
                    int entryIndex = Unsafe.Add(ref entryIndexesRef, lane);
                    if (entryIndex == DoneEntryIndex)
                    {
                        continue;
                    }

                    ref Entry entry = ref Unsafe.Add(ref entriesRef, (nint)(uint)entryIndex);
                    if (entry.hashCode == Unsafe.Add(ref hashesRef, lane)
                        && EqualityComparer<TKey>.Default.Equals(entry.key, Unsafe.Add(ref keysRef, lane)))
                    {
                        Unsafe.Add(ref valuesRef, lane) = entry.value;
                        Unsafe.Add(ref foundRef, lane) = true;
                        Unsafe.Add(ref entryIndexesRef, lane) = DoneEntryIndex;
                        hitCount++;
                        activeCount--;
                        continue;
                    }

                    // Per-lane chain-step counter — identical invariant to the BCL
                    // per-lookup check (collisionCount <= entries.Length) and to
                    // FindStringBatch above.
                    uint collisions = Unsafe.Add(ref collisionCountsRef, lane) + 1;
                    Unsafe.Add(ref collisionCountsRef, lane) = collisions;
                    if (collisions > entriesLength)
                    {
                        throw new InvalidOperationException("Concurrent operations are not supported.");
                    }

                    int nextEntryIndex = entry.next;
                    if (nextEntryIndex >= 0)
                    {
                        Unsafe.Add(ref entryIndexesRef, lane) = nextEntryIndex;
                        Prefetch.Address((nint)Unsafe.AsPointer(ref Unsafe.Add(ref entriesRef, (nint)(uint)nextEntryIndex)));
                    }
                    else
                    {
                        Unsafe.Add(ref entryIndexesRef, lane) = DoneEntryIndex;
                        activeCount--;
                    }
                }
            }

            return hitCount;
        }
    }
}
