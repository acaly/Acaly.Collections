## Acaly.Collections

Some random collection of containers, which might be useful to someone.

### ConcurrentLookupTable

This is a `ConcurrentDictionary`-like container, but the modification is limited to inserting (no Remove or Clear).

* On the fast path, look-ups are lock-free and very fast,
comparable to `Dictionary` on single-thread case and `ConcurrentDictionary` on multi-thread case.
* Inserting operations are linearized with lock (`Monitor`).
* Much lower memory usage compared with `ConcurrentDictionary`, especially when number of elements is small.
* Provide reference accesses similar to `CollectionsMarshal`, allowing more flexible operations from the caller side.
* There is no automatic resizing, so you should have a good estimation for the element count when creating the container,
or ensure the most frequently accessed items are inserted first.
* When a new item cannot find a place within the size limit, it will be put on the fallback `Dictionary`,
which is always accessed with a lock, and will not benefit from the performance advantage.

The most common usage is as a global cache of key-value pairs of known size. 

#### Benchmarks

BenchmarkDotNet v0.15.0, Windows 11 (10.0.26100.4061/24H2/2024Update/HudsonValley)

.NET 9.0.4 (9.0.425.16305), X64 RyuJIT AVX2

TryGetValue, single-thread and multi-thread.

| Method                   | Mean      | Error     | StdDev    | Code Size |
|------------------------- |----------:|----------:|----------:|----------:|
| ConcurrentLookupTable    |  1.174 ns | 0.0100 ns | 0.0094 ns |     246 B |
| Dictionary               |  1.326 ns | 0.0114 ns | 0.0107 ns |     373 B |
| LockedDictionary         | 11.335 ns | 0.0548 ns | 0.0486 ns |     488 B |
| ConcurrentDictionary     |  1.190 ns | 0.0144 ns | 0.0135 ns |     327 B |
| ConcurrentLookupTable_MT |  1.197 ns | 0.0118 ns | 0.0110 ns |     246 B |
| LockedDictionary_MT      | 29.547 ns | 0.5748 ns | 0.6388 ns |     488 B |
| ConcurrentDictionary_MT  |  1.359 ns | 0.0087 ns | 0.0081 ns |     327 B |

Note: LockedDictionary is a normal `Dictionary` wrapped with `lock`, which is the only way to use it in multi-thread case.
