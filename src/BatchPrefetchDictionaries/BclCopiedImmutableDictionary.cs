// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Derived from dotnet/runtime; modified for this repository (internals exposed
// for benchmarking). See NOTICE.md for attribution.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BatchPrefetchDictionaries;

/// <summary>
/// Generic benchmark copy of the core dotnet/runtime ImmutableDictionary&lt;TKey,TValue&gt;
/// lookup layout: an Int32-keyed AVL tree over hash codes, with a hash bucket at each
/// hash node. Batch lookup is added on top of that copied generic layout.
/// </summary>
public sealed partial class BclCopiedImmutableDictionary<TKey, TValue>
    where TKey : notnull
{

    private static readonly Action<KeyValuePair<int, HashBucket>> s_freezeBucketAction = static kv => kv.Value.Freeze();

    private readonly int _count;
    private readonly SortedInt32KeyNode<HashBucket> _root;
    private readonly Comparers _comparers;

    public BclCopiedImmutableDictionary(IEqualityComparer<TKey>? keyComparer = null, IEqualityComparer<TValue>? valueComparer = null)
    {
        _comparers = Comparers.Get(keyComparer ?? EqualityComparer<TKey>.Default, valueComparer ?? EqualityComparer<TValue>.Default);
        _root = SortedInt32KeyNode<HashBucket>.EmptyNode;
    }

    private BclCopiedImmutableDictionary(SortedInt32KeyNode<HashBucket> root, Comparers comparers, int count)
        : this(comparers)
    {
        root.Freeze(s_freezeBucketAction);
        _root = root;
        _count = count;
    }

    private BclCopiedImmutableDictionary(Comparers comparers)
    {
        _comparers = comparers;
        _root = SortedInt32KeyNode<HashBucket>.EmptyNode;
    }

    public int Count => _count;

    public IEqualityComparer<TKey> KeyComparer => _comparers.KeyComparer;

    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
    }

    public IEnumerable<TValue> Values
    {
        get
        {
            foreach (KeyValuePair<int, HashBucket> bucket in _root)
            {
                foreach (KeyValuePair<TKey, TValue> item in bucket.Value)
                {
                    yield return item.Value;
                }
            }
        }
    }

    public static BclCopiedImmutableDictionary<TKey, TValue> CreateRange(
        IEnumerable<KeyValuePair<TKey, TValue>> items,
        IEqualityComparer<TKey>? keyComparer = null,
        IEqualityComparer<TValue>? valueComparer = null)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        Comparers comparers = Comparers.Get(keyComparer ?? EqualityComparer<TKey>.Default, valueComparer ?? EqualityComparer<TValue>.Default);
        SortedInt32KeyNode<HashBucket> root = SortedInt32KeyNode<HashBucket>.EmptyNode;
        int count = 0;

        foreach (KeyValuePair<TKey, TValue> item in items)
        {
            if (item.Key is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            int hashCode = comparers.KeyComparer.GetHashCode(item.Key);
            HashBucket bucket = root.GetValueOrDefault(hashCode);
            HashBucket newBucket = bucket.Add(
                item.Key,
                item.Value,
                comparers.KeyOnlyComparer,
                comparers.ValueComparer,
                KeyCollisionBehavior.ThrowAlways,
                out OperationResult result);

            root = UpdateRoot(root, hashCode, newBucket, comparers.HashBucketEqualityComparer);
            if (result == OperationResult.SizeChanged)
            {
                count++;
            }
        }

        return new BclCopiedImmutableDictionary<TKey, TValue>(root, comparers, count);
    }

    public BclCopiedImmutableDictionary<TKey, TValue> Add(TKey key, TValue value)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        int hashCode = _comparers.KeyComparer.GetHashCode(key);
        HashBucket bucket = _root.GetValueOrDefault(hashCode);
        HashBucket newBucket = bucket.Add(
            key,
            value,
            _comparers.KeyOnlyComparer,
            _comparers.ValueComparer,
            KeyCollisionBehavior.ThrowAlways,
            out OperationResult result);

        SortedInt32KeyNode<HashBucket> newRoot = UpdateRoot(_root, hashCode, newBucket, _comparers.HashBucketEqualityComparer);
        return Wrap(newRoot, result == OperationResult.SizeChanged ? _count + 1 : _count);
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        return TryGetValue(key, new MutationInput(_root, _comparers), out value);
    }

    private static bool TryGetValue(TKey key, MutationInput origin, [MaybeNullWhen(false)] out TValue value)
    {
        int hashCode = origin.KeyComparer.GetHashCode(key);
        if (origin.Root.TryGetValue(hashCode, out HashBucket bucket))
        {
            return bucket.TryGetValue(key, origin.Comparers, out value);
        }

        value = default;
        return false;
    }

    private static SortedInt32KeyNode<HashBucket> UpdateRoot(
        SortedInt32KeyNode<HashBucket> root,
        int hashCode,
        HashBucket newBucket,
        IEqualityComparer<HashBucket> hashBucketComparer)
    {
        bool mutated;
        bool replacedExistingValue;
        if (newBucket.IsEmpty)
        {
            return root.Remove(hashCode, out mutated);
        }

        return root.SetItem(hashCode, newBucket, hashBucketComparer, out replacedExistingValue, out mutated);
    }

    private BclCopiedImmutableDictionary<TKey, TValue> Wrap(SortedInt32KeyNode<HashBucket> root, int count)
    {
        if (root == _root)
        {
            return this;
        }

        return new BclCopiedImmutableDictionary<TKey, TValue>(root, _comparers, count);
    }


    private enum KeyCollisionBehavior
    {
        SetValue,
        Skip,
        ThrowIfValueDifferent,
        ThrowAlways,
    }

    private enum OperationResult
    {
        AppliedWithoutSizeChange,
        SizeChanged,
        NoChangeRequired,
    }

    private readonly struct MutationInput
    {
        internal MutationInput(SortedInt32KeyNode<HashBucket> root, Comparers comparers)
        {
            Root = root;
            Comparers = comparers;
        }

        internal SortedInt32KeyNode<HashBucket> Root { get; }

        internal Comparers Comparers { get; }

        internal IEqualityComparer<TKey> KeyComparer => Comparers.KeyComparer;
    }

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private SortedInt32KeyNode<HashBucket>.Enumerator _rootEnumerator;
        private HashBucket.Enumerator _bucketEnumerator;

        internal Enumerator(BclCopiedImmutableDictionary<TKey, TValue> dictionary)
        {
            _rootEnumerator = dictionary._root.GetEnumerator();
            _bucketEnumerator = default;
        }

        public KeyValuePair<TKey, TValue> Current => _bucketEnumerator.Current;

        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_bucketEnumerator.MoveNext())
            {
                return true;
            }

            if (_rootEnumerator.MoveNext())
            {
                _bucketEnumerator = _rootEnumerator.Current.Value.GetEnumerator();
                return _bucketEnumerator.MoveNext();
            }

            return false;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
            _rootEnumerator.Dispose();
            _bucketEnumerator.Dispose();
        }
    }

    private sealed class Comparers : IEqualityComparer<HashBucket>, IEqualityComparer<KeyValuePair<TKey, TValue>>
    {
        internal static readonly Comparers Default = new(EqualityComparer<TKey>.Default, EqualityComparer<TValue>.Default);

        private readonly IEqualityComparer<TKey> _keyComparer;
        private readonly IEqualityComparer<TValue> _valueComparer;

        private Comparers(IEqualityComparer<TKey> keyComparer, IEqualityComparer<TValue> valueComparer)
        {
            _keyComparer = keyComparer;
            _valueComparer = valueComparer;
        }

        internal IEqualityComparer<TKey> KeyComparer => _keyComparer;

        internal IEqualityComparer<KeyValuePair<TKey, TValue>> KeyOnlyComparer => this;

        internal IEqualityComparer<TValue> ValueComparer => _valueComparer;

        internal IEqualityComparer<HashBucket> HashBucketEqualityComparer => this;

        public bool Equals(HashBucket x, HashBucket y)
        {
            return ReferenceEquals(x.AdditionalElements, y.AdditionalElements)
                && _keyComparer.Equals(x.FirstValue.Key, y.FirstValue.Key)
                && _valueComparer.Equals(x.FirstValue.Value, y.FirstValue.Value);
        }

        public int GetHashCode(HashBucket obj)
        {
            return _keyComparer.GetHashCode(obj.FirstValue.Key);
        }

        bool IEqualityComparer<KeyValuePair<TKey, TValue>>.Equals(KeyValuePair<TKey, TValue> x, KeyValuePair<TKey, TValue> y)
        {
            return _keyComparer.Equals(x.Key, y.Key);
        }

        int IEqualityComparer<KeyValuePair<TKey, TValue>>.GetHashCode(KeyValuePair<TKey, TValue> obj)
        {
            return _keyComparer.GetHashCode(obj.Key);
        }

        internal static Comparers Get(IEqualityComparer<TKey> keyComparer, IEqualityComparer<TValue> valueComparer)
        {
            return keyComparer == Default.KeyComparer && valueComparer == Default.ValueComparer
                ? Default
                : new Comparers(keyComparer, valueComparer);
        }
    }

    private readonly struct HashBucket : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        private readonly KeyValuePair<TKey, TValue> _firstValue;
        private readonly BclImmutableListNode<KeyValuePair<TKey, TValue>>? _additionalElements;

        private HashBucket(
            KeyValuePair<TKey, TValue> firstElement,
            BclImmutableListNode<KeyValuePair<TKey, TValue>>? additionalElements = null)
        {
            _firstValue = firstElement;
            _additionalElements = additionalElements ?? BclImmutableListNode<KeyValuePair<TKey, TValue>>.EmptyNode;
        }

        internal bool IsEmpty => _additionalElements == null;

        internal KeyValuePair<TKey, TValue> FirstValue
        {
            get
            {
                if (IsEmpty)
                {
                    throw new InvalidOperationException();
                }

                return _firstValue;
            }
        }

        internal BclImmutableListNode<KeyValuePair<TKey, TValue>> AdditionalElements => _additionalElements!;

        internal KeyValuePair<TKey, TValue> FirstValueUnchecked
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _firstValue;
        }

        internal BclImmutableListNode<KeyValuePair<TKey, TValue>>? AdditionalElementsOrNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _additionalElements;
        }

        public Enumerator GetEnumerator() => new(this);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        internal HashBucket Add(
            TKey key,
            TValue value,
            IEqualityComparer<KeyValuePair<TKey, TValue>> keyOnlyComparer,
            IEqualityComparer<TValue> valueComparer,
            KeyCollisionBehavior behavior,
            out OperationResult result)
        {
            var kv = new KeyValuePair<TKey, TValue>(key, value);
            if (IsEmpty)
            {
                result = OperationResult.SizeChanged;
                return new HashBucket(kv);
            }

            if (keyOnlyComparer.Equals(kv, _firstValue))
            {
                switch (behavior)
                {
                    case KeyCollisionBehavior.SetValue:
                        result = OperationResult.AppliedWithoutSizeChange;
                        return new HashBucket(kv, _additionalElements);
                    case KeyCollisionBehavior.Skip:
                        result = OperationResult.NoChangeRequired;
                        return this;
                    case KeyCollisionBehavior.ThrowIfValueDifferent:
                        if (!valueComparer.Equals(_firstValue.Value, value))
                        {
                            throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
                        }

                        result = OperationResult.NoChangeRequired;
                        return this;
                    case KeyCollisionBehavior.ThrowAlways:
                        throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
                    default:
                        throw new InvalidOperationException();
                }
            }

            int keyCollisionIndex = _additionalElements!.IndexOf(kv, keyOnlyComparer);
            if (keyCollisionIndex < 0)
            {
                result = OperationResult.SizeChanged;
                return new HashBucket(_firstValue, _additionalElements.Add(kv));
            }

            switch (behavior)
            {
                case KeyCollisionBehavior.SetValue:
                    result = OperationResult.AppliedWithoutSizeChange;
                    return new HashBucket(_firstValue, _additionalElements.ReplaceAt(keyCollisionIndex, kv));
                case KeyCollisionBehavior.Skip:
                    result = OperationResult.NoChangeRequired;
                    return this;
                case KeyCollisionBehavior.ThrowIfValueDifferent:
                    ref readonly KeyValuePair<TKey, TValue> existingEntry = ref _additionalElements.ItemRef(keyCollisionIndex);
                    if (!valueComparer.Equals(existingEntry.Value, value))
                    {
                        throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
                    }

                    result = OperationResult.NoChangeRequired;
                    return this;
                case KeyCollisionBehavior.ThrowAlways:
                    throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
                default:
                    throw new InvalidOperationException();
            }
        }

        internal bool TryGetValue(TKey key, Comparers comparers, [MaybeNullWhen(false)] out TValue value)
        {
            if (IsEmpty)
            {
                value = default;
                return false;
            }

            if (comparers.KeyComparer.Equals(_firstValue.Key, key))
            {
                value = _firstValue.Value;
                return true;
            }

            var kv = new KeyValuePair<TKey, TValue>(key, default!);
            int index = _additionalElements!.IndexOf(kv, comparers.KeyOnlyComparer);
            if (index < 0)
            {
                value = default;
                return false;
            }

            value = _additionalElements.ItemRef(index).Value;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetAdditionalValue(TKey key, Comparers comparers, [MaybeNullWhen(false)] out TValue value)
        {
            var kv = new KeyValuePair<TKey, TValue>(key, default!);
            int index = _additionalElements!.IndexOf(kv, comparers.KeyOnlyComparer);
            if (index < 0)
            {
                value = default;
                return false;
            }

            value = _additionalElements.ItemRef(index).Value;
            return true;
        }

        internal void Freeze()
        {
            _additionalElements?.Freeze();
        }

        internal struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private readonly HashBucket _bucket;
            private Position _currentPosition;
            private BclImmutableListNode<KeyValuePair<TKey, TValue>>.Enumerator _additionalEnumerator;

            internal Enumerator(HashBucket bucket)
            {
                _bucket = bucket;
                _currentPosition = Position.BeforeFirst;
                _additionalEnumerator = default;
            }

            private enum Position
            {
                BeforeFirst,
                First,
                Additional,
                End,
            }

            public KeyValuePair<TKey, TValue> Current => _currentPosition switch
            {
                Position.First => _bucket._firstValue,
                Position.Additional => _additionalEnumerator.Current,
                _ => throw new InvalidOperationException(),
            };

            object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_bucket.IsEmpty)
                {
                    _currentPosition = Position.End;
                    return false;
                }

                switch (_currentPosition)
                {
                    case Position.BeforeFirst:
                        _currentPosition = Position.First;
                        return true;
                    case Position.First:
                        if (_bucket._additionalElements!.IsEmpty)
                        {
                            _currentPosition = Position.End;
                            return false;
                        }

                        _currentPosition = Position.Additional;
                        _additionalEnumerator = new BclImmutableListNode<KeyValuePair<TKey, TValue>>.Enumerator(_bucket._additionalElements);
                        return _additionalEnumerator.MoveNext();
                    case Position.Additional:
                        return _additionalEnumerator.MoveNext();
                    case Position.End:
                        return false;
                    default:
                        throw new InvalidOperationException();
                }
            }

            public void Reset()
            {
                _additionalEnumerator.Dispose();
                _currentPosition = Position.BeforeFirst;
            }

            public void Dispose()
            {
                _additionalEnumerator.Dispose();
            }
        }
    }

    private sealed class SortedInt32KeyNode<TNodeValue> : IEnumerable<KeyValuePair<int, TNodeValue>>
    {
        internal static readonly SortedInt32KeyNode<TNodeValue> EmptyNode = new();

        internal readonly int _key;
        private readonly TNodeValue? _value;
        private bool _frozen;
        internal byte _height;
        private SortedInt32KeyNode<TNodeValue>? _left;
        private SortedInt32KeyNode<TNodeValue>? _right;

        private SortedInt32KeyNode()
        {
            _frozen = true;
        }

        private SortedInt32KeyNode(
            int key,
            TNodeValue value,
            SortedInt32KeyNode<TNodeValue> left,
            SortedInt32KeyNode<TNodeValue> right,
            bool frozen = false)
        {
            _key = key;
            _value = value;
            _left = left;
            _right = right;
            _frozen = frozen;
            _height = checked((byte)(1 + Math.Max(left._height, right._height)));
        }

        public bool IsEmpty => _left == null;

        public int Key => _key;

        public TNodeValue ValueOrDefault => _value!;

        public SortedInt32KeyNode<TNodeValue>? Left => _left;

        public SortedInt32KeyNode<TNodeValue>? Right => _right;

        public Enumerator GetEnumerator() => new(this);

        IEnumerator<KeyValuePair<int, TNodeValue>> IEnumerable<KeyValuePair<int, TNodeValue>>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        internal TNodeValue GetValueOrDefault(int key)
        {
            SortedInt32KeyNode<TNodeValue> node = this;
            while (true)
            {
                if (node.IsEmpty)
                {
                    return default!;
                }

                if (key == node._key)
                {
                    return node._value!;
                }

                node = key > node._key ? node._right! : node._left!;
            }
        }

        internal bool TryGetValue(int key, [MaybeNullWhen(false)] out TNodeValue value)
        {
            SortedInt32KeyNode<TNodeValue> node = this;
            while (true)
            {
                if (node.IsEmpty)
                {
                    value = default;
                    return false;
                }

                if (key == node._key)
                {
                    value = node._value!;
                    return true;
                }

                node = key > node._key ? node._right! : node._left!;
            }
        }

        internal SortedInt32KeyNode<TNodeValue> SetItem(
            int key,
            TNodeValue value,
            IEqualityComparer<TNodeValue> valueComparer,
            out bool replacedExistingValue,
            out bool mutated)
        {
            return SetOrAdd(key, value, valueComparer, overwriteExistingValue: true, out replacedExistingValue, out mutated);
        }

        internal SortedInt32KeyNode<TNodeValue> Remove(int key, out bool mutated)
        {
            return RemoveRecursive(key, out mutated);
        }

        internal void Freeze(Action<KeyValuePair<int, TNodeValue>>? freezeAction = null)
        {
            if (!_frozen)
            {
                freezeAction?.Invoke(new KeyValuePair<int, TNodeValue>(_key, _value!));
                _left!.Freeze(freezeAction);
                _right!.Freeze(freezeAction);
                _frozen = true;
            }
        }

        private SortedInt32KeyNode<TNodeValue> SetOrAdd(
            int key,
            TNodeValue value,
            IEqualityComparer<TNodeValue> valueComparer,
            bool overwriteExistingValue,
            out bool replacedExistingValue,
            out bool mutated)
        {
            replacedExistingValue = false;
            if (IsEmpty)
            {
                mutated = true;
                return new SortedInt32KeyNode<TNodeValue>(key, value, this, this);
            }

            SortedInt32KeyNode<TNodeValue> result = this;
            if (key > _key)
            {
                SortedInt32KeyNode<TNodeValue> newRight = _right!.SetOrAdd(key, value, valueComparer, overwriteExistingValue, out replacedExistingValue, out mutated);
                if (mutated)
                {
                    result = Mutate(right: newRight);
                }
            }
            else if (key < _key)
            {
                SortedInt32KeyNode<TNodeValue> newLeft = _left!.SetOrAdd(key, value, valueComparer, overwriteExistingValue, out replacedExistingValue, out mutated);
                if (mutated)
                {
                    result = Mutate(left: newLeft);
                }
            }
            else
            {
                if (valueComparer.Equals(_value!, value))
                {
                    mutated = false;
                    return this;
                }

                if (overwriteExistingValue)
                {
                    mutated = true;
                    replacedExistingValue = true;
                    result = new SortedInt32KeyNode<TNodeValue>(key, value, _left!, _right!);
                }
                else
                {
                    throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
                }
            }

            return mutated ? MakeBalanced(result) : result;
        }

        private SortedInt32KeyNode<TNodeValue> RemoveRecursive(int key, out bool mutated)
        {
            if (IsEmpty)
            {
                mutated = false;
                return this;
            }

            SortedInt32KeyNode<TNodeValue> result = this;
            if (key == _key)
            {
                mutated = true;
                if (_right!.IsEmpty && _left!.IsEmpty)
                {
                    result = EmptyNode;
                }
                else if (_right.IsEmpty && !_left!.IsEmpty)
                {
                    result = _left;
                }
                else if (!_right.IsEmpty && _left!.IsEmpty)
                {
                    result = _right;
                }
                else
                {
                    SortedInt32KeyNode<TNodeValue> successor = _right;
                    while (!successor._left!.IsEmpty)
                    {
                        successor = successor._left;
                    }

                    SortedInt32KeyNode<TNodeValue> newRight = _right.Remove(successor._key, out _);
                    result = successor.Mutate(left: _left, right: newRight);
                }
            }
            else if (key < _key)
            {
                SortedInt32KeyNode<TNodeValue> newLeft = _left!.Remove(key, out mutated);
                if (mutated)
                {
                    result = Mutate(left: newLeft);
                }
            }
            else
            {
                SortedInt32KeyNode<TNodeValue> newRight = _right!.Remove(key, out mutated);
                if (mutated)
                {
                    result = Mutate(right: newRight);
                }
            }

            return result.IsEmpty || !mutated ? result : MakeBalanced(result);
        }

        private SortedInt32KeyNode<TNodeValue> Mutate(
            SortedInt32KeyNode<TNodeValue>? left = null,
            SortedInt32KeyNode<TNodeValue>? right = null)
        {
            Debug.Assert(_left != null && _right != null);
            if (_frozen)
            {
                return new SortedInt32KeyNode<TNodeValue>(_key, _value!, left ?? _left, right ?? _right);
            }

            if (left != null)
            {
                _left = left;
            }

            if (right != null)
            {
                _right = right;
            }

            _height = checked((byte)(1 + Math.Max(_left._height, _right._height)));
            return this;
        }

        private static SortedInt32KeyNode<TNodeValue> RotateLeft(SortedInt32KeyNode<TNodeValue> tree)
        {
            Debug.Assert(!tree.IsEmpty);
            if (tree._right!.IsEmpty)
            {
                return tree;
            }

            SortedInt32KeyNode<TNodeValue> right = tree._right;
            return right.Mutate(left: tree.Mutate(right: right._left!));
        }

        private static SortedInt32KeyNode<TNodeValue> RotateRight(SortedInt32KeyNode<TNodeValue> tree)
        {
            Debug.Assert(!tree.IsEmpty);
            if (tree._left!.IsEmpty)
            {
                return tree;
            }

            SortedInt32KeyNode<TNodeValue> left = tree._left;
            return left.Mutate(right: tree.Mutate(left: left._right!));
        }

        private static SortedInt32KeyNode<TNodeValue> DoubleLeft(SortedInt32KeyNode<TNodeValue> tree)
        {
            SortedInt32KeyNode<TNodeValue> rotatedRightChild = tree.Mutate(right: RotateRight(tree._right!));
            return RotateLeft(rotatedRightChild);
        }

        private static SortedInt32KeyNode<TNodeValue> DoubleRight(SortedInt32KeyNode<TNodeValue> tree)
        {
            SortedInt32KeyNode<TNodeValue> rotatedLeftChild = tree.Mutate(left: RotateLeft(tree._left!));
            return RotateRight(rotatedLeftChild);
        }

        private static int Balance(SortedInt32KeyNode<TNodeValue> tree)
        {
            return tree._right!._height - tree._left!._height;
        }

        private static SortedInt32KeyNode<TNodeValue> MakeBalanced(SortedInt32KeyNode<TNodeValue> tree)
        {
            if (Balance(tree) >= 2)
            {
                return Balance(tree._right!) < 0 ? DoubleLeft(tree) : RotateLeft(tree);
            }

            if (Balance(tree) <= -2)
            {
                return Balance(tree._left!) > 0 ? DoubleRight(tree) : RotateRight(tree);
            }

            return tree;
        }

        public struct Enumerator : IEnumerator<KeyValuePair<int, TNodeValue>>
        {
            private Stack<RefAsValueType<SortedInt32KeyNode<TNodeValue>>>? _stack;
            private SortedInt32KeyNode<TNodeValue>? _current;

            internal Enumerator(SortedInt32KeyNode<TNodeValue> root)
            {
                _current = null;
                _stack = root.IsEmpty ? null : new Stack<RefAsValueType<SortedInt32KeyNode<TNodeValue>>>(root._height);
                if (_stack != null)
                {
                    PushLeft(root);
                }
            }

            public KeyValuePair<int, TNodeValue> Current
            {
                get
                {
                    if (_current == null)
                    {
                        throw new InvalidOperationException();
                    }

                    return new KeyValuePair<int, TNodeValue>(_current._key, _current._value!);
                }
            }

            object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_stack is { Count: > 0 } stack)
                {
                    SortedInt32KeyNode<TNodeValue> node = stack.Pop().Value!;
                    _current = node;
                    PushLeft(node._right!);
                    return true;
                }

                _current = null;
                return false;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                _stack?.Clear();
                _stack = null;
                _current = null;
            }

            private void PushLeft(SortedInt32KeyNode<TNodeValue> node)
            {
                while (!node.IsEmpty)
                {
                    _stack!.Push(new RefAsValueType<SortedInt32KeyNode<TNodeValue>>(node));
                    node = node._left!;
                }
            }
        }
    }

    private sealed class BclImmutableListNode<T> : IEnumerable<T>
    {
        internal static readonly BclImmutableListNode<T> EmptyNode = new();

        internal T _key = default!;
        private bool _frozen;
        internal byte _height;
        private int _count;
        private BclImmutableListNode<T>? _left;
        private BclImmutableListNode<T>? _right;

        private BclImmutableListNode()
        {
            _frozen = true;
        }

        private BclImmutableListNode(T key, BclImmutableListNode<T> left, BclImmutableListNode<T> right, bool frozen = false)
        {
            _key = key;
            _left = left;
            _right = right;
            _height = ParentHeight(left, right);
            _count = ParentCount(left, right);
            _frozen = frozen;
        }

        public bool IsEmpty => _left == null;

        public int Count => _count;

        public BclImmutableListNode<T>? Left => _left;

        public BclImmutableListNode<T>? Right => _right;

        public T Key => _key;

        internal ref readonly T ItemRef(int index)
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return ref ItemRefUnchecked(index);
        }

        private ref readonly T ItemRefUnchecked(int index)
        {
            Debug.Assert(_left != null && _right != null);
            if (index < _left._count)
            {
                return ref _left.ItemRefUnchecked(index);
            }

            if (index > _left._count)
            {
                return ref _right.ItemRefUnchecked(index - _left._count - 1);
            }

            return ref _key;
        }

        internal BclImmutableListNode<T> Add(T key)
        {
            if (IsEmpty)
            {
                return CreateLeaf(key);
            }

            BclImmutableListNode<T> newRight = _right!.Add(key);
            BclImmutableListNode<T> result = MutateRight(newRight);
            return result.IsBalanced ? result : result.BalanceRight();
        }

        internal BclImmutableListNode<T> ReplaceAt(int index, T value)
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            BclImmutableListNode<T> result;
            if (index == _left!._count)
            {
                result = MutateKey(value);
            }
            else if (index < _left._count)
            {
                BclImmutableListNode<T> newLeft = _left.ReplaceAt(index, value);
                result = MutateLeft(newLeft);
            }
            else
            {
                BclImmutableListNode<T> newRight = _right!.ReplaceAt(index - _left._count - 1, value);
                result = MutateRight(newRight);
            }

            return result;
        }

        internal int IndexOf(T item, IEqualityComparer<T>? equalityComparer)
        {
            equalityComparer ??= EqualityComparer<T>.Default;
            int index = 0;
            using Enumerator enumerator = new(this);
            while (enumerator.MoveNext())
            {
                if (equalityComparer.Equals(item, enumerator.Current))
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        internal void Freeze()
        {
            if (!_frozen)
            {
                _left!.Freeze();
                _right!.Freeze();
                _frozen = true;
            }
        }

        public Enumerator GetEnumerator() => new(this);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private BclImmutableListNode<T> RotateLeft()
        {
            Debug.Assert(!IsEmpty);
            Debug.Assert(!_right!.IsEmpty);
            return _right.MutateLeft(MutateRight(_right._left!));
        }

        private BclImmutableListNode<T> RotateRight()
        {
            Debug.Assert(!IsEmpty);
            Debug.Assert(!_left!.IsEmpty);
            return _left.MutateRight(MutateLeft(_left._right!));
        }

        private BclImmutableListNode<T> DoubleLeft()
        {
            BclImmutableListNode<T> right = _right!;
            BclImmutableListNode<T> rightLeft = right._left!;
            return rightLeft.MutateBoth(
                MutateRight(rightLeft._left!),
                right.MutateLeft(rightLeft._right!));
        }

        private BclImmutableListNode<T> DoubleRight()
        {
            BclImmutableListNode<T> left = _left!;
            BclImmutableListNode<T> leftRight = left._right!;
            return leftRight.MutateBoth(
                left.MutateRight(leftRight._left!),
                MutateLeft(leftRight._right!));
        }

        private int BalanceFactor => _right!._height - _left!._height;

        private bool IsRightHeavy => BalanceFactor >= 2;

        private bool IsLeftHeavy => BalanceFactor <= -2;

        private bool IsBalanced => unchecked((uint)(BalanceFactor + 1)) <= 2;

        private BclImmutableListNode<T> BalanceLeft() => _left!.BalanceFactor > 0 ? DoubleRight() : RotateRight();

        private BclImmutableListNode<T> BalanceRight() => _right!.BalanceFactor < 0 ? DoubleLeft() : RotateLeft();

        private BclImmutableListNode<T> MutateBoth(BclImmutableListNode<T> left, BclImmutableListNode<T> right)
        {
            if (_frozen)
            {
                return new BclImmutableListNode<T>(_key, left, right);
            }

            _left = left;
            _right = right;
            _height = ParentHeight(left, right);
            _count = ParentCount(left, right);
            return this;
        }

        private BclImmutableListNode<T> MutateLeft(BclImmutableListNode<T> left)
        {
            if (_frozen)
            {
                return new BclImmutableListNode<T>(_key, left, _right!);
            }

            _left = left;
            _height = ParentHeight(left, _right!);
            _count = ParentCount(left, _right!);
            return this;
        }

        private BclImmutableListNode<T> MutateRight(BclImmutableListNode<T> right)
        {
            if (_frozen)
            {
                return new BclImmutableListNode<T>(_key, _left!, right);
            }

            _right = right;
            _height = ParentHeight(_left!, right);
            _count = ParentCount(_left!, right);
            return this;
        }

        private BclImmutableListNode<T> MutateKey(T key)
        {
            if (_frozen)
            {
                return new BclImmutableListNode<T>(key, _left!, _right!);
            }

            _key = key;
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ParentHeight(BclImmutableListNode<T> left, BclImmutableListNode<T> right)
        {
            return checked((byte)(1 + Math.Max(left._height, right._height)));
        }

        private static int ParentCount(BclImmutableListNode<T> left, BclImmutableListNode<T> right)
        {
            return 1 + left._count + right._count;
        }

        private static BclImmutableListNode<T> CreateLeaf(T key)
        {
            return new BclImmutableListNode<T>(key, EmptyNode, EmptyNode);
        }

        public struct Enumerator : IEnumerator<T>
        {
            private Stack<RefAsValueType<BclImmutableListNode<T>>>? _stack;
            private BclImmutableListNode<T>? _current;

            internal Enumerator(BclImmutableListNode<T> root)
            {
                _current = null;
                _stack = root.IsEmpty ? null : new Stack<RefAsValueType<BclImmutableListNode<T>>>(root._height);
                if (_stack != null)
                {
                    PushLeft(root);
                }
            }

            public T Current
            {
                get
                {
                    if (_current == null)
                    {
                        throw new InvalidOperationException();
                    }

                    return _current._key;
                }
            }

            object? System.Collections.IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_stack is { Count: > 0 } stack)
                {
                    BclImmutableListNode<T> node = stack.Pop().Value!;
                    _current = node;
                    PushLeft(node._right!);
                    return true;
                }

                _current = null;
                return false;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                _stack?.Clear();
                _stack = null;
                _current = null;
            }

            private void PushLeft(BclImmutableListNode<T> node)
            {
                while (!node.IsEmpty)
                {
                    _stack!.Push(new RefAsValueType<BclImmutableListNode<T>>(node));
                    node = node._left!;
                }
            }
        }
    }

    private readonly struct RefAsValueType<TRef>
        where TRef : class
    {
        internal RefAsValueType(TRef? value)
        {
            Value = value;
        }

        internal TRef? Value { get; }
    }

}
