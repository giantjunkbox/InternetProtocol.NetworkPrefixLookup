# Lookup Optimization Log

This log records the work to make the planned generic core as fast as possible. That core is described in
[Abstraction-Study.md](Abstraction-Study.md): a trie over the `IBitKey<TSelf>` key interface, with masks cached in each
node and lookups returning `out` parameters. All code is in [`Prototypes/GenericTrie`](../Prototypes/GenericTrie).

## Method

- **Data.** The benchmark's 96,944 distinct random IPv4 prefixes, stored as `IPNetwork` keys with string values, and
  1,000,000 random IPv4 addresses. The small-table runs use the first 1,000 prefixes plus `0.0.0.0/0`.
- **Correctness.** Every variant was checked against the reference trie for all 1,000,000 keys before it was timed.
  Run `verify-opt` to repeat the checks.
- **Quick timings.** While iterating, each variant ran in its own process and the fastest of 15 timed runs was kept.
  Run `time <variant>` to repeat one. These numbers steer the work; they are not the final figures.
- **Final timings.** BenchmarkDotNet, one session per comparison, as recorded under [Results](#results).
- **Consumer.** Every lookup reads both the matched prefix and the value, as a real caller would.
- **Machine.** A 4-core Intel Xeon 2.80 GHz cloud container with a 33 MiB L3 cache and 1 MiB of L2 per core, on
  .NET 10.0.13.

## Results

All figures below are BenchmarkDotNet means, with ratios against the first row of the same session.

**Lookup, from precomputed 32-bit keys.** One million lookups against the large table.

| Variant | Time | Ratio |
| --- | --- | --- |
| Baseline: the decided design, `CachedKeyTrie` | 643 ms | 1.00 |
| Children as fields | 503 ms | 0.79 ± 0.13 |
| Node pool | 317 ms | 0.50 ± 0.09 |
| Node pool, breadth-first layout | 242 ms | 0.38 ± 0.07 |
| Node pool, depth-first layout | 210 ms | 0.33 ± 0.06 |
| Depth-first, 12-bit index | 57 ms | 0.09 ± 0.01 |
| Depth-first, 16-bit index | 21 ms | 0.03 |
| Depth-first, 17-bit index | 22 ms | 0.04 ± 0.01 |

The final design looks up a key in about 21 ns, roughly 30 times faster than the baseline. None of the variants
allocates during lookups.

**End to end, from `IPAddress` objects.** One million lookups against the large table, including converting each
address to a key. The optimized rows use the depth-first node pool with a 17-bit index.

| Variant | Time | Ratio | Allocated |
| --- | --- | --- | --- |
| Current library, `NetworkPrefixLookup<string>` | 1,885 ms | 1.00 | 61 MB |
| Optimized, `TryWriteBytes` conversion | 42 ms | 0.02 | 0 |
| Optimized, `Address` property conversion | 29 ms | 0.02 | 0 |
| Optimized, private field conversion | 32 ms | 0.02 | 0 |

The optimized design is 45 to 64 times faster than the current library end to end.

**Building the large table.** Inserting all 96,944 prefixes.

| Variant | Time | Ratio | Allocated | Garbage collections per build |
| --- | --- | --- | --- | --- |
| Current library | 347 ms | 1.00 | 94.6 MB | 5 Gen0, 4 Gen1 |
| Baseline: `CachedKeyTrie` | 83 ms | 0.24 ± 0.02 | 18.5 MB | 1 Gen0, 0.8 Gen1 |
| Node pool | 33 ms | 0.10 ± 0.01 | 20.0 MB | none |
| Node pool, then depth-first relayout | 54 ms | 0.16 ± 0.02 | 28.3 MB | none |
| Node pool, relayout and 17-bit index | 58 ms | 0.17 ± 0.02 | 29.3 MB | none |

The node pool allocates a few large arrays instead of an object per node, so building it triggers no collections. The
depth-first relayout costs about 21 ms and the index about 4 ms on top.

## The steps

Quick timings for 1,000,000 lookups of precomputed 32-bit keys against the large table, in the order the steps were
taken. Each row includes every earlier step that was kept.

| Step | Change | Time | Kept |
| --- | --- | --- | --- |
| 0 | Baseline: the decided design, `CachedKeyTrie` | 620 ms | — |
| 1 | Children as two fields instead of a separate array | 455 ms | yes |
| 2 | Node pool: one struct array, integer child indexes, 24-byte nodes, values in a separate array | 399 ms | yes |
| 3 | Lay nodes out breadth-first | 271 ms | no, depth-first is better |
| 4 | Lay nodes out depth-first | 223 ms | yes |
| 5 | Direct-indexed table over the first 16 bits | 32 ms | yes |
| 6 | Branch-free child selection and best-match tracking | 24 ms | yes |
| 7 | Sentinel node, so each step has one branch | 20 to 23 ms | yes |
| 8 | Prefix and value stored side by side | 20 to 21 ms | yes, no measurable change |

### 1. Children as fields

The baseline keeps each node's two children in a separate two-element array. Every step of the walk therefore loads
the node and then the array. Moving the children into the node removes one dependent memory load per step.

### 2. Node pool

Nodes became structs in one array, and children became 32-bit indexes into it. An IPv4 node is 24 bytes: the bits, the
mask and the split bit, the two child indexes, and a value index. Prefixes and values live in a separate array that the
walk never touches. The whole IPv4 trie of 172,930 nodes takes 4.1 MB, against roughly 24 MB for the class-based
baseline, and building it allocates far less.

### 3 and 4. Node order

Nodes are created in insertion order, which scatters a path across memory. `Relayout` rewrites the array in
breadth-first or depth-first order. Depth-first puts each node's first child right after it and keeps whole subtrees
together, which suited this data better than breadth-first.

### 5. Top-level index

After the depth-first layout, a lookup still visited about 16 nodes. A table with one entry for every value of the
first *k* bits of the key holds the deepest node that those bits alone decide, plus the best match on the way to it. A
lookup starts there instead of at the root. With *k* = 16, a lookup visits 1.14 nodes on average; with *k* = 17,
1.05 nodes.

| Index bits | Table size | Time | Nodes visited per lookup |
| --- | --- | --- | --- |
| none | 0 | 223 ms | 15.87 |
| 8 | 2 KB | 155 ms | |
| 12 | 32 KB | 88 ms | 3.87 |
| 14 | 128 KB | 59 ms | |
| 15 | 256 KB | 37 ms | |
| 16 | 512 KB | 32 ms | 1.14 |
| 17 | 1 MB | 30 ms | 1.05 |
| 18 | 2 MB | 32 ms | 1.02 |
| 20 | 8 MB | 62 ms | |

These timings were taken before the branch-free change. Beyond 17 bits the table stops fitting in the faster caches
and lookups slow down again. The best size tracked the number of nodes: about log2 of the node count minus one, which
is 16 for this table and 10 for the 1,000-prefix table.

### 6. Branch-free selection

The machine code showed that the JIT compiled `condition ? a : b` as a branch in two places: choosing the next child,
and remembering the best match. Both depend on random key bits or on whether a node holds a value, so they
mispredicted about half the time. The fix chooses the child by indexing from the first child field, and selects the
best match with a sign mask. This mattered most once the data fit in cache: the small table went from 14.1 ms to
7.2 ms per million lookups.

### 7. Sentinel node

Each step tested for a missing child and then for containment. Node 0 is now a sentinel with a zero mask and non-zero
bits, so no key ever matches it. A missing child simply points at it, and each step has a single branch.

## What did not help

| Idea | Result | Why |
| --- | --- | --- |
| Interleaved batch lookups, 3 to 16 at a time, with and without prefetching | No faster: 30 to 36 ms against 31 ms with the index, 246 ms against 223 ms without | The processor already overlaps consecutive independent lookups, and the batch bookkeeping costs as much as it saves |
| Index entries naming the next node to test for both values of the next bit | Slower: 25.6 ms against 20.7 ms at 17 bits | Entries are twice as big, and the larger table costs more than the node load it saves |
| Index entries for finished blocks pointing straight at the sentinel | No change | Only 13 to 29% of entries qualify |
| Best match stored in every node, with 4-byte index entries | Unindexed walk 9% faster, indexed lookups no faster | The smaller table did not help, and inserts must push values down the trie |
| A 20-bit index | Twice as slow as 17 bits | The 8 MB table does not fit in cache |

## Small tables

With 1,000 prefixes plus a default route, the baseline took 63 ms per million lookups. The pooled trie with a 12-bit
index took 6.4 to 7.2 ms, about 9 times faster. Without an index it took 60 ms, so the index matters for small tables
too.

## Wider keys

The same steps carry over to 128-bit keys. With the IPv4 data encoded in 128-bit keys, the baseline took 827 ms, the
depth-first node pool 381 ms, and the node pool with a 16-bit index 40 ms.

Real IPv6 tables behave very differently from this encoded data; see [Real routing tables](#real-routing-tables).

## Address conversion

End to end from `IPAddress` objects, the lookup also converts the address to a key. Three conversions were tried. All
three agreed for every one of the 1,000,000 addresses.

| Conversion | End-to-end time | Notes |
| --- | --- | --- |
| `IPAddress.TryWriteBytes` into a stack buffer, then `BinaryPrimitives.ReadUInt32BigEndian` | 42 ms | Public, supported API |
| The `IPAddress.Address` property, byte-swapped | 29 ms | Public but marked obsolete because it only works for IPv4 |
| The private `_addressOrScopeId` field through `UnsafeAccessor` | 32 ms | Depends on a runtime implementation detail |

The `Address` property was the fastest in the authoritative session, about 12 ns per lookup quicker than
`TryWriteBytes`. Reading the private field brought nothing extra. In the quick timings the private field came out
ahead; the difference between those two is within the noise.

## Real routing tables

Random prefixes are a convenient benchmark but not a realistic one. The later work used the CAIDA RouteViews
prefix-to-AS snapshots of 30 September 2025, which list the prefixes announced in the global routing table.
`Prototypes/GenericTrie/download-routeviews.sh` fetches them, and `ROUTEVIEWS_DIR` points the prototype at them. Lookup
keys are 1,000,000 random addresses inside randomly chosen announced prefixes, because uniformly random addresses would
almost never match an IPv6 prefix.

| Table | Prefixes | Most common lengths | Trie nodes |
| --- | --- | --- | --- |
| IPv4 | 1,054,037 | /24 (62%), /22, /23, /21 | 1,928,039 |
| IPv6 | 239,603 | /48 (45%), /32, /40, /44 | 451,326 |

Every variant was checked against the current library on both tables, with no mismatches for any of the 1,000,000 keys.

Real data changes the picture in two ways:

- **The trie no longer fits in cache.** The IPv4 node array alone is 44 MB, more than the 33 MiB L3 cache, so node
  visits go to main memory.
- **A flat index fits real data poorly.** Real IPv4 is clustered: after a 20-bit index a lookup still visits 3.9 nodes,
  against 1.05 for random data. IPv6 is worse: only 66 distinct values occur in the first 16 bits, so a 16-bit index
  leaves tens of thousands of prefixes behind single entries. Lookups still visited 18.9 nodes with a 16-bit index and
  15.6 nodes with a 20-bit index.

## Multi-level index

The multi-level index replaces the flat one. A root table covers the first 16 bits. Wherever an entry's block still
holds more than a threshold number of nodes, the entry points to a sub-table over the next 8 bits instead, and so on
down. Dense regions are indexed deeply; sparse ones cost nothing. Each sub-table level adds one memory load, so it only
pays where it saves several node visits.

Nodes visited per lookup, and the index size:

| Index | Real IPv4 | IPv4 index | Real IPv6 | IPv6 index |
| --- | --- | --- | --- | --- |
| Flat, 16 bits | 7.55 | 0.5 MB | 18.92 | 0.5 MB |
| Flat, 20 bits | 3.87 | 8 MB | 15.61 | 8 MB |
| Multi-level, 8-bit sub-tables, threshold 8 | 1.04 | 36.7 MB | 1.51 | 26.8 MB |
| Multi-level, 8-bit sub-tables, threshold 32 | 1.31 | 24.8 MB | 2.69 | 8.6 MB |
| Multi-level, 4-bit sub-tables, threshold 4 | 1.16 | 14.5 MB | 1.43 | 6.5 MB |
| Multi-level, 4-bit sub-tables, threshold 16 | 2.27 | 6.4 MB | 2.68 | 2.8 MB |

Quick timings for 1,000,000 precomputed keys:

| Variant | Real IPv4 | Real IPv6 |
| --- | --- | --- |
| Baseline: the decided design | 2,190 ms | 1,886 ms |
| Node pool, depth-first, no index | 743 ms | 1,152 ms |
| Flat 20-bit index | 328 to 351 ms | 899 to 940 ms |
| Multi-level, 8-bit sub-tables, threshold 8 | 176 ms | 334 ms |
| Multi-level, 8-bit sub-tables, threshold 32 | 192 ms | 429 ms |
| Multi-level, 8-bit sub-tables, threshold 4 | | 314 ms |
| Multi-level, 4-bit sub-tables, threshold 4 | 208 ms | 479 ms |

Eight-bit sub-tables beat four-bit ones, because there are fewer levels to load. A 20-bit root and 12-bit sub-tables were
both slower. The threshold is the knob between speed and memory.

## Production behaviors

The experimental classes are insert-only and single-threaded. [`SnapshotTrie`](../Prototypes/GenericTrie/SnapshotTrie.cs)
adds what a production core needs, on top of the node pool, branch-free walk, sentinel and multi-level index:

- **Removal.** A value node is demoted with one write. Routing nodes that are no longer needed are unlinked or replaced
  by their only child, each with one write. Removing a routing node's prefix returns false, unlike the current library.
- **Snapshots.** Everything a lookup reads lives in one snapshot object: nodes, lengths, prefixes and values, and the
  index. A reader reads the snapshot reference once. The writer changes the current snapshot only in ways readers
  tolerate: new nodes, values and sub-tables are written completely, then linked with one atomic write. When an array
  is full, the writer copies it, and every array that refers to it, into a new snapshot and publishes it with one write.
- **Index maintenance.** After each change, the writer walks down the index along the changed prefix and rebuilds only
  the deepest entry whose block contains it, or the few sub-table entries the prefix spans. The first version rebuilt
  the whole root entry on every change; on the real IPv6 table that regenerated thousands of sub-tables per insert and
  ran out of memory.
- **Compaction.** Removed nodes and values, and replaced sub-tables, stay in place while readers might still use them.
  `Compact` builds a fresh snapshot with only live data, laid out depth-first, with a rebuilt index, and publishes it
  with one write. An early version published a half-built snapshot when an array grew during compaction; the writer
  now builds privately and publishes once.
- **Bulk loading.** `BeginBulkLoad` stops index maintenance while many prefixes are added, and readers walk from the
  root meanwhile. `EndBulkLoad` compacts and builds the index once.

### Tests

[`Prototypes/GenericTrie.Tests`](../Prototypes/GenericTrie.Tests) has 17 NUnit tests, all passing:

| Test | What it checks |
| --- | --- |
| Lookups match the reference | 1,000,000 lookups against the first prototype, with no index, a flat index and two multi-level shapes |
| Removing prefixes | Removing 20% gives the same tree and lookups as never adding them, before and after compaction |
| Insertion order | Shuffled and reversed inserts give the identical tree |
| Index under churn | 50,000 random inserts and removes, checked every 5,000 against a fresh reference, with a compaction midway |
| Bulk load | Correct during the load without an index, identical after it, and maintained again afterwards |
| Edge cases | Empty trie, duplicates, removing a routing node's prefix, the default route, host routes, exact counts |
| Real IPv4 and IPv6 tables | All 1,000,000 keys against the current library, after removing every tenth prefix, and after compaction |
| Readers during writes | One writer adding, removing and compacting for 15 seconds while three readers check every result |

In the last run of the concurrency test, the writer made 1.84 million changes and 91 compactions while the readers did
104 million lookups. Readers checked that every result contained the key, carried the right value, and was at least as
specific as the best permanent prefix for that key. There were no failures.

Run the tests with:

```sh
ROUTEVIEWS_DIR=<folder> dotnet test Prototypes/GenericTrie.Tests -c Release
```

Without `ROUTEVIEWS_DIR`, the two real-table tests are skipped.

### Write costs

Measured on the real tables, with the multi-level index:

| Operation | Real IPv4, threshold 8 | Real IPv4, threshold 32 | Real IPv6, threshold 8 | Real IPv6, threshold 32 |
| --- | --- | --- | --- | --- |
| Bulk load of the full table, including the index | 1.8 s | 0.7 s | 0.8 s | 0.2 s |
| Inserting one at a time with the index maintained | 18 µs each | 4 µs each | 43 µs each | 6 µs each |
| Removing and re-adding a prefix | 20 µs | 39 µs | 199 µs | 54 µs |
| Compaction after one-at-a-time inserts | 0.8 s | 1.4 s | 0.8 s | 0.1 s |

Updates to the common prefixes are cheap. Updates to short prefixes cost more, because every index entry stores the
best match above it: changing a /32 in IPv6 rewrites every index entry beneath it. Inserting a whole table one prefix at
a time also leaves a lot of replaced sub-tables behind, up to 550 MB before compaction for the full IPv6 table at
threshold 8. That is what bulk loading is for.

## Memory

Memory kept after building, measured as heap growth after full collections. The callers' own key objects are created
beforehand and not counted.

| Design | Random IPv4, 96,944 prefixes | Real IPv4, 1,054,037 prefixes | Real IPv6, 239,603 prefixes |
| --- | --- | --- | --- |
| Current library | 37.0 MB, 400 bytes per prefix | 415.6 MB, 413 | 106.2 MB, 465 |
| Baseline: the decided design | 18.5 MB, 200 | 191.2 MB, 190 | 65.4 MB, 286 |
| Node pool, trimmed, no index | 6.8 MB, 74 | 75.6 MB, 75 | 34.8 MB, 152 |
| Node pool, trimmed, flat index | 7.3 MB, 79 (16-bit) | 83.6 MB, 83 (20-bit) | 42.8 MB, 187 (20-bit) |
| `SnapshotTrie`, multi-level, threshold 8 | | 113.2 MB, 113 | 64.0 MB, 280 |
| `SnapshotTrie`, multi-level, threshold 32 | | 99.8 MB, 99 | 43.5 MB, 190 |

The optimized design uses 2.5 to 5 times less memory than the current library on every table, while also being much
faster. Breakdown for the trimmed node pool with a 20-bit index on real IPv4:

| Part | Size |
| --- | --- |
| Nodes, 24 bytes each | 44.1 MB |
| Prefix lengths, 4 bytes each; `SnapshotTrie` uses 2 | 7.4 MB |
| Prefixes and values, an `IPNetwork` and an `int` each | 24.1 MB |
| Index | 8.0 MB |

The multi-level index costs more than the flat one: 25 to 37 MB on real IPv4. The threshold controls that trade.

## Results on real tables

The BenchmarkDotNet results for the real tables are being collected and will be added here.

## Recommendations for the production core

1. **Use the node pool.** One struct array per address family, integer child indexes, and prefixes and values in a
   separate array.
2. **Make the walk branch-free.** Index from the first child field, select the best match with a mask, and use a
   sentinel node for missing children.
3. **Use the multi-level index**, with a 16-bit root and 8-bit sub-tables. Expose the threshold as a tuning option, with
   a default between 8 and 32: 8 for speed, 32 for memory and cheaper updates. The flat index is a special case and
   fits real data poorly.
4. **Follow `SnapshotTrie` for writes and concurrency**: one snapshot object per published state, copy-on-growth of the
   arrays that refer to a grown array, rebuilding only the deepest affected index entry, and compaction that builds
   privately and publishes once.
5. **Offer bulk loading** and use it for initial loads.
6. **Compact on a policy**, for example when replaced sub-tables or removed nodes exceed a share of the live size.
   Compaction blocks the writer for 0.1 to 1.4 seconds on the real tables, but never blocks readers.
7. **Convert IPv4 addresses with the `Address` property**, byte-swapped, behind a warning suppression with a comment
   explaining why. It was about 30% faster end to end than `TryWriteBytes`. Use `TryWriteBytes` for IPv6, where
   `Address` throws. Do not read the private field; it is no faster and depends on runtime internals.
8. **Do not add batch lookups or the other ideas** in [What did not help](#what-did-not-help).

## Open issues

- **Updates to short prefixes are expensive** with a deep index, because each entry stores the best match above it.
  Storing the best match in nodes instead moves the same cost into node updates. If short-prefix churn matters, measure
  with a real update stream, such as a day of BGP updates, before choosing.
- **Only one writer at a time.** Multiple writers need an external lock, as the current `Concurrence*` classes do.
- **Memory ordering** relies on `Volatile` reads and writes, which the .NET memory model guarantees on all platforms. The
  stress test ran only on x64; it should also run on ARM64.
