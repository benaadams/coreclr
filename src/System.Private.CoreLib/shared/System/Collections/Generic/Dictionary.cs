// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

using Internal.Runtime.CompilerServices;

namespace System.Collections.Generic
{
    /// <summary>
    /// Used internally to control behavior of insertion into a <see cref="Dictionary{TKey, TValue}"/>.
    /// </summary>
    internal enum InsertionBehavior : byte
    {
        /// <summary>
        /// The default insertion behavior.
        /// </summary>
        None = 0,

        /// <summary>
        /// Specifies that an existing entry with the same key should be overwritten if encountered.
        /// </summary>
        OverwriteExisting = 1,

        /// <summary>
        /// Specifies that if an existing entry with the same key is encountered, an exception should be thrown.
        /// </summary>
        ThrowOnExisting = 2
    }

    [DebuggerTypeProxy(typeof(IDictionaryDebugView<,>))]
    [DebuggerDisplay("Count = {Count}")]
    [Serializable]
    [TypeForwardedFrom("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")]
    public class Dictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDictionary, IReadOnlyDictionary<TKey, TValue>, ISerializable, IDeserializationCallback where TKey : notnull
    {
        private struct Entry
        {
            public uint hashCode;
            // 0-based index of next entry in chain: -1 means end of chain
            // also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
            // so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
            public int next;
            public TKey key;           // Key of entry
            public TValue value;         // Value of entry
        }

        private Array? _buckets;
        private Entry[]? _entries;
        private int _count;
        private int _freeList;
        private int _freeCount;
        private int _version;
        private IEqualityComparer<TKey>? _comparer;
        private KeyCollection? _keys;
        private ValueCollection? _values;
        private const int StartOfFreeList = -3;

        // constants for serialization
        private const string VersionName = "Version"; // Do not rename (binary serialization)
        private const string HashSizeName = "HashSize"; // Do not rename (binary serialization). Must save buckets.Length
        private const string KeyValuePairsName = "KeyValuePairs"; // Do not rename (binary serialization)
        private const string ComparerName = "Comparer"; // Do not rename (binary serialization)

        public Dictionary() : this(0, null) { }

        public Dictionary(int capacity) : this(capacity, null) { }

        public Dictionary(IEqualityComparer<TKey>? comparer) : this(0, comparer) { }

        public Dictionary(int capacity, IEqualityComparer<TKey>? comparer)
        {
            if (capacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            if (capacity > 0) Initialize(capacity);
            if (comparer != EqualityComparer<TKey>.Default)
            {
                _comparer = comparer;
            }

            if (typeof(TKey) == typeof(string) && _comparer == null)
            {
                // To start, move off default comparer for string which is randomised
                _comparer = (IEqualityComparer<TKey>)NonRandomizedStringEqualityComparer.Default;
            }
        }

        public Dictionary(IDictionary<TKey, TValue> dictionary) : this(dictionary, null) { }

        public Dictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey>? comparer) :
            this(dictionary != null ? dictionary.Count : 0, comparer)
        {
            if (dictionary == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
            }

            // It is likely that the passed-in dictionary is Dictionary<TKey,TValue>. When this is the case,
            // avoid the enumerator allocation and overhead by looping through the entries array directly.
            // We only do this when dictionary is Dictionary<TKey,TValue> and not a subclass, to maintain
            // back-compat with subclasses that may have overridden the enumerator behavior.
            if (dictionary.GetType() == typeof(Dictionary<TKey, TValue>))
            {
                Dictionary<TKey, TValue> d = (Dictionary<TKey, TValue>)dictionary;
                int count = d._count;
                Entry[]? entries = d._entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries![i].next >= -1)
                    {
                        Add(entries[i].key, entries[i].value);
                    }
                }
                return;
            }

            foreach (KeyValuePair<TKey, TValue> pair in dictionary)
            {
                Add(pair.Key, pair.Value);
            }
        }

        public Dictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection) : this(collection, null) { }

        public Dictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey>? comparer) :
            this((collection as ICollection<KeyValuePair<TKey, TValue>>)?.Count ?? 0, comparer)
        {
            if (collection == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.collection);
            }

            foreach (KeyValuePair<TKey, TValue> pair in collection)
            {
                Add(pair.Key, pair.Value);
            }
        }

        protected Dictionary(SerializationInfo info, StreamingContext context)
        {
            // We can't do anything with the keys and values until the entire graph has been deserialized
            // and we have a resonable estimate that GetHashCode is not going to fail.  For the time being,
            // we'll just cache this.  The graph is not valid until OnDeserialization has been called.
            HashHelpers.SerializationInfoTable.Add(this, info);
        }

        public IEqualityComparer<TKey> Comparer =>
            (_comparer == null || _comparer is NonRandomizedStringEqualityComparer) ?
                EqualityComparer<TKey>.Default :
                _comparer;

        public int Count => _count - _freeCount;

        public KeyCollection Keys => _keys ??= new KeyCollection(this);

        ICollection<TKey> IDictionary<TKey, TValue>.Keys => _keys ??= new KeyCollection(this);

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _keys ??= new KeyCollection(this);

        public ValueCollection Values => _values ??= new ValueCollection(this);

        ICollection<TValue> IDictionary<TKey, TValue>.Values => _values ??= new ValueCollection(this);

        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _values ??= new ValueCollection(this);

        public TValue this[TKey key]
        {
            get
            {
                ref TValue value = ref FindValue(key);
                if (!Unsafe.IsNullRef(ref value))
                {
                    return value;
                }
                ThrowHelper.ThrowKeyNotFoundException(key);
                return default;
            }
            set
            {
                bool modified = TryInsert(key, value, InsertionBehavior.OverwriteExisting);
                Debug.Assert(modified);
            }
        }

        public void Add(TKey key, TValue value)
        {
            bool modified = TryInsert(key, value, InsertionBehavior.ThrowOnExisting);
            Debug.Assert(modified); // If there was an existing key and the Add failed, an exception will already have been thrown.
        }

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> keyValuePair)
            => Add(keyValuePair.Key, keyValuePair.Value);

        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> keyValuePair)
        {
            ref TValue value = ref FindValue(keyValuePair.Key);
            if (!Unsafe.IsNullRef(ref value) && EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value))
            {
                return true;
            }
            return false;
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> keyValuePair)
        {
            ref TValue value = ref FindValue(keyValuePair.Key);
            if (!Unsafe.IsNullRef(ref value) && EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value))
            {
                Remove(keyValuePair.Key);
                return true;
            }
            return false;
        }

        public void Clear()
        {
            int count = _count;
            if (count > 0)
            {
                Debug.Assert(_buckets != null, "_buckets should be non-null");
                Debug.Assert(_entries != null, "_entries should be non-null");

                Array.Clear(_buckets, 0, _buckets.Length);

                _count = 0;
                _freeList = -1;
                _freeCount = 0;
                Array.Clear(_entries, 0, count);
            }
        }

        public bool ContainsKey(TKey key)
            => !Unsafe.IsNullRef(ref FindValue(key));

        public bool ContainsValue(TValue value)
        {
            Entry[]? entries = _entries;
            if (value == null)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1 && entries[i].value == null) return true;
                }
            }
            else
            {
                if (default(TValue)! != null) // TODO-NULLABLE: default(T) == null warning (https://github.com/dotnet/roslyn/issues/34757)
                {
                    // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                    for (int i = 0; i < _count; i++)
                    {
                        if (entries![i].next >= -1 && EqualityComparer<TValue>.Default.Equals(entries[i].value, value)) return true;
                    }
                }
                else
                {
                    // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                    // https://github.com/dotnet/coreclr/issues/17273
                    // So cache in a local rather than get EqualityComparer per loop iteration
                    EqualityComparer<TValue> defaultComparer = EqualityComparer<TValue>.Default;
                    for (int i = 0; i < _count; i++)
                    {
                        if (entries![i].next >= -1 && defaultComparer.Equals(entries[i].value, value)) return true;
                    }
                }
            }
            return false;
        }

        private void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            if (array == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
            }

            if ((uint)index > (uint)array.Length)
            {
                ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
            }

            if (array.Length - index < Count)
            {
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
            }

            int count = _count;
            Entry[]? entries = _entries;
            for (int i = 0; i < count; i++)
            {
                if (entries![i].next >= -1)
                {
                    array[index++] = new KeyValuePair<TKey, TValue>(entries[i].key, entries[i].value);
                }
            }
        }

        public Enumerator GetEnumerator()
            => new Enumerator(this, Enumerator.KeyValuePair);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
            => new Enumerator(this, Enumerator.KeyValuePair);

        public virtual void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.info);
            }

            info.AddValue(VersionName, _version);
            info.AddValue(ComparerName, _comparer ?? EqualityComparer<TKey>.Default, typeof(IEqualityComparer<TKey>));
            info.AddValue(HashSizeName, _buckets == null ? 0 : _buckets.Length); // This is the length of the bucket array

            if (_buckets != null)
            {
                var array = new KeyValuePair<TKey, TValue>[Count];
                CopyTo(array, 0);
                info.AddValue(KeyValuePairsName, array, typeof(KeyValuePair<TKey, TValue>[]));
            }
        }

        private ref TValue FindValue(TKey key)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            Array? buckets = _buckets;
            ref Entry entry = ref Unsafe.NullRef<Entry>();
            if (buckets != null)
            {
                Debug.Assert(_entries != null, "expected entries to be != null");
                IEqualityComparer<TKey>? comparer = _comparer;
                if (comparer == null)
                {
                    uint hashCode = (uint)key.GetHashCode();
                    uint i = HashHelpers.GetEntryIndex(buckets, hashCode, out _);
                    Entry[]? entries = _entries;
                    uint collisionCount = 0;
                    if (default(TKey)! != null) // TODO-NULLABLE: default(T) == null warning (https://github.com/dotnet/roslyn/issues/34757)
                    {
                        // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic

                        // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                        i--;
                        do
                        {
                            // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                            // Test in if to drop range check for following array access
                            if (i >= (uint)entries.Length)
                            {
                                goto ReturnNotFound;
                            }

                            entry = ref entries[i];
                            if (entry.hashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entry.key, key))
                            {
                                goto ReturnFound;
                            }

                            i = (uint)entry.next;

                            collisionCount++;
                        } while (collisionCount <= (uint)entries.Length);
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        goto ConcurrentOperation;
                    }
                    else
                    {
                        // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                        // https://github.com/dotnet/coreclr/issues/17273
                        // So cache in a local rather than get EqualityComparer per loop iteration
                        EqualityComparer<TKey> defaultComparer = EqualityComparer<TKey>.Default;

                        // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                        i--;
                        do
                        {
                            // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                            // Test in if to drop range check for following array access
                            if (i >= (uint)entries.Length)
                            {
                                goto ReturnNotFound;
                            }

                            entry = ref entries[i];
                            if (entry.hashCode == hashCode && defaultComparer.Equals(entry.key, key))
                            {
                                goto ReturnFound;
                            }

                            i = (uint)entry.next;

                            collisionCount++;
                        } while (collisionCount <= (uint)entries.Length);
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        goto ConcurrentOperation;
                    }
                }
                else
                {
                    uint hashCode = (uint)comparer.GetHashCode(key);
                    uint i = HashHelpers.GetEntryIndex(buckets, hashCode, out _);
                    Entry[]? entries = _entries;
                    uint collisionCount = 0;
                    // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                    i--;
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                        // Test in if to drop range check for following array access
                        if (i >= (uint)entries.Length)
                        {
                            goto ReturnNotFound;
                        }

                        entry = ref entries[i];
                        if (entry.hashCode == hashCode && comparer.Equals(entry.key, key))
                        {
                            goto ReturnFound;
                        }

                        i = (uint)entry.next;

                        collisionCount++;
                    } while (collisionCount <= (uint)entries.Length);
                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    goto ConcurrentOperation;
                }
            }

            goto ReturnNotFound;

        ConcurrentOperation:
            ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
        ReturnFound:
            ref TValue value = ref entry.value;
        Return:
            return ref value;
        ReturnNotFound:
            value = ref Unsafe.NullRef<TValue>();
            goto Return;
        }

        private int Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);

            _freeList = -1;
            _buckets = HashHelpers.CreateBucketArray(size);
            _entries = new Entry[size];

            return size;
        }

        private bool TryInsert(TKey key, TValue value, InsertionBehavior behavior)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (_buckets == null)
            {
                Initialize(0);
            }
            Debug.Assert(_buckets != null);

            Entry[]? entries = _entries;
            Debug.Assert(entries != null, "expected entries to be non-null");

            IEqualityComparer<TKey>? comparer = _comparer;
            uint hashCode = (uint)((comparer == null) ? key.GetHashCode() : comparer.GetHashCode(key));

            uint collisionCount = 0;
            // Value in _buckets is 1-based
            int i = (int)HashHelpers.GetEntryIndex(_buckets, hashCode, out uint bucketIndex) - 1;

            if (comparer == null)
            {
                if (default(TKey)! != null) // TODO-NULLABLE: default(T) == null warning (https://github.com/dotnet/roslyn/issues/34757)
                {
                    // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                    while (true)
                    {
                        // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                        // Test uint in if rather than loop condition to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            break;
                        }

                        if (entries[i].hashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].key, key))
                        {
                            if (behavior == InsertionBehavior.OverwriteExisting)
                            {
                                entries[i].value = value;
                                _version++;
                                return true;
                            }

                            if (behavior == InsertionBehavior.ThrowOnExisting)
                            {
                                ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                            }

                            return false;
                        }

                        i = entries[i].next;

                        collisionCount++;
                        if (collisionCount > (uint)entries.Length)
                        {
                            // The chain of entries forms a loop; which means a concurrent update has happened.
                            // Break out of the loop and throw, rather than looping forever.
                            ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                        }
                    }
                }
                else
                {
                    // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                    // https://github.com/dotnet/coreclr/issues/17273
                    // So cache in a local rather than get EqualityComparer per loop iteration
                    EqualityComparer<TKey> defaultComparer = EqualityComparer<TKey>.Default;
                    while (true)
                    {
                        // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                        // Test uint in if rather than loop condition to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            break;
                        }

                        if (entries[i].hashCode == hashCode && defaultComparer.Equals(entries[i].key, key))
                        {
                            if (behavior == InsertionBehavior.OverwriteExisting)
                            {
                                entries[i].value = value;
                                _version++;
                                return true;
                            }

                            if (behavior == InsertionBehavior.ThrowOnExisting)
                            {
                                ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                            }

                            return false;
                        }

                        i = entries[i].next;

                        collisionCount++;
                        if (collisionCount > (uint)entries.Length)
                        {
                            // The chain of entries forms a loop; which means a concurrent update has happened.
                            // Break out of the loop and throw, rather than looping forever.
                            ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                        }
                    }
                }
            }
            else
            {
                while (true)
                {
                    // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                    // Test uint in if rather than loop condition to drop range check for following array access
                    if ((uint)i >= (uint)entries.Length)
                    {
                        break;
                    }

                    if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                    {
                        if (behavior == InsertionBehavior.OverwriteExisting)
                        {
                            entries[i].value = value;
                            _version++;
                            return true;
                        }

                        if (behavior == InsertionBehavior.ThrowOnExisting)
                        {
                            ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                        }

                        return false;
                    }

                    i = entries[i].next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }

            bool updateFreeList = false;
            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                updateFreeList = true;
                _freeCount--;
            }
            else
            {
                int count = _count;
                if (count == entries.Length)
                {
                    Resize();
                    bucketIndex = HashHelpers.GetBucketIndex(_buckets, hashCode);
                }
                index = count;
                _count = count + 1;
                entries = _entries;
            }

            ref Entry entry = ref entries![index];

            if (updateFreeList)
            {
                Debug.Assert((StartOfFreeList - entries[_freeList].next) >= -1, "shouldn't overflow because `next` cannot underflow");

                _freeList = StartOfFreeList - entries[_freeList].next;
            }
            entry.hashCode = hashCode;
            // Value in _buckets is 1-based
            entry.next = (int)HashHelpers.ExchangeEntryIndex(_buckets, bucketIndex, newEntryIndex: (uint)(index + 1)) - 1;
            entry.key = key;
            entry.value = value;
            _version++;

            // Value types never rehash
            if (default(TKey)! == null && collisionCount > HashHelpers.HashCollisionThreshold && comparer is NonRandomizedStringEqualityComparer) // TODO-NULLABLE: default(T) == null warning (https://github.com/dotnet/roslyn/issues/34757)
            {
                // If we hit the collision threshold we'll need to switch to the comparer which is using randomized string hashing
                // i.e. EqualityComparer<string>.Default.
                _comparer = null;
                Resize(entries.Length, true);
            }

            return true;
        }

        public virtual void OnDeserialization(object? sender)
        {
            HashHelpers.SerializationInfoTable.TryGetValue(this, out SerializationInfo? siInfo);

            if (siInfo == null)
            {
                // We can return immediately if this function is called twice.
                // Note we remove the serialization info from the table at the end of this method.
                return;
            }

            int realVersion = siInfo.GetInt32(VersionName);
            int hashsize = siInfo.GetInt32(HashSizeName);
            _comparer = (IEqualityComparer<TKey>)siInfo.GetValue(ComparerName, typeof(IEqualityComparer<TKey>))!; // When serialized if comparer is null, we use the default.

            if (hashsize != 0)
            {
                Initialize(hashsize);

                KeyValuePair<TKey, TValue>[]? array = (KeyValuePair<TKey, TValue>[]?)
                    siInfo.GetValue(KeyValuePairsName, typeof(KeyValuePair<TKey, TValue>[]));

                if (array == null)
                {
                    ThrowHelper.ThrowSerializationException(ExceptionResource.Serialization_MissingKeys);
                }

                for (int i = 0; i < array.Length; i++)
                {
                    if (array[i].Key == null)
                    {
                        ThrowHelper.ThrowSerializationException(ExceptionResource.Serialization_NullKey);
                    }
                    Add(array[i].Key, array[i].Value);
                }
            }
            else
            {
                _buckets = null;
            }

            _version = realVersion;
            HashHelpers.SerializationInfoTable.Remove(this);
        }

        private void Resize()
            => Resize(HashHelpers.ExpandPrime(_count), false);

        private void Resize(int newSize, bool forceNewHashCodes)
        {
            // Value types never rehash
            Debug.Assert(!forceNewHashCodes || default(TKey)! == null); // TODO-NULLABLE: default(T) == null warning (https://github.com/dotnet/roslyn/issues/34757)
            Debug.Assert(_entries != null, "_entries should be non-null");
            Debug.Assert(newSize >= _entries.Length);

            Array buckets = HashHelpers.CreateBucketArray(newSize);
            Entry[] entries = new Entry[newSize];

            int count = _count;
            Array.Copy(_entries, 0, entries, 0, count);

            if (default(TKey)! == null && forceNewHashCodes) // TODO-NULLABLE: default(T) == null warning (https://github.com/dotnet/roslyn/issues/34757)
            {
                for (int i = 0; i < count; i++)
                {
                    if (entries[i].next >= -1)
                    {
                        Debug.Assert(_comparer == null);
                        entries[i].hashCode = (uint)entries[i].key.GetHashCode();
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (entries[i].next >= -1)
                {
                    // Value in _buckets is 1-based
                    entries[i].next = (int)HashHelpers.ExchangeEntryIndex(buckets!, HashHelpers.GetBucketIndex(buckets!, entries[i].hashCode), newEntryIndex: (uint)(i + 1)) - 1;
                }
            }

            _buckets = buckets;
            _entries = entries;
        }

        // The overload Remove(TKey key, out TValue value) is a copy of this method with one additional
        // statement to copy the value for entry being removed into the output parameter.
        // Code has been intentionally duplicated for performance reasons.
        public bool Remove(TKey key)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            Array? buckets = _buckets;
            Entry[]? entries = _entries;
            if (buckets != null)
            {
                Debug.Assert(entries != null, "entries should be non-null");
                uint collisionCount = 0;
                uint hashCode = (uint)(_comparer?.GetHashCode(key) ?? key.GetHashCode());
                int last = -1;
                // Value in buckets is 1-based
                int i = (int)HashHelpers.GetEntryIndex(buckets, hashCode, out uint bucketIndex) - 1;
                while (i >= 0)
                {
                    ref Entry entry = ref entries[i];

                    if (entry.hashCode == hashCode && (_comparer?.Equals(entry.key, key) ?? EqualityComparer<TKey>.Default.Equals(entry.key, key)))
                    {
                        if (last < 0)
                        {
                            // Value in buckets is 1-based
                            HashHelpers.SetEntryIndex(buckets, bucketIndex, (uint)(entry.next + 1));
                        }
                        else
                        {
                            entries[last].next = entry.next;
                        }

                        Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");

                        entry.next = StartOfFreeList - _freeList;

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                        {
                            entry.key = default!;
                        }
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                        {
                            entry.value = default!;
                        }
                        _freeList = i;
                        _freeCount++;
                        return true;
                    }

                    last = i;
                    i = entry.next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }
            return false;
        }

        // This overload is a copy of the overload Remove(TKey key) with one additional
        // statement to copy the value for entry being removed into the output parameter.
        // Code has been intentionally duplicated for performance reasons.
        public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            Array? buckets = _buckets;
            Entry[]? entries = _entries;
            if (buckets != null)
            {
                Debug.Assert(entries != null, "entries should be non-null");
                uint collisionCount = 0;
                uint hashCode = (uint)(_comparer?.GetHashCode(key) ?? key.GetHashCode());
                int last = -1;
                // Value in buckets is 1-based
                int i = (int)HashHelpers.GetEntryIndex(buckets, hashCode, out uint bucketIndex) - 1;
                while (i >= 0)
                {
                    ref Entry entry = ref entries[i];

                    if (entry.hashCode == hashCode && (_comparer?.Equals(entry.key, key) ?? EqualityComparer<TKey>.Default.Equals(entry.key, key)))
                    {
                        if (last < 0)
                        {
                            // Value in buckets is 1-based
                            HashHelpers.SetEntryIndex(buckets, bucketIndex, (uint)(entry.next + 1));
                        }
                        else
                        {
                            entries[last].next = entry.next;
                        }

                        value = entry.value;

                        Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");

                        entry.next = StartOfFreeList - _freeList;

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                        {
                            entry.key = default!;
                        }
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                        {
                            entry.value = default!;
                        }
                        _freeList = i;
                        _freeCount++;
                        return true;
                    }

                    last = i;
                    i = entry.next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }
            value = default!;
            return false;
        }

        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            ref TValue valRef = ref FindValue(key);
            if (!Unsafe.IsNullRef(ref valRef))
            {
                value = valRef;
                return true;
            }
            value = default!;
            return false;
        }

        public bool TryAdd(TKey key, TValue value)
            => TryInsert(key, value, InsertionBehavior.None);

        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

        void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
            => CopyTo(array, index);

        void ICollection.CopyTo(Array array, int index)
        {
            if (array == null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
            if (array.Rank != 1)
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_RankMultiDimNotSupported);
            if (array.GetLowerBound(0) != 0)
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_NonZeroLowerBound);
            if ((uint)index > (uint)array.Length)
                ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
            if (array.Length - index < Count)
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);

            if (array is KeyValuePair<TKey, TValue>[] pairs)
            {
                CopyTo(pairs, index);
            }
            else if (array is DictionaryEntry[] dictEntryArray)
            {
                Entry[]? entries = _entries;
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1)
                    {
                        dictEntryArray[index++] = new DictionaryEntry(entries[i].key, entries[i].value);
                    }
                }
            }
            else
            {
                object[]? objects = array as object[];
                if (objects == null)
                {
                    ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                }

                try
                {
                    int count = _count;
                    Entry[]? entries = _entries;
                    for (int i = 0; i < count; i++)
                    {
                        if (entries![i].next >= -1)
                        {
                            objects[index++] = new KeyValuePair<TKey, TValue>(entries[i].key, entries[i].value);
                        }
                    }
                }
                catch (ArrayTypeMismatchException)
                {
                    ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
            => new Enumerator(this, Enumerator.KeyValuePair);

        /// <summary>
        /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        public int EnsureCapacity(int capacity)
        {
            if (capacity < 0)
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            int currentCapacity = _entries == null ? 0 : _entries.Length;
            if (currentCapacity >= capacity)
                return currentCapacity;
            _version++;
            if (_buckets == null)
                return Initialize(capacity);
            int newSize = HashHelpers.GetPrime(capacity);
            Resize(newSize, forceNewHashCodes: false);
            return newSize;
        }

        /// <summary>
        /// Sets the capacity of this dictionary to what it would be if it had been originally initialized with all its entries
        ///
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        ///
        /// To allocate minimum size storage array, execute the following statements:
        ///
        /// dictionary.Clear();
        /// dictionary.TrimExcess();
        /// </summary>
        public void TrimExcess()
            => TrimExcess(Count);

        /// <summary>
        /// Sets the capacity of this dictionary to hold up 'capacity' entries without any further expansion of its backing storage
        ///
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        /// </summary>
        public void TrimExcess(int capacity)
        {
            if (capacity < Count)
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            int newSize = HashHelpers.GetPrime(capacity);

            Entry[]? oldEntries = _entries;
            int currentCapacity = oldEntries == null ? 0 : oldEntries.Length;
            if (newSize >= currentCapacity)
                return;

            int oldCount = _count;
            _version++;
            Initialize(newSize);
            Entry[]? entries = _entries;
            Array? buckets = _buckets;
            int count = 0;
            for (int i = 0; i < oldCount; i++)
            {
                uint hashCode = oldEntries![i].hashCode; // At this point, we know we have entries.
                if (oldEntries[i].next >= -1)
                {
                    ref Entry entry = ref entries![count];
                    entry = oldEntries[i];
                    // Value in _buckets is 1-based
                    entry.next = (int)HashHelpers.ExchangeEntryIndex(buckets!, HashHelpers.GetBucketIndex(buckets!, hashCode), newEntryIndex: (uint)(count + 1)) - 1; // If we get here, we have entries, therefore buckets is not null
                    count++;
                }
            }
            _count = count;
            _freeCount = 0;
        }

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        bool IDictionary.IsFixedSize => false;

        bool IDictionary.IsReadOnly => false;

        ICollection IDictionary.Keys => (ICollection)Keys;

        ICollection IDictionary.Values => (ICollection)Values;

        object? IDictionary.this[object key]
        {
            get
            {
                if (IsCompatibleKey(key))
                {
                    ref TValue value = ref FindValue((TKey)key);
                    if (!Unsafe.IsNullRef(ref value))
                    {
                        return value;
                    }
                }
                return null;
            }
            set
            {
                if (key == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }
                ThrowHelper.IfNullAndNullsAreIllegalThenThrow<TValue>(value, ExceptionArgument.value);

                try
                {
                    TKey tempKey = (TKey)key;
                    try
                    {
                        this[tempKey] = (TValue)value!;
                    }
                    catch (InvalidCastException)
                    {
                        ThrowHelper.ThrowWrongValueTypeArgumentException(value, typeof(TValue));
                    }
                }
                catch (InvalidCastException)
                {
                    ThrowHelper.ThrowWrongKeyTypeArgumentException(key, typeof(TKey));
                }
            }
        }

        private static bool IsCompatibleKey(object key)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }
            return key is TKey;
        }

        void IDictionary.Add(object key, object? value)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }
            ThrowHelper.IfNullAndNullsAreIllegalThenThrow<TValue>(value, ExceptionArgument.value);

            try
            {
                TKey tempKey = (TKey)key;

                try
                {
                    Add(tempKey, (TValue)value!);
                }
                catch (InvalidCastException)
                {
                    ThrowHelper.ThrowWrongValueTypeArgumentException(value, typeof(TValue));
                }
            }
            catch (InvalidCastException)
            {
                ThrowHelper.ThrowWrongKeyTypeArgumentException(key, typeof(TKey));
            }
        }

        bool IDictionary.Contains(object key)
        {
            if (IsCompatibleKey(key))
            {
                return ContainsKey((TKey)key);
            }

            return false;
        }

        IDictionaryEnumerator IDictionary.GetEnumerator()
            => new Enumerator(this, Enumerator.DictEntry);

        void IDictionary.Remove(object key)
        {
            if (IsCompatibleKey(key))
            {
                Remove((TKey)key);
            }
        }

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>,
            IDictionaryEnumerator
        {
            private readonly Dictionary<TKey, TValue> _dictionary;
            private readonly int _version;
            private int _index;
            private KeyValuePair<TKey, TValue> _current;
            private readonly int _getEnumeratorRetType;  // What should Enumerator.Current return?

            internal const int DictEntry = 1;
            internal const int KeyValuePair = 2;

            internal Enumerator(Dictionary<TKey, TValue> dictionary, int getEnumeratorRetType)
            {
                _dictionary = dictionary;
                _version = dictionary._version;
                _index = 0;
                _getEnumeratorRetType = getEnumeratorRetType;
                _current = new KeyValuePair<TKey, TValue>();
            }

            public bool MoveNext()
            {
                if (_version != _dictionary._version)
                {
                    ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                }

                // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
                // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
                while ((uint)_index < (uint)_dictionary._count)
                {
                    ref Entry entry = ref _dictionary._entries![_index++];

                    if (entry.next >= -1)
                    {
                        _current = new KeyValuePair<TKey, TValue>(entry.key, entry.value);
                        return true;
                    }
                }

                _index = _dictionary._count + 1;
                _current = new KeyValuePair<TKey, TValue>();
                return false;
            }

            public KeyValuePair<TKey, TValue> Current => _current;

            public void Dispose()
            {
            }

            object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    if (_getEnumeratorRetType == DictEntry)
                    {
                        return new DictionaryEntry(_current.Key, _current.Value);
                    }
                    else
                    {
                        return new KeyValuePair<TKey, TValue>(_current.Key, _current.Value);
                    }
                }
            }

            void IEnumerator.Reset()
            {
                if (_version != _dictionary._version)
                {
                    ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                }

                _index = 0;
                _current = new KeyValuePair<TKey, TValue>();
            }

            DictionaryEntry IDictionaryEnumerator.Entry
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return new DictionaryEntry(_current.Key, _current.Value);
                }
            }

            object IDictionaryEnumerator.Key
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return _current.Key;
                }
            }

            object? IDictionaryEnumerator.Value
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return _current.Value;
                }
            }
        }

        [DebuggerTypeProxy(typeof(DictionaryKeyCollectionDebugView<,>))]
        [DebuggerDisplay("Count = {Count}")]
        public sealed class KeyCollection : ICollection<TKey>, ICollection, IReadOnlyCollection<TKey>
        {
            private readonly Dictionary<TKey, TValue> _dictionary;

            public KeyCollection(Dictionary<TKey, TValue> dictionary)
            {
                if (dictionary == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
                }
                _dictionary = dictionary;
            }

            public Enumerator GetEnumerator()
                => new Enumerator(_dictionary);

            public void CopyTo(TKey[] array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if (index < 0 || index > array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < _dictionary.Count)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
                }

                int count = _dictionary._count;
                Entry[]? entries = _dictionary._entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries![i].next >= -1) array[index++] = entries[i].key;
                }
            }

            public int Count => _dictionary.Count;

            bool ICollection<TKey>.IsReadOnly => true;

            void ICollection<TKey>.Add(TKey item)
                => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);

            void ICollection<TKey>.Clear()
                => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);

            bool ICollection<TKey>.Contains(TKey item)
                => _dictionary.ContainsKey(item);

            bool ICollection<TKey>.Remove(TKey item)
            {
                ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);
                return false;
            }

            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator()
                => new Enumerator(_dictionary);

            IEnumerator IEnumerable.GetEnumerator()
                => new Enumerator(_dictionary);

            void ICollection.CopyTo(Array array, int index)
            {
                if (array == null)
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                if (array.Rank != 1)
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_RankMultiDimNotSupported);
                if (array.GetLowerBound(0) != 0)
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_NonZeroLowerBound);
                if ((uint)index > (uint)array.Length)
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                if (array.Length - index < _dictionary.Count)
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);

                if (array is TKey[] keys)
                {
                    CopyTo(keys, index);
                }
                else
                {
                    object[]? objects = array as object[];
                    if (objects == null)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                    }

                    int count = _dictionary._count;
                    Entry[]? entries = _dictionary._entries;
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (entries![i].next >= -1) objects[index++] = entries[i].key;
                        }
                    }
                    catch (ArrayTypeMismatchException)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                    }
                }
            }

            bool ICollection.IsSynchronized => false;

            object ICollection.SyncRoot => ((ICollection)_dictionary).SyncRoot;

            public struct Enumerator : IEnumerator<TKey>, IEnumerator
            {
                private readonly Dictionary<TKey, TValue> _dictionary;
                private int _index;
                private readonly int _version;
                [AllowNull, MaybeNull] private TKey _currentKey;

                internal Enumerator(Dictionary<TKey, TValue> dictionary)
                {
                    _dictionary = dictionary;
                    _version = dictionary._version;
                    _index = 0;
                    _currentKey = default;
                }

                public void Dispose()
                {
                }

                public bool MoveNext()
                {
                    if (_version != _dictionary._version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    while ((uint)_index < (uint)_dictionary._count)
                    {
                        ref Entry entry = ref _dictionary._entries![_index++];

                        if (entry.next >= -1)
                        {
                            _currentKey = entry.key;
                            return true;
                        }
                    }

                    _index = _dictionary._count + 1;
                    _currentKey = default;
                    return false;
                }

                public TKey Current => _currentKey!;

                object? IEnumerator.Current
                {
                    get
                    {
                        if (_index == 0 || (_index == _dictionary._count + 1))
                        {
                            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                        }

                        return _currentKey;
                    }
                }

                void IEnumerator.Reset()
                {
                    if (_version != _dictionary._version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    _index = 0;
                    _currentKey = default;
                }
            }
        }

        [DebuggerTypeProxy(typeof(DictionaryValueCollectionDebugView<,>))]
        [DebuggerDisplay("Count = {Count}")]
        public sealed class ValueCollection : ICollection<TValue>, ICollection, IReadOnlyCollection<TValue>
        {
            private readonly Dictionary<TKey, TValue> _dictionary;

            public ValueCollection(Dictionary<TKey, TValue> dictionary)
            {
                if (dictionary == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
                }
                _dictionary = dictionary;
            }

            public Enumerator GetEnumerator()
                => new Enumerator(_dictionary);

            public void CopyTo(TValue[] array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if ((uint)index > array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < _dictionary.Count)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
                }

                int count = _dictionary._count;
                Entry[]? entries = _dictionary._entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries![i].next >= -1) array[index++] = entries[i].value;
                }
            }

            public int Count => _dictionary.Count;

            bool ICollection<TValue>.IsReadOnly => true;

            void ICollection<TValue>.Add(TValue item)
                => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);

            bool ICollection<TValue>.Remove(TValue item)
            {
                ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);
                return false;
            }

            void ICollection<TValue>.Clear()
                => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);

            bool ICollection<TValue>.Contains(TValue item)
                => _dictionary.ContainsValue(item);

            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
                => new Enumerator(_dictionary);

            IEnumerator IEnumerable.GetEnumerator()
                => new Enumerator(_dictionary);

            void ICollection.CopyTo(Array array, int index)
            {
                if (array == null)
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                if (array.Rank != 1)
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_RankMultiDimNotSupported);
                if (array.GetLowerBound(0) != 0)
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_NonZeroLowerBound);
                if ((uint)index > (uint)array.Length)
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                if (array.Length - index < _dictionary.Count)
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);

                if (array is TValue[] values)
                {
                    CopyTo(values, index);
                }
                else
                {
                    object[]? objects = array as object[];
                    if (objects == null)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                    }

                    int count = _dictionary._count;
                    Entry[]? entries = _dictionary._entries;
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (entries![i].next >= -1) objects[index++] = entries[i].value!;
                        }
                    }
                    catch (ArrayTypeMismatchException)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_InvalidArrayType();
                    }
                }
            }

            bool ICollection.IsSynchronized => false;

            object ICollection.SyncRoot => ((ICollection)_dictionary).SyncRoot;

            public struct Enumerator : IEnumerator<TValue>, IEnumerator
            {
                private readonly Dictionary<TKey, TValue> _dictionary;
                private int _index;
                private readonly int _version;
                [AllowNull, MaybeNull] private TValue _currentValue;

                internal Enumerator(Dictionary<TKey, TValue> dictionary)
                {
                    _dictionary = dictionary;
                    _version = dictionary._version;
                    _index = 0;
                    _currentValue = default;
                }

                public void Dispose()
                {
                }

                public bool MoveNext()
                {
                    if (_version != _dictionary._version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    while ((uint)_index < (uint)_dictionary._count)
                    {
                        ref Entry entry = ref _dictionary._entries![_index++];

                        if (entry.next >= -1)
                        {
                            _currentValue = entry.value;
                            return true;
                        }
                    }
                    _index = _dictionary._count + 1;
                    _currentValue = default;
                    return false;
                }

                public TValue Current => _currentValue!;

                object? IEnumerator.Current
                {
                    get
                    {
                        if (_index == 0 || (_index == _dictionary._count + 1))
                        {
                            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                        }

                        return _currentValue;
                    }
                }

                void IEnumerator.Reset()
                {
                    if (_version != _dictionary._version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }
                    _index = 0;
                    _currentValue = default;
                }
            }
        }
    }
}
