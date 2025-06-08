using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Acaly.Collections
{
    //Advantages:
    //* Comparable single-thread performance on fast path to Dictionary.
    //* Comparable concurrent look-up performance on fast path to ConcurrentDictionary.
    //* Provide reference access, allowing lock-free initializing with CompareExchange.
    public class ConcurrentLookupTable<TKey, TValue>
        where TKey : notnull
    {
        private struct Entry
        {
            public volatile uint HashCode;
            public volatile uint Next;

            public TKey Key;
            public TValue Value;
        }

        private readonly Entry[] _mainEntries;
        private readonly uint _bucketCount;
        private readonly ulong _mainEntriesMultiplier;
        private int _nextFreeEntry;

        //Lazy initialization.
        private RefDictionary<TKey, TValue>? _fallbackDictionary;

        public ConcurrentLookupTable(int mainCapacity, int bucketCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);
            if (mainCapacity < bucketCount)
            {
                throw new ArgumentOutOfRangeException(nameof(mainCapacity),
                    "capacity cannot be smaller than bucket count");
            }

            _mainEntries = new Entry[mainCapacity];
            _bucketCount = (uint)bucketCount;
            _nextFreeEntry = bucketCount;
            _mainEntriesMultiplier = HashHelpers.GetFastModMultiplier((uint)_bucketCount);
        }

        //This class internally uses zero HashCode to indicate an empty entry.
        //Hash codes equal to zero is changed to one before usage.
        private static uint GetHashCode(TKey key)
        {
            var hash = (uint)key.GetHashCode();
            return hash == 0 ? 1 : hash;
        }

        public TValue this[TKey key]
        {
            get
            {
                ref var r = ref GetValueRefOrNullRef(key);
                if (Unsafe.IsNullRef(ref r))
                {
                    throw new KeyNotFoundException();
                }
                return r;
            }
            set
            {
                ref var r = ref GetValueRefOrAddValue(key, value, out var exists);
                if (exists)
                {
                    r = value;
                }
            }
        }

        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            var mainEntries = _mainEntries;
            var hash = GetHashCode(key);
            Debug.Assert(hash != 0);
            var entryIndex = HashHelpers.FastMod(hash, _bucketCount, _mainEntriesMultiplier);
            while (true)
            {
                Debug.Assert(entryIndex >= 0 && entryIndex < mainEntries.Length);
                ref var entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mainEntries), entryIndex);
                var entryHashCode = entry.HashCode;
                if (entryHashCode == hash &&
                    EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                {
                    value = entry.Value;
                    return true;
                }
                else if (entryHashCode == 0)
                {
                    value = default;
                    return false;
                }
                else
                {
                    var entryNext = entry.Next;
                    if (entryNext != 0)
                    {
                        if ((uint)entryNext >= (uint)mainEntries.Length)
                        {
                            throw new IndexOutOfRangeException();
                        }
                        entryIndex = entryNext;
                        continue;
                    }
                    ref var rval = ref FindFallback(key, ref entry);
                    if (Unsafe.IsNullRef(ref rval))
                    {
                        value = default;
                        return false;
                    }
                    else
                    {
                        value = rval;
                        return true;
                    }
                }
            }
        }

        public ref TValue GetValueRefOrNullRef(TKey key)
        {
            var mainEntries = _mainEntries;
            var hash = GetHashCode(key);
            Debug.Assert(hash != 0);
            var entryIndex = HashHelpers.FastMod(hash, _bucketCount, _mainEntriesMultiplier);
            while (true)
            {
                Debug.Assert(entryIndex >= 0 && entryIndex < mainEntries.Length);
                ref var entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mainEntries), entryIndex);
                var entryHashCode = entry.HashCode;
                if (entryHashCode == hash &&
                    EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                {
                    return ref entry.Value;
                }
                else if (entryHashCode == 0)
                {
                    return ref Unsafe.NullRef<TValue>();
                }
                else
                {
                    var entryNext = entry.Next;
                    if (entryNext != 0)
                    {
                        if ((uint)entryNext >= (uint)mainEntries.Length)
                        {
                            throw new IndexOutOfRangeException();
                        }
                        entryIndex = entryNext;
                        continue;
                    }
                    return ref FindFallback(key, ref entry);
                }
            }
        }

        public TValue GetOrAdd(TKey key, TValue value)
        {
            return GetValueRefOrAddValue(key, value, out _);
        }

        public ref TValue GetValueRefOrAddDefault(TKey key)
        {
            return ref GetValueRefOrAddValue(key, default!, out _);
        }

        public ref TValue GetValueRefOrAddValue(TKey key, TValue value, out bool exists)
        {
            var mainEntries = _mainEntries;
            var hash = GetHashCode(key);
            Debug.Assert(hash != 0);
            var entryIndex = HashHelpers.FastMod(hash, _bucketCount, _mainEntriesMultiplier);
            ref var entry = ref Unsafe.NullRef<Entry>();
            while (true)
            {
                Debug.Assert(entryIndex >= 0 && entryIndex < mainEntries.Length);
                entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mainEntries), entryIndex);
                var entryHashCode = entry.HashCode;
                if (entryHashCode == hash &&
                    EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                {
                    exists = true;
                    return ref entry.Value;
                }
                else if (entryHashCode == 0)
                {
                    break;
                }
                else
                {
                    var entryNext = entry.Next;
                    if (entryNext != 0)
                    {
                        Debug.Assert(entryNext >= _bucketCount);
                        if ((uint)entryNext >= (uint)mainEntries.Length)
                        {
                            throw new IndexOutOfRangeException();
                        }
                        entryIndex = entryNext;
                        continue;
                    }
                    break;
                }
            }
            return ref GetOrAddLocked(key, static (_, v) => v, value, ref entry, hash, out exists);
        }

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> func)
        {
            var mainEntries = _mainEntries;
            var hash = GetHashCode(key);
            Debug.Assert(hash != 0);
            var entryIndex = HashHelpers.FastMod(hash, _bucketCount, _mainEntriesMultiplier);
            ref var entry = ref Unsafe.NullRef<Entry>();
            while (true)
            {
                Debug.Assert(entryIndex >= 0 && entryIndex < mainEntries.Length);
                entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mainEntries), entryIndex);
                var entryHashCode = entry.HashCode;
                if (entryHashCode == hash &&
                    EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                {
                    return entry.Value;
                }
                else if (entryHashCode == 0)
                {
                    break;
                }
                else
                {
                    var entryNext = entry.Next;
                    if (entryNext != 0)
                    {
                        Debug.Assert(entryNext >= _bucketCount);
                        if ((uint)entryNext >= (uint)mainEntries.Length)
                        {
                            throw new IndexOutOfRangeException();
                        }
                        entryIndex = entryNext;
                        continue;
                    }
                    break;
                }
            }
            return GetOrAddLocked(key, static (k, f) => f(k), func, ref entry, hash, out _);
        }

        public TValue GetOrAdd<TArg>(TKey key, Func<TKey, TArg, TValue> func, TArg arg)
        {
            var mainEntries = _mainEntries;
            var hash = GetHashCode(key);
            Debug.Assert(hash != 0);
            var entryIndex = HashHelpers.FastMod(hash, _bucketCount, _mainEntriesMultiplier);
            ref var entry = ref Unsafe.NullRef<Entry>();
            while (true)
            {
                Debug.Assert(entryIndex >= 0 && entryIndex < mainEntries.Length);
                entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mainEntries), entryIndex);
                var entryHashCode = entry.HashCode;
                if (entryHashCode == hash &&
                    EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                {
                    return entry.Value;
                }
                else if (entryHashCode == 0)
                {
                    break;
                }
                else
                {
                    var entryNext = entry.Next;
                    if (entryNext != 0)
                    {
                        Debug.Assert(entryNext >= _bucketCount);
                        if ((uint)entryNext >= (uint)mainEntries.Length)
                        {
                            throw new IndexOutOfRangeException();
                        }
                        entryIndex = entryNext;
                        continue;
                    }
                    break;
                }
            }
            return GetOrAddLocked(key, func, arg, ref entry, hash, out _);
        }

        private ref TValue GetOrAddLocked<TArg>(TKey key, Func<TKey, TArg, TValue> func, TArg arg,
            ref Entry entry, uint hash, out bool exists)
        {
            var mainEntries = _mainEntries;

            //Up to here, we have checked all main entries in the bucket.
            //Either one value exists in fallback dictionary, or we need to add one.
            //If there does not exist one, we want to calculate the value now, before entering the lock.

            //However, we want to check the fallback dictionary first.
            //Otherwise, we may waste one instance in each look-up.
            //This only happens when the main entries are full.

            if (_nextFreeEntry == mainEntries.Length)
            {
                lock (mainEntries)
                {
                    ref TValue find = ref FindFallbackNoLock(key);
                    if (!Unsafe.IsNullRef(ref find))
                    {
                        exists = true;
                        return ref find;
                    }
                }
            }

            //Now we can calculate the value.
            var value = func(key, arg);

            lock (mainEntries)
            {
                //Check again along the linked list after acquiring the lock.
                var entryHashCode = entry.HashCode;
                if (entryHashCode != 0)
                {
                    while (true)
                    {
                        Debug.Assert(entryHashCode != 0);
                        if (entryHashCode == hash &&
                            EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                        {
                            exists = true;
                            return ref entry.Value;
                        }
                        else
                        {
                            var entryNext = entry.Next;
                            if (entryNext != 0)
                            {
                                Debug.Assert(entryNext >= _bucketCount);
                                entry = ref _mainEntries[entryNext];
                                entryHashCode = entry.HashCode;
                                continue;
                            }

                            var nextIndex = _nextFreeEntry;
                            if (nextIndex >= _mainEntries.Length)
                            {
                                return ref GetOrAddFallbackNoLock(key, value, out exists);
                            }
                            _nextFreeEntry = nextIndex + 1;
                            ref var nextEntry = ref _mainEntries[nextIndex];

                            nextEntry.Key = key;
                            nextEntry.Value = value;
                            nextEntry.HashCode = hash;

                            entry.Next = (uint)nextIndex;
                            exists = false;
                            break;
                        }
                    }
                }
                else
                {
                    entry.Key = key;
                    entry.Value = value;
                    entry.HashCode = hash;
                    exists = false;
                }
            }
            return ref entry.Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ref TValue FindFallback(TKey key, ref Entry entry)
        {
            var mainEntries = _mainEntries;
            var hash = GetHashCode(key);
            Debug.Assert(hash != 0);

            //First check _nextFreeEntry in this fallback path.
            //This is necessary to decide whether we need to check fallback dictionary.
            //The check can be done without the lock,
            //but we don't want to put it in caller, which must be kept simple.
            //Note that it's possible that entry.Next is no longer zero this time.
            //In that case we have to go through another loop, similar to caller.

            var nextIndex = _nextFreeEntry; //Read before volatile.
            var entryNext = entry.Next;

            if (entryNext != 0)
            {
                //Check entries (added just now from other threads).

                Debug.Assert(entryNext >= _bucketCount);
                if ((uint)entryNext >= (uint)mainEntries.Length)
                {
                    throw new IndexOutOfRangeException();
                }
                entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mainEntries), entryNext);

                while (true)
                {
                    var entryHashCode = entry.HashCode;
                    if (entryHashCode == hash &&
                        EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                    {
                        return ref entry.Value;
                    }
                    else if (entryHashCode == 0)
                    {
                        return ref Unsafe.NullRef<TValue>();
                    }
                    else
                    {
                        nextIndex = _nextFreeEntry; //Read before volatile.
                        entryNext = entry.Next;
                        if (entryNext != 0)
                        {
                            if ((uint)entryNext >= (uint)mainEntries.Length)
                            {
                                throw new IndexOutOfRangeException();
                            }
                            entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mainEntries), entryNext);
                            continue;
                        }

                        //entry.Next is zero again.
                        //This time we read the _nextFreeEntry before entry.Next.
                    }
                }
            }

            if (nextIndex < mainEntries.Length)
            {
                //entry.Next is zero, while _nextFreeEntry is less than mainEntries.Length.
                //This indicates that the linked list is end, and fallback dictionary is not used yet.
                return ref Unsafe.NullRef<TValue>();
            }

            lock (mainEntries)
            {
                return ref FindFallbackNoLock(key);
            }
        }

        private ref TValue FindFallbackNoLock(TKey key)
        {
            var dict = _fallbackDictionary;
            if (dict is null)
            {
                RefDictionary<TKey, TValue> newDict = new();
                dict = Interlocked.CompareExchange(ref _fallbackDictionary, newDict, null) ?? newDict;
            }
            return ref dict.Find(key);
        }

        private ref TValue GetOrAddFallbackNoLock(TKey key, TValue value, out bool exists)
        {
            var dict = _fallbackDictionary;
            if (dict is null)
            {
                RefDictionary<TKey, TValue> newDict = new();
                dict = Interlocked.CompareExchange(ref _fallbackDictionary, newDict, null) ?? newDict;
            }
            return ref dict.GetOrAdd(key, value, out exists);
        }
    }
}
