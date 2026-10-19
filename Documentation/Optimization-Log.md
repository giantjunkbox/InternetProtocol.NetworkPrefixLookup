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

The BenchmarkDotNet results are being collected and will be added here.

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

Real IPv6 tables are a different matter. Most IPv6 prefixes fall under `2000::/3`, so an index over the first 16 bits
would put almost every prefix behind a handful of entries. IPv6 needs its own measurements with realistic data, for
example a full BGP table, before the index design is settled for it.

## Address conversion

End to end from `IPAddress` objects, the lookup also converts the address to a key. Three conversions were tried. All
three agreed for every one of the 1,000,000 addresses.

| Conversion | Notes |
| --- | --- |
| `IPAddress.TryWriteBytes` into a stack buffer, then `BinaryPrimitives.ReadUInt32BigEndian` | Public, supported API |
| The `IPAddress.Address` property, byte-swapped | Public but marked obsolete because it only works for IPv4 |
| The private `_addressOrScopeId` field through `UnsafeAccessor` | Fastest, but depends on a runtime implementation detail |

## Recommendations for the production core

1. **Use the node pool.** One struct array per address family, integer child indexes, and prefixes and values in a
   separate array.
2. **Make the walk branch-free.** Index from the first child field, select the best match with a mask, and use a
   sentinel node for missing children.
3. **Lay nodes out depth-first** after bulk loads, through an explicit method such as `Optimize()`, or automatically
   once enough inserts have accumulated.
4. **Add the top-level index**, sized from the node count: about log2(nodes) minus one, between 8 and 17 bits for IPv4.
5. **Convert addresses with `TryWriteBytes`.** The `Address` property is a reasonable alternative for IPv4. The private
   field is not worth the fragility.
6. **Do not add batch lookups or the other ideas** in [What did not help](#what-did-not-help).

## Production concerns

The prototype is insert-only and single-threaded. The production core has to keep the rule that readers never lock:

- **Growing the node array.** Copy the array, then publish the new one with a single reference write. Readers that
  still hold the old array see a consistent, slightly older trie.
- **Adding nodes.** Write the new node completely, then link it with one 32-bit write of the parent's child index,
  which is atomic.
- **Removing nodes.** Do not reuse a freed slot while a reader might still reach it. Reclaim slots only when a relayout
  builds a new array.
- **Relayout.** It already builds new arrays, so it can be published with one reference write.
- **The index.** Entries are 8 bytes and can be written atomically. An index refers to positions in one particular node
  array, so the node array and the index should be published together, for example in one snapshot object. Rebuilding
  a 17-bit index visits at most a few million nodes, a few milliseconds. Updating it incrementally is possible: a new
  prefix of length *L* no longer than *k* touches 2^(*k* − *L*) entries.
