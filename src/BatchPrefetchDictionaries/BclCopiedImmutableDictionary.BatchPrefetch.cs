// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Dan Touitou (@touitou-dan)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BatchPrefetchDictionaries;

/// <summary>
/// Batched + software-prefetch additions to
/// <see cref="BclCopiedImmutableDictionary{TKey, TValue}"/> covering both
/// hash-find (<see cref="BclCopiedImmutableDictionary{TKey, TValue}.BatchLookup"/>)
/// and pair enumeration
/// (<see cref="BclCopiedImmutableDictionary{TKey, TValue}.PairBatchEnumerator"/>).
/// Everything in this file is novel to this proposal; the sibling
/// <c>BclCopiedImmutableDictionary.cs</c> stays close to the upstream
/// MIT-licensed dotnet/runtime <c>ImmutableDictionary</c> source.
/// </summary>
public sealed partial class BclCopiedImmutableDictionary<TKey, TValue>
    where TKey : notnull
{
    public const int MaxBatchSize = 32;
    private const int MaxEnumerationStackDepth = 64;
    private const int MaxEnumerationSplitNodes = MaxBatchSize * 2;
    public BatchLookup CreateBatchLookup()
    {
        return new BatchLookup(this);
    }

    public PairBatchEnumerator CreatePairBatchEnumerator(int vectorSize = MaxBatchSize)
    {
        return new PairBatchEnumerator(this, vectorSize);
    }

    /// <summary>
    /// foreach-shaped pair enumeration with the same lane + software-prefetch
    /// engine as <see cref="PairBatchEnumerator"/>, delivered one
    /// <see cref="KeyValuePair{TKey,TValue}"/> at a time. Buffers a burst of
    /// <paramref name="window"/> pairs (each issuing its prefetch) and drains them
    /// one per <c>MoveNext</c>, so the prefetch still runs ahead of the read.
    /// </summary>
    public PrefetchedPairEnumerable PrefetchedPairs(int window)
    {
        return new PrefetchedPairEnumerable(this, window);
    }

    [InlineArray(MaxBatchSize)]
    private struct PairRingBuffer
    {
        private KeyValuePair<TKey, TValue> _element0;
    }

    public readonly ref struct PrefetchedPairEnumerable
    {
        private readonly BclCopiedImmutableDictionary<TKey, TValue> _dictionary;
        private readonly int _window;

        internal PrefetchedPairEnumerable(BclCopiedImmutableDictionary<TKey, TValue> dictionary, int window)
        {
            _dictionary = dictionary;
            _window = window;
        }

        public PrefetchedPairEnumerator GetEnumerator() => new(_dictionary, _window);
    }

    public ref struct PrefetchedPairEnumerator
    {
        private PairBatchEnumerator _engine;
        private readonly int _window;
        private PairRingBuffer _buffer;
        private KeyValuePair<TKey, TValue> _current;
        private int _index;
        private int _filled;

        internal PrefetchedPairEnumerator(BclCopiedImmutableDictionary<TKey, TValue> dictionary, int window)
        {
            if ((uint)(window - 1) >= MaxBatchSize)
            {
                throw new ArgumentOutOfRangeException(nameof(window));
            }

            _engine = dictionary.CreatePairBatchEnumerator(window);
            _window = window;
            _buffer = default;
            _current = default;
            _index = 0;
            _filled = 0;
        }

        public readonly KeyValuePair<TKey, TValue> Current => _current;

        public bool MoveNext()
        {
            if (_index == _filled)
            {
                _filled = 0;
                for (int i = 0; i < _window; i++)
                {
                    if (!_engine.TryNextPair(out KeyValuePair<TKey, TValue> p))
                    {
                        break;
                    }

                    _buffer[i] = p;
                    _filled++;
                }

                _index = 0;
                if (_filled == 0)
                {
                    _current = default;
                    return false;
                }
            }

            _current = _buffer[_index++];
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void PrefetchTreeNode(SortedInt32KeyNode<HashBucket> node)
    {
        // Prefetch the object base directly. Reinterprets the reference as an
        // integer via Unsafe.As<T,nint> WITHOUT dereferencing any field, so the
        // cold node is never demand-loaded onto the critical path. Reading a
        // field offset instead would require a generic-type static (compiled to a
        // per-element CORINFO_HELP_GET_NONGCSTATIC_BASE call under shared generics)
        // and a null-check load. The node is small enough that its hot fields
        // share the base cache line.
        nint addr = Unsafe.As<SortedInt32KeyNode<HashBucket>, nint>(ref node);
        Prefetch.Address(addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void PrefetchListNode(BclImmutableListNode<KeyValuePair<TKey, TValue>> node)
    {
        nint addr = Unsafe.As<BclImmutableListNode<KeyValuePair<TKey, TValue>>, nint>(ref node);
        Prefetch.Address(addr);
    }

    [InlineArray(32)]
    private struct BatchNodeBuffer
    {
        private SortedInt32KeyNode<HashBucket>? _element0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref SortedInt32KeyNode<HashBucket>? BatchNodeSlot(ref BatchNodeBuffer nodes, int index)
    {
        Debug.Assert((uint)index < 32);
        ref SortedInt32KeyNode<HashBucket>? first = ref Unsafe.As<BatchNodeBuffer, SortedInt32KeyNode<HashBucket>?>(ref nodes);
        return ref Unsafe.Add(ref first, index);
    }

    private enum BatchLookupLaneState : byte
    {
        Done,
        NodePrefetched,
        KeyPrefetched,
    }

    [InlineArray(MaxEnumerationSplitNodes)]
    private struct EnumerationNodeBuffer
    {
        private SortedInt32KeyNode<HashBucket>? _element0;
    }

    [InlineArray(MaxEnumerationStackDepth)]
    private struct EnumerationTreeStack
    {
        private SortedInt32KeyNode<HashBucket>? _element0;
    }

    [InlineArray(MaxEnumerationStackDepth)]
    private struct EnumerationCollisionStack
    {
        private BclImmutableListNode<KeyValuePair<TKey, TValue>>? _element0;
    }

    [InlineArray(MaxBatchSize)]
    private struct PairEnumerationLaneBuffer
    {
        private PairEnumerationLane _element0;
    }

    private enum PairLaneStep : byte
    {
        Returned,
        Prepared,
        Exhausted,
    }

    private struct PairEnumerationLane
    {
        public EnumerationTreeStack TreeStack;
        public EnumerationCollisionStack CollisionStack;
        public int TreeTop;
        public int CollisionTop;
    }

    public ref struct PairBatchEnumerator
    {
        private readonly int _vectorSize;
        private readonly SortedInt32KeyNode<HashBucket> _emptyTreeNode;
        private readonly BclImmutableListNode<KeyValuePair<TKey, TValue>> _emptyListNode;
        private PairEnumerationLaneBuffer _lanes;
        private EnumerationNodeBuffer _singleNodes;
        private EnumerationCollisionStack _singleCollisionStack;
        private int _laneCount;
        private int _lane;
        private int _singleCount;
        private int _singleIndex;
        private int _singleCollisionTop;

        internal PairBatchEnumerator(
            BclCopiedImmutableDictionary<TKey, TValue> dictionary,
            int vectorSize)
        {
            if (vectorSize <= 0 || vectorSize > MaxBatchSize)
            {
                throw new ArgumentOutOfRangeException(nameof(vectorSize), $"Vector size must be between 1 and {MaxBatchSize}.");
            }

            // Cache the EmptyNode sentinels once. They are statics of a generic
            // type, so reading them in the per-element empty-checks would compile
            // to a CORINFO_HELP_GET_GCSTATIC_BASE call each time in shared generic
            // codegen. Reading them once here turns those into plain field loads.
            _emptyTreeNode = SortedInt32KeyNode<HashBucket>.EmptyNode;
            _emptyListNode = BclImmutableListNode<KeyValuePair<TKey, TValue>>.EmptyNode;

            _vectorSize = vectorSize;
            Initialize(dictionary._root);
        }

        // Single-step engine access for the burst-refill foreach enumerator.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryNextPair(out KeyValuePair<TKey, TValue> pair)
        {
            if (MoveNext(out TKey key, out TValue value))
            {
                pair = new KeyValuePair<TKey, TValue>(key, value);
                return true;
            }

            pair = default;
            return false;
        }

        private bool MoveNext(out TKey key, out TValue value)
        {
            if (_singleCollisionTop > 0)
            {
                BclImmutableListNode<KeyValuePair<TKey, TValue>> node = _singleCollisionStack[--_singleCollisionTop]!;
                PushSingleCollision(node.Left);
                PushSingleCollision(node.Right);
                KeyValuePair<TKey, TValue> pair = node.Key;
                PrefetchKeyValue(pair.Key, pair.Value);
                key = pair.Key;
                value = pair.Value;
                return true;
            }

            if (_singleIndex < _singleCount)
            {
                SortedInt32KeyNode<HashBucket> node = _singleNodes[_singleIndex++]!;
                HashBucket bucket = node.ValueOrDefault;
                PushSingleCollision(bucket.AdditionalElements);
                KeyValuePair<TKey, TValue> pair = bucket.FirstValue;
                PrefetchKeyValue(pair.Key, pair.Value);
                key = pair.Key;
                value = pair.Value;
                return true;
            }

            while (_laneCount > 0)
            {
                if (_lane >= _laneCount)
                {
                    _lane = 0;
                }

                if (MoveNextFromLane(ref _lanes[_lane], out key, out value))
                {
                    _lane++;
                    return true;
                }

                _laneCount--;
                if (_lane < _laneCount)
                {
                    _lanes[_lane] = _lanes[_laneCount];
                }
            }

            key = default!;
            value = default!;
            return false;
        }

        private void Initialize(SortedInt32KeyNode<HashBucket> root)
        {
            if (ReferenceEquals(root, SortedInt32KeyNode<HashBucket>.EmptyNode))
            {
                return;
            }

            PrefetchTreeNode(root);

            EnumerationNodeBuffer frontier = default;
            int frontierCount = 1;
            frontier[0] = root;

            while (frontierCount < _vectorSize && _singleCount < MaxEnumerationSplitNodes)
            {
                int splitIndex = -1;
                for (int i = 0; i < frontierCount; i++)
                {
                    SortedInt32KeyNode<HashBucket> node = frontier[i]!;
                    SortedInt32KeyNode<HashBucket>? scanLeft = node.Left;
                    SortedInt32KeyNode<HashBucket>? scanRight = node.Right;
                    if (IsNonEmptyTreeReference(scanLeft))
                    {
                        PrefetchTreeNode(scanLeft!);
                    }

                    if (IsNonEmptyTreeReference(scanRight))
                    {
                        PrefetchTreeNode(scanRight!);
                    }

                    if (IsNonEmptyTreeReference(scanLeft) || IsNonEmptyTreeReference(scanRight))
                    {
                        splitIndex = i;
                        break;
                    }
                }

                if (splitIndex < 0)
                {
                    break;
                }

                SortedInt32KeyNode<HashBucket> splitNode = frontier[splitIndex]!;
                frontier[splitIndex] = frontier[--frontierCount];
                _singleNodes[_singleCount++] = splitNode;

                SortedInt32KeyNode<HashBucket>? left = splitNode.Left;
                if (IsNonEmptyTreeReference(left))
                {
                    PrefetchTreeNode(left!);
                    frontier[frontierCount++] = left!;
                }

                SortedInt32KeyNode<HashBucket>? right = splitNode.Right;
                if (IsNonEmptyTreeReference(right))
                {
                    PrefetchTreeNode(right!);
                    frontier[frontierCount++] = right!;
                }
            }

            _laneCount = frontierCount;
            for (int i = 0; i < _laneCount; i++)
            {
                PushTree(ref _lanes[i], frontier[i]!);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MoveNextFromLane(
            ref PairEnumerationLane lane,
            out TKey key,
            out TValue value)
        {
            if (lane.CollisionTop > 0)
            {
                BclImmutableListNode<KeyValuePair<TKey, TValue>> node = lane.CollisionStack[--lane.CollisionTop]!;
                PushCollision(ref lane, node.Left);
                PushCollision(ref lane, node.Right);
                KeyValuePair<TKey, TValue> pair = node.Key;
                PrefetchKeyValue(pair.Key, pair.Value);
                key = pair.Key;
                value = pair.Value;
                return true;
            }

            if (lane.TreeTop == 0)
            {
                key = default!;
                value = default!;
                return false;
            }

            SortedInt32KeyNode<HashBucket> treeNode = lane.TreeStack[--lane.TreeTop]!;
            PushTreeNoPrefetch(ref lane, treeNode.Left);
            PushTreeNoPrefetch(ref lane, treeNode.Right);

            // Top-of-stack prefetch policy: both children were just pushed cold
            // (PushTreeNoPrefetch above); prefetch only the new top of stack — the
            // node that will be popped next.
            if (lane.TreeTop > 0)
            {
                PrefetchTreeNode(lane.TreeStack[lane.TreeTop - 1]!);
            }

            HashBucket bucket = treeNode.ValueOrDefault;
            PushCollision(ref lane, bucket.AdditionalElements);
            KeyValuePair<TKey, TValue> firstPair = bucket.FirstValue;
            PrefetchKeyValue(firstPair.Key, firstPair.Value);
            key = firstPair.Key;
            value = firstPair.Value;
            return true;
        }

        private void PushSingleCollision(BclImmutableListNode<KeyValuePair<TKey, TValue>>? node)
        {
            if (!IsNonEmptyCollision(node))
            {
                return;
            }

            PrefetchListNode(node!);
            _singleCollisionStack[_singleCollisionTop++] = node!;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushTree(
            ref PairEnumerationLane lane,
            SortedInt32KeyNode<HashBucket>? node)
        {
            if (!IsNonEmptyTree(node))
            {
                return;
            }

            PrefetchTreeNode(node!);
            lane.TreeStack[lane.TreeTop++] = node!;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushTreeNoPrefetch(
            ref PairEnumerationLane lane,
            SortedInt32KeyNode<HashBucket>? node)
        {
            if (!IsNonEmptyTree(node))
            {
                return;
            }

            lane.TreeStack[lane.TreeTop++] = node!;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushCollision(
            ref PairEnumerationLane lane,
            BclImmutableListNode<KeyValuePair<TKey, TValue>>? node)
        {
            if (!IsNonEmptyCollision(node))
            {
                return;
            }

            PrefetchListNode(node!);
            lane.CollisionStack[lane.CollisionTop++] = node!;
        }

        // Empty-checks comparing against the cached sentinels (no per-element
        // generic-static read). Used on the hot enumeration path.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly bool IsNonEmptyTree(SortedInt32KeyNode<HashBucket>? node)
            => node is not null && !ReferenceEquals(node, _emptyTreeNode);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly bool IsNonEmptyCollision(BclImmutableListNode<KeyValuePair<TKey, TValue>>? node)
            => node is not null && !ReferenceEquals(node, _emptyListNode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNonEmptyTreeReference(SortedInt32KeyNode<HashBucket>? node)
    {
        return node is not null && !ReferenceEquals(node, SortedInt32KeyNode<HashBucket>.EmptyNode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNonEmptyCollisionReference(BclImmutableListNode<KeyValuePair<TKey, TValue>>? node)
    {
        return node is not null && !ReferenceEquals(node, BclImmutableListNode<KeyValuePair<TKey, TValue>>.EmptyNode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefetchKeyValue(TKey key, TValue value)
    {
        if (!typeof(TKey).IsValueType)
        {
            nint keyAddress = Unsafe.As<TKey, nint>(ref key);
            if (keyAddress != 0)
            {
                Prefetch.Address(keyAddress);
            }
        }

        if (!typeof(TValue).IsValueType)
        {
            nint valueAddress = Unsafe.As<TValue, nint>(ref value);
            if (valueAddress != 0)
            {
                Prefetch.Address(valueAddress);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefetchAddress(nint address)
    {
        if (address != 0)
        {
            Prefetch.Address(address);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint ReferenceToAddress<TObject>(TObject value)
    {
        Debug.Assert(!typeof(TObject).IsValueType);
        return Unsafe.As<TObject, nint>(ref value);
    }

    public sealed class BatchLookup
    {
        private readonly BclCopiedImmutableDictionary<TKey, TValue> _dictionary;

        internal BatchLookup(BclCopiedImmutableDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
        }

        /// <summary>
        /// Looks up a batch of keys in the dictionary using a software-prefetched, round-robin lane
        /// traversal of the underlying hash-code-keyed AVL tree. Optimized for the no-collision case
        /// (where almost all hashes map to a bucket containing a single entry) which is by far the
        /// common case for typical extent/document key sets.
        /// </summary>
        /// <param name="keys">The lookup keys, one per lane. Length must be in <c>[0, MaxBatchSize]</c>.</param>
        /// <param name="values">Output span receiving the value for each found lane. Must be at least
        /// <c>keys.Length</c> elements long. Slots for keys that are not found are set to
        /// <c>default(TValue)</c>.</param>
        /// <param name="found">Output span receiving <c>true</c> for each lane whose key was found,
        /// <c>false</c> otherwise. Must be at least <c>keys.Length</c> elements long.</param>
        /// <returns>The number of lanes whose key was found (i.e. <see langword="true"/> entries in
        /// <paramref name="found"/>).</returns>
        /// <remarks>
        /// <para>
        /// The public method is a thin dispatcher that selects one of two hand-tuned hot paths based
        /// on the shape of <typeparamref name="TKey"/>. A unified single-body implementation was
        /// evaluated but was slower on the string path: the JIT does not reliably dead-code-eliminate
        /// the shape-discriminator branches inside a shared <c>__Canon</c>-erased generic instantiation
        /// — neither runtime <c>typeof()</c> checks nor static-abstract policy struct dispatch fold
        /// reliably there. Two dedicated methods keep both paths at their measured optimum:
        /// </para>
        /// <list type="bullet">
        ///   <item><see cref="FindStringBatchPrefetch"/> — string-only path with deferred
        ///   compare. The candidate key prefetch and the cache-pulse trick (skip the descend if the
        ///   bucket key's cache line isn't in cache yet) are inlined; reference-equality short-circuit
        ///   is folded into <see cref="string.Equals(string?, string?, StringComparison)"/>.</item>
        ///   <item><see cref="FindValueTypeBatchPrefetch"/> — value-type-only path with
        ///   inline (single-pass) compare. The value-type key sits on the cache line the bucket read
        ///   already brought in, so we skip the deferred <c>KeyPrefetched</c> state entirely — each
        ///   successful lookup is one round-robin pass shorter than the string path.
        ///   <see cref="EqualityComparer{T}.Default"/> devirtualizes to a primitive compare under the
        ///   value-type generic instantiation.</item>
        /// </list>
        /// <para>
        /// Algorithm (common to both hot paths):
        /// </para>
        /// <list type="number">
        ///   <item>Each lane is seeded with the dictionary root and the lookup key's precomputed hash.
        ///   A software prefetch is issued for the root so its cache line is in flight before the first
        ///   tree-step inspects it.</item>
        ///   <item>The hot loop round-robins across the active lanes. On each visit to a lane it either:
        ///     <list type="bullet">
        ///       <item>descends one level in the AVL tree (<c>hash &lt; nodeKey ? Left : Right</c>),
        ///       writes the chosen child into the lane slot, prefetches its address, and moves on to
        ///       the next lane;</item>
        ///       <item>when the lane's hash equals <c>nodeKey</c> and the key is a reference type,
        ///       prefetches the candidate key and switches the lane to <c>KeyPrefetched</c> state so
        ///       the actual equality check is deferred to the next pass (giving the key's cache line
        ///       time to arrive);</item>
        ///       <item>when the lane's hash equals <c>nodeKey</c> and the key is a value type,
        ///       compares the bucket's first key inline in the same pass — the value-type key sits on
        ///       the cache line the bucket read already brought in;</item>
        ///       <item>when the child is the empty sentinel, marks the lane done.</item>
        ///     </list>
        ///   </item>
        ///   <item>Round-robin continues until every lane has finished. Results are written to
        ///   <paramref name="values"/> and <paramref name="found"/>; the return value is the number of
        ///   hits.</item>
        /// </list>
        /// <para>
        /// Why the no-collision focus: the rare same-hash collision case is handled via the bucket's
        /// additional-elements list but is not allowed to grow per-lane traversal state. On typical
        /// mixed-key data the additional-elements list is empty for ~all hits, which removes the
        /// largest fixed cost of the earlier batch-find variant.
        /// </para>
        /// <para>
        /// Performance: ~2.60 M ops/sec on 10 M elements / 100 M independent lookups (mixed32 string
        /// keys, immutable shape, 16 lanes) — roughly 3.3× the scalar baseline. The implementation is
        /// GC-safe: all lane state is stored as managed references in a stack-allocated
        /// <c>[InlineArray]</c>.
        /// </para>
        /// <para>
        /// Only string and value-type keys are supported; any other reference type throws
        /// <see cref="NotSupportedException"/>.
        /// </para>
        /// </remarks>
        public int FindBatchPrefetch(ReadOnlySpan<TKey> keys, Span<TValue> values, Span<bool> found)
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

            if (typeof(TKey) == typeof(string))
            {
                return FindStringBatchPrefetch(keys, values, found);
            }

            if (typeof(TKey).IsValueType)
            {
                return FindValueTypeBatchPrefetch(keys, values, found);
            }

            throw new NotSupportedException(
                "FindBatchPrefetch supports string and value-type keys only.");
        }

        /// <summary>
        /// String-only hot path for <see cref="FindBatchPrefetch"/>. This is a
        /// hand-tuned specialization for <typeparamref name="TKey"/> = <see cref="string"/>;
        /// the public dispatcher routes <c>typeof(TKey) == typeof(string)</c> here so the
        /// <c>__Canon</c>-shared body does not need any per-iteration shape checks.
        /// </summary>
        /// <remarks>
        /// Per-key-type design choice (deferred compare): for reference-type keys the candidate
        /// key cache line is unlikely to be present when the bucket is read. We prefetch it,
        /// switch the lane to <c>KeyPrefetched</c> state, and pay the compare cost on the next
        /// round-robin visit when the line should have arrived. <see cref="string.Equals(string?, string?, StringComparison)"/>
        /// is called directly (after an <see cref="Unsafe.As{TFrom, TTo}(ref TFrom)"/> reinterpret)
        /// to avoid going through the comparer interface, which would re-introduce a virtual call.
        /// </remarks>
        private int FindStringBatchPrefetch(ReadOnlySpan<TKey> keys, Span<TValue> values, Span<bool> found)
        {
            int laneCount = keys.Length;
            int activeCount = 0;
            int hitCount = 0;
            SortedInt32KeyNode<HashBucket> root = _dictionary._root;

            // Out-of-line two cold/short paths via [NoInlining] helpers. The forward calls
            // consume the byref/managed-reference arguments at the call sites, which splits
            // the JIT's GC-root liveness graph: in the main body below, fewer references
            // remain simultaneously live across the string.Equals safepoint, freeing the
            // register allocator to keep loop state in callee-saved registers instead of
            // spilling to GC-tracked stack slots. The single-key path is also genuinely
            // faster on its own (it avoids ~256 bytes of stackalloc and the active-lanes
            // round-robin bookkeeping that batching needs).
            if (!IsNonEmptyTreeReference(root))
            {
                return ClearAllLanesAndReturn(keys, values, found, root);
            }
            if (laneCount == 1)
            {
                return FindStringSingle(keys, values, found, root);
            }

            Span<int> hashes = stackalloc int[32];
            Span<int> activeLanes = stackalloc int[32];
            Span<BatchLookupLaneState> laneStates = stackalloc BatchLookupLaneState[32];
            BatchNodeBuffer nodes = default;

            // Hoist the empty-node sentinel into a stack local. Reading the static
            // SortedInt32KeyNode<HashBucket>.EmptyNode inside the hot loop forces the JIT
            // to emit a CORINFO_HELP_GETGENERICS_GCSTATIC_BASE helper call on every descent.
            // The sentinel is invariant for the lifetime of the program, so one load is enough.
            SortedInt32KeyNode<HashBucket> emptyNode = SortedInt32KeyNode<HashBucket>.EmptyNode;

            // Take ref-locals over the stackalloc buffers so the hot loop accesses them
            // via Unsafe.Add(ref base, index) instead of through Span's indexer. The Span
            // indexer emits a per-access RNG-CHK guard the JIT cannot eliminate even though
            // every lane index is provably in [0, 32). All three buffers have a fixed length
            // of 32 and are written via the same indices, so this is safe by construction.
            ref int hashesRef = ref MemoryMarshal.GetReference(hashes);
            ref int activeLanesRef = ref MemoryMarshal.GetReference(activeLanes);
            ref BatchLookupLaneState laneStatesRef = ref MemoryMarshal.GetReference(laneStates);

            for (int lane = 0; lane < laneCount; lane++)
            {
                TKey key = keys[lane];
                if (key == null)
                {
                    throw new ArgumentNullException(nameof(keys));
                }

                string stringKey = Unsafe.As<TKey, string>(ref key);
                Unsafe.Add(ref hashesRef, lane) = stringKey.GetHashCode();
                BatchNodeSlot(ref nodes, lane) = root;
                values[lane] = default!;
                found[lane] = false;
                Unsafe.Add(ref laneStatesRef, lane) = BatchLookupLaneState.NodePrefetched;
                Unsafe.Add(ref activeLanesRef, activeCount++) = lane;
                // Issue an L1 prefetch for the root so its cache line is in flight before the
                // first hot-loop visit reads node.Key. The first visit then overlaps with the
                // following lane's address-translation/load latency, which is the whole point
                // of the round-robin design.
                PrefetchAddress(ReferenceToAddress(root));
            }

            // Round-robin until every lane has either resolved to a value or terminated. Lanes
            // are removed from activeLanes in-place by swapping the tail in (see RemoveActiveLane),
            // so the inner loop does NOT increment activeIndex after a removal.
            while (activeCount > 0)
            {
                int activeIndex = 0;
                while (activeIndex < activeCount)
                {
                    int lane = Unsafe.Add(ref activeLanesRef, activeIndex);

                    // Load the lane's current tree node. nodeSlot is a ref into the inline-array
                    // buffer; reading it through the ref keeps the JIT-emitted store on the same
                    // address path as the load, which is what lets the helper-call short-circuit
                    // its card-marking work for stack destinations.
                    ref SortedInt32KeyNode<HashBucket>? nodeSlot = ref BatchNodeSlot(ref nodes, lane);
                    SortedInt32KeyNode<HashBucket> node = nodeSlot!;

                    if (Unsafe.Add(ref laneStatesRef, lane) == BatchLookupLaneState.KeyPrefetched)
                    {
                        // State: the previous visit found the bucket and prefetched the candidate
                        // key string. Now its cache line should be present, so we can pay the
                        // string-compare cost without stalling.
                        HashBucket bucket = node.ValueOrDefault;
                        KeyValuePair<TKey, TValue> pair = bucket.FirstValueUnchecked;

                        // Inline the string-specialized compare so it doesn't go through the
                        // comparer interface (which would re-introduce a virtual call).
                        TKey pairKeyGeneric = pair.Key;
                        string candidateKey = Unsafe.As<TKey, string>(ref pairKeyGeneric);
                        TKey lookupKeyGeneric = keys[lane];
                        string lookupKey = Unsafe.As<TKey, string>(ref lookupKeyGeneric);

                        if (string.Equals(candidateKey, lookupKey, StringComparison.Ordinal))
                        {
                            values[lane] = pair.Value;
                            found[lane] = true;
                            Unsafe.Add(ref laneStatesRef, lane) = BatchLookupLaneState.Done;
                            hitCount++;
                            activeCount = RemoveActiveLane(ref activeLanesRef, activeIndex, activeCount);
                            continue;
                        }

                        // Slow path: the bucket may hold additional same-hash collision entries.
                        // For typical mixed-key data this list is empty and the IsNonEmpty check
                        // short-circuits to false in one branch.
                        BclImmutableListNode<KeyValuePair<TKey, TValue>>? additionalElements = bucket.AdditionalElementsOrNull;
                        if (IsNonEmptyCollisionReference(additionalElements)
                            && bucket.TryGetAdditionalValue(keys[lane], _dictionary._comparers, out TValue? collisionValue))
                        {
                            values[lane] = collisionValue;
                            found[lane] = true;
                            hitCount++;
                        }

                        Unsafe.Add(ref laneStatesRef, lane) = BatchLookupLaneState.Done;
                        activeCount = RemoveActiveLane(ref activeLanesRef, activeIndex, activeCount);
                        continue;
                    }

                    // State: NodePrefetched. The cache line for `node` should be present now, so
                    // we can read node.Key without expecting a miss.
                    int hash = Unsafe.Add(ref hashesRef, lane);
                    int nodeKey = node.Key;
                    if (hash == nodeKey)
                    {
                        HashBucket bucket = node.ValueOrDefault;
                        Debug.Assert(!bucket.IsEmpty);
                        KeyValuePair<TKey, TValue> pair = bucket.FirstValueUnchecked;

                        // Reference-type path: prefetch the candidate key and defer the compare to
                        // the next visit so the key's cache line has time to arrive.
                        TKey pairKeyGeneric = pair.Key;
                        string pairKey = Unsafe.As<TKey, string>(ref pairKeyGeneric);
                        PrefetchAddress(ReferenceToAddress(pairKey));

                        Unsafe.Add(ref laneStatesRef, lane) = BatchLookupLaneState.KeyPrefetched;
                        activeIndex++;
                        continue;
                    }

                    // Descend one level. The next-node pointer is read from the current node
                    // (already in cache from the earlier prefetch). If the child is the empty
                    // sentinel, this lane is done — the key is not present.
                    SortedInt32KeyNode<HashBucket>? next = hash > nodeKey ? node.Right : node.Left;
                    if (next is null || ReferenceEquals(next, emptyNode))
                    {
                        Unsafe.Add(ref laneStatesRef, lane) = BatchLookupLaneState.Done;
                        activeCount = RemoveActiveLane(ref activeLanesRef, activeIndex, activeCount);
                        continue;
                    }

                    // Stay in NodePrefetched state: write the new node into the lane slot and
                    // prefetch its address so the next visit to this lane finds it in cache.
                    nodeSlot = next;
                    PrefetchAddress(ReferenceToAddress(next));
                    activeIndex++;
                }
            }

            return hitCount;

            static int RemoveActiveLane(ref int activeLanesRef, int activeIndex, int activeCount)
            {
                // Swap-with-tail removal: O(1) and keeps the activeLanes prefix dense, so the
                // outer while-loop's bound (activeCount) shrinks naturally as lanes finish.
                activeCount--;
                Unsafe.Add(ref activeLanesRef, activeIndex) = Unsafe.Add(ref activeLanesRef, activeCount);
                return activeCount;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int ClearAllLanesAndReturn(
            ReadOnlySpan<TKey> keys,
            Span<TValue> values,
            Span<bool> found,
            SortedInt32KeyNode<HashBucket> root)
        {
            // root is unused at runtime — it is always the empty sentinel here. It is included
            // in the signature so the call site consumes the same argument shape as the
            // FindStringSingle dispatch below, keeping both branches structurally uniform.
            _ = root;
            int laneCount = keys.Length;
            for (int lane = 0; lane < laneCount; lane++)
            {
                values[lane] = default!;
                found[lane] = false;
            }

            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int FindStringSingle(
            ReadOnlySpan<TKey> keys,
            Span<TValue> values,
            Span<bool> found,
            SortedInt32KeyNode<HashBucket> root)
        {
            // Specialized scalar walk for the laneCount == 1 case. Two reasons it exists:
            //   1. Real perf: avoids the batched path's stackalloc (3×32 spans + InlineArray
            //      lane buffer ≈ 256 bytes) and the round-robin bookkeeping that gives the
            //      batched path its MLP win but is pure overhead when there is only one key.
            //   2. JIT codegen: serves as a second [NoInlining] dispatch at the top of
            //      FindStringBatchPrefetch, which (together with ClearAllLanesAndReturn)
            //      splits the GC-root liveness graph entering the batched main loop.
            TKey keyGeneric = keys[0];
            if (keyGeneric == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            string stringKey = Unsafe.As<TKey, string>(ref keyGeneric);
            int hash = stringKey.GetHashCode();
            SortedInt32KeyNode<HashBucket> node = root;
            SortedInt32KeyNode<HashBucket> emptyNode = SortedInt32KeyNode<HashBucket>.EmptyNode;

            while (true)
            {
                int nodeKey = node.Key;
                if (hash == nodeKey)
                {
                    HashBucket bucket = node.ValueOrDefault;
                    Debug.Assert(!bucket.IsEmpty);
                    KeyValuePair<TKey, TValue> pair = bucket.FirstValueUnchecked;

                    TKey pairKeyGeneric = pair.Key;
                    string candidateKey = Unsafe.As<TKey, string>(ref pairKeyGeneric);

                    if (string.Equals(candidateKey, stringKey, StringComparison.Ordinal))
                    {
                        values[0] = pair.Value;
                        found[0] = true;
                        return 1;
                    }

                    BclImmutableListNode<KeyValuePair<TKey, TValue>>? additionalElements = bucket.AdditionalElementsOrNull;
                    if (IsNonEmptyCollisionReference(additionalElements)
                        && bucket.TryGetAdditionalValue(keys[0], _dictionary._comparers, out TValue? collisionValue))
                    {
                        values[0] = collisionValue;
                        found[0] = true;
                        return 1;
                    }

                    values[0] = default!;
                    found[0] = false;
                    return 0;
                }

                SortedInt32KeyNode<HashBucket>? next = hash > nodeKey ? node.Right : node.Left;
                if (next is null || ReferenceEquals(next, emptyNode))
                {
                    values[0] = default!;
                    found[0] = false;
                    return 0;
                }

                node = next;
            }
        }

        /// <summary>
        /// Value-type-only hot path for <see cref="FindBatchPrefetch"/>. This is a
        /// hand-tuned specialization for value-type <typeparamref name="TKey"/> (e.g. <see cref="int"/>);
        /// the public dispatcher routes <c>typeof(TKey).IsValueType == true</c> here so the
        /// per-<c>TKey</c>-specialized generic body does not need any per-iteration shape checks.
        /// </summary>
        /// <remarks>
        /// Per-key-type design choice (inline single-pass compare): for value-type keys the
        /// bucket's first key sits on the cache line the bucket read already brought in
        /// (<see cref="KeyValuePair{TKey, TValue}"/> is laid out inline inside the bucket), so
        /// we skip the deferred <c>KeyPrefetched</c> state entirely and compare in the same
        /// pass that finds the matching hash. Each successful lookup is one round-robin pass
        /// shorter than the string path. <see cref="EqualityComparer{T}.Default"/> devirtualizes
        /// to a primitive compare under a value-type generic instantiation, so the call below
        /// becomes a direct compare with no virtual dispatch.
        /// </remarks>
        private int FindValueTypeBatchPrefetch(ReadOnlySpan<TKey> keys, Span<TValue> values, Span<bool> found)
        {
            int laneCount = keys.Length;
            int activeCount = 0;
            int hitCount = 0;
            SortedInt32KeyNode<HashBucket> root = _dictionary._root;

            if (!IsNonEmptyTreeReference(root))
            {
                for (int lane = 0; lane < laneCount; lane++)
                {
                    values[lane] = default!;
                    found[lane] = false;
                }

                return 0;
            }

            Span<int> hashes = stackalloc int[32];
            Span<int> activeLanes = stackalloc int[32];
            BatchNodeBuffer nodes = default;

            // Hoist the empty-node sentinel into a stack local; see the string method for the
            // helper-call rationale. Identical safety: the sentinel is process-invariant.
            SortedInt32KeyNode<HashBucket> emptyNode = SortedInt32KeyNode<HashBucket>.EmptyNode;

            // Fair-comparison path: route every key hash/equals through the stored
            // IEqualityComparer<TKey> field (interface dispatch), mirroring what the real
            // BCL ImmutableDictionary does. Hoisting the field load out of the hot loop is
            // the same shape the BCL uses; the interface call itself stays virtual.
            IEqualityComparer<TKey> keyComparer = _dictionary._comparers.KeyComparer;

            // Ref-locals over the stackalloc buffers to bypass the Span indexer's RNG-CHK guards.
            // The two buffers (hashes, activeLanes) are stackalloc[32] and accessed only via lane
            // indices in [0, 32), so this is safe by construction.
            ref int hashesRef = ref MemoryMarshal.GetReference(hashes);
            ref int activeLanesRef = ref MemoryMarshal.GetReference(activeLanes);

            for (int lane = 0; lane < laneCount; lane++)
            {
                TKey key = keys[lane];
                Unsafe.Add(ref hashesRef, lane) = keyComparer.GetHashCode(key);
                BatchNodeSlot(ref nodes, lane) = root;
                values[lane] = default!;
                found[lane] = false;
                Unsafe.Add(ref activeLanesRef, activeCount++) = lane;
                PrefetchAddress(ReferenceToAddress(root));
            }

            while (activeCount > 0)
            {
                int activeIndex = 0;
                while (activeIndex < activeCount)
                {
                    int lane = Unsafe.Add(ref activeLanesRef, activeIndex);
                    ref SortedInt32KeyNode<HashBucket>? nodeSlot = ref BatchNodeSlot(ref nodes, lane);
                    SortedInt32KeyNode<HashBucket> node = nodeSlot!;

                    // State: NodePrefetched (the only state — value-type path never defers).
                    int hash = Unsafe.Add(ref hashesRef, lane);
                    int nodeKey = node.Key;
                    if (hash == nodeKey)
                    {
                        // Value-type fast path: compare inline in the same pass.
                        HashBucket bucket = node.ValueOrDefault;
                        Debug.Assert(!bucket.IsEmpty);
                        KeyValuePair<TKey, TValue> pair = bucket.FirstValueUnchecked;
                        TKey lookupKey = keys[lane];
                        if (keyComparer.Equals(pair.Key, lookupKey))
                        {
                            values[lane] = pair.Value;
                            found[lane] = true;
                            hitCount++;
                        }
                        else
                        {
                            BclImmutableListNode<KeyValuePair<TKey, TValue>>? additionalElements = bucket.AdditionalElementsOrNull;
                            if (IsNonEmptyCollisionReference(additionalElements)
                                && bucket.TryGetAdditionalValue(lookupKey, _dictionary._comparers, out TValue? collisionValue))
                            {
                                values[lane] = collisionValue;
                                found[lane] = true;
                                hitCount++;
                            }
                        }

                        activeCount = RemoveActiveLane(ref activeLanesRef, activeIndex, activeCount);
                        continue;
                    }

                    SortedInt32KeyNode<HashBucket>? next = hash > nodeKey ? node.Right : node.Left;
                    if (next is null || ReferenceEquals(next, emptyNode))
                    {
                        activeCount = RemoveActiveLane(ref activeLanesRef, activeIndex, activeCount);
                        continue;
                    }

                    nodeSlot = next;
                    PrefetchAddress(ReferenceToAddress(next));
                    activeIndex++;
                }
            }

            return hitCount;

            static int RemoveActiveLane(ref int activeLanesRef, int activeIndex, int activeCount)
            {
                activeCount--;
                Unsafe.Add(ref activeLanesRef, activeIndex) = Unsafe.Add(ref activeLanesRef, activeCount);
                return activeCount;
            }
        }

    }
}
