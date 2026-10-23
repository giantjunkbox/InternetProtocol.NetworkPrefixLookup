# Architecture

This document describes how version 3 of `Bitvantage.InternetProtocol.NetworkPrefixLookup` stores prefixes and answers
longest-prefix-match queries. It is written for people who want to change the internals. For usage see the
[README](../README.md); for the design history see [Abstraction-Study.md](Abstraction-Study.md),
[Bart-Comparison.md](Bart-Comparison.md) and [Interface.md](Interface.md). Version 2 is described in
[Architecture-v2.md](Architecture-v2.md).

## Contents

1. [Purpose and packages](#purpose-and-packages)
2. [Repository layout](#repository-layout)
3. [Layers](#layers)
4. [Keys](#keys)
5. [The read-write trie](#the-read-write-trie)
6. [Writes, batches and readers](#writes-batches-and-readers)
7. [The frozen trie](#the-frozen-trie)
8. [The IP layer](#the-ip-layer)
9. [Adapter packages](#adapter-packages)
10. [Diagnostics](#diagnostics)
11. [Performance](#performance)
12. [Testing and benchmarking](#testing-and-benchmarking)
13. [Limitations](#limitations)

## Purpose and packages

The library answers one question quickly: given a key such as an IP address, which stored prefix is the most specific
one that contains it? It can also return every stored prefix that contains a key, find the longest stored prefix that
contains a given prefix, and look prefixes up exactly.

| Package | Contents | Depends on |
| --- | --- | --- |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup` | The keys, the core tries, the IP layer, and `IPNetworkLookup` for `System.Net.IPNetwork` | Nothing |
| `….NetworkPrefixLookup.IPNetwork2` | `IPNetwork2Lookup<TValue>` | `IPNetwork2` |
| `….NetworkPrefixLookup.MaxMind` | `MaxMindNetworkLookup<TValue>` and `MaxMindDatabase.Load` | `MaxMind.Db` |

[Adapter-Survey.md](Adapter-Survey.md) explains why these libraries get packages. Ranges with arbitrary ends, such as
`192.168.0.10-192.168.0.20`, are left to a separate library. All projects target .NET 10 only.

## Repository layout

| Path | Contents |
| --- | --- |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup/Keys/` | `IBitKey<TSelf>`, `Bits32` and `Bits128` |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup/Core/` | `BitTrie`, `FrozenBitTrie`, `IReadOnlyBitTrie`, `BitSet256` and the ART tables, `PrefixEntry`, `NoValue` |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup/IP/` | The adapter interface, `IPAddressBits`, `IPPrefixLookup` and its frozen form, the internal `IPLookupReader` they share, `IPNetworkLookup` |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.*/` | One adapter package each |
| `Test/` | NUnit tests: `Core/`, `IP/`, `Adapters/`, and MaxMind test databases in `Data/` |
| `PerformanceTest/` | BenchmarkDotNet benchmarks and a memory report |
| `Prototypes/GenericTrie/` | The prototypes the design was measured with; not part of the solution |

The read-only members of `IPPrefixLookup` and `FrozenIPPrefixLookup` are written once, in the internal
`IPLookupReader`; see [The IP layer](#the-ip-layer).

## Layers

| Layer | Types | Knows about |
| --- | --- | --- |
| IP layer | `IPPrefixLookup`, `IPNetworkLookup`, the adapter packages, their frozen forms | `IPAddress`, address families, the caller's prefix type |
| Core | `BitTrie<TKey, TPrefix, TValue>`, `FrozenBitTrie<TKey, TPrefix, TValue>` | Bits and lengths only |
| Keys | `IBitKey<TSelf>`, `Bits32` and `Bits128` | Bit arithmetic for one fixed width |

The core stores a *prefix object* and a *value* with each prefix. The prefix object is whatever the caller wants back
from a lookup: the IP layer stores the caller's `IPNetwork`, `IPNetwork2` or MaxMind `Network`. Routing tables and the core
never create prefix objects, so diagnostics take a formatter for bits and lengths instead.

Both forms of every layer implement one read-only interface, `IReadOnlyBitTrie` in the core and
`IReadOnlyIPPrefixLookup` in the IP layer, so code that only reads accepts either. Lookups return the prefix and value
through `out` parameters, as decided in [Abstraction-Study.md](Abstraction-Study.md).

## Keys

A key is a fixed-width bit string, left-aligned: bit 0 is the most significant bit. `IBitKey<TSelf>` has only static
members, so each key type is a struct the JIT compiles into plain instructions. Widths must be multiples of 8, because the trie
consumes 8 bits per level, from 8 to 128, so that a path fits in a fixed stack of 17 nodes. The library ships `Bits32`
for IPv4 and `Bits128` for IPv6, and those are the only key types. The interface's members are internal, so no other
assembly can implement it: `BitSet256` and the frozen tables index with `GetBits` without bounds checks, and a key type
whose bits strayed out of range would corrupt memory. The test project defines `Bits8`, `Bits16` and two special keys
through `InternalsVisibleTo`; `Bits16` relies on the default `GetTopBits`.

Keys are not the place for context such as a VRF. The context would have to come before the address, costing a trie
level per 8 bits and spending the shortcut table and the frozen root on bits that barely vary. Use one lookup per
context instead.

Operations past the end of the key read zeros. The frozen tables rely on that: a last table narrower than the others
reads padding bits as zeros.

## The read-write trie

`BitTrie` follows Go's BART (Balanced ART): a multibit trie with 8-bit strides, where each node keeps its contents in
popcount-compressed arrays indexed through 256-bit sets.

### A node

```text
Node
  BitSet256 Prefixes   PrefixEntries[]   prefixes that end inside this node's stride, relative length 0 to 7
  BitSet256 Fringes    FringeEntries[]   prefixes that end exactly at the end of the stride, relative length 8
  BitSet256 Kids       KidNodes[]        child nodes, one per octet that leads to two or more longer prefixes
  BitSet256 Leaves     LeafEntries[]     lone longer prefixes, stored whole with their bits, mask and length
  int Count, int Stamp, byte Shared      writer bookkeeping
```

A node at depth `d` covers key bits `8d` to `8d + 7`. An array holds one element per set bit, in bit order, so the
element for bit `b` is at `Rank(b) - 1`, where `Rank` counts the set bits at or below `b` with four popcounts.

**Prefixes inside a stride** use Knuth's ART base indexes: the prefixes of relative length 0 to 7 form a complete
binary tree numbered 1 to 255, where index `i` has children `2i` and `2i + 1`. The longest prefix inside a stride that
contains octet `o` is index `(o >> 1) + 128`. `ArtTables.Ancestors[i]` is the set of `i` and all its ancestors, so the
longest stored prefix containing an octet is the highest set bit of `Prefixes AND Ancestors[(o >> 1) + 128]`: one AND
and a leading-zero count per word.

**Fringes** are prefixes of relative length 8: they cover one whole octet. Storing them in the parent, as BART does,
saves a node per fringe.

**Leaves** are path compression. A prefix longer than the stride whose octet leads to no other prefix is stored
whole in a leaf, instead of in a chain of nodes. A lookup that reaches a leaf checks the key against it with one masked
comparison.

### The canonical shape

The shape depends only on the set of stored prefixes, never on the order of writes:

- A child node exists at an octet exactly when two or more stored prefixes lie below that octet, longer than the
  stride.
- A single prefix below an octet is a leaf.
- Prefixes of relative length 0 to 7 are base indexes, and length 8 is a fringe, in the node of their stride.

Insertion turns a leaf into a child node when a second prefix arrives below its octet. Removal reverses it: a child
left with one prefix becomes a leaf in its parent, an empty child disappears, and the check repeats upward. The tests
compare `ToTextTree` output after shuffled, reversed and batched insertion, and after removal against never adding.

### Lookup

`Find` walks down while the key's next octet has a child node. If the last node has a leaf at the key's octet and the
leaf matches, that is the answer. Otherwise it backtracks from the deepest node up: in each node a fringe at the key's
octet beats any prefix inside the stride, then the `Prefixes AND Ancestors` test finds the longest base index. The
first hit is the longest match.

Nodes have no parent links, because a copy-on-write node can belong to several versions, so a backtrack needs the
path. The walk keeps only the node one level up, in a local. On the real tables the deepest node answers 98 to 100% of
lookups and its parent nearly all the rest; on the random table 0.8% of lookups need to go further. Those walk again
from the root, pushing each node on a stack (an inline array of 17 nodes), and backtrack through it. Keeping the stack
on every lookup cost a write barrier call per node, because the JIT cannot prove that a store into the inline array at
a variable index goes to the stack; without it lookups take 0.85 to 0.93 of the time, as measured under
[Since the baseline](#since-the-baseline). The retry around `Find` covers both walks.

For keys of 16 bits or more, **shortcut blocks** let a lookup skip the first two levels. There is one block for each
first octet that leads to a node at depth 1; it holds that node and, for each second octet, the node at depth 2 below
it, or null. A 256-entry array points at the blocks. A block is built, filled, when its depth-1 node appears and dropped
when the node goes, so a trie holds blocks only where they save work: an empty trie holds none, the real IPv4 table 215
(435 KB) and the real IPv6 table 6 (12 KB). A write that replaces a depth-1 node updates its block's 256 entries in place; a write at
depth 2 updates one entry. No write builds more than one block, so there is no threshold and no large build.

An earlier version used one flat table of 65,536 slots, allocated with every trie. In one session the blocks made real
IPv4 lookups 0.91 of the flat table's time, IPv6 0.94 and the random table 0.83, because the blocks take less cache.

The other queries reuse the walk:

| Query | How |
| --- | --- |
| `TryGetMatch(bits, length)` | As `Find`, but only prefixes no longer than `length` count, so the walk stops descending at the node that holds `length`. It starts from a shortcut block when the prefix is longer than 8 or 16 bits and keeps only the parent, as `FindOnce` does; a deeper backtrack walks again with the whole path |
| `TryGetValue`, `ContainsPrefix` | Descend to the node of the prefix's stride and test one base index, fringe or leaf |
| `GetMatches` | Collects on the way down to the node that holds `length`, or as far as a key leads: in each node the base indexes on the path, the ancestors of `i`, shortest first, then the fringe, and below the last node a leaf. Reversing them at the end gives longest first, so the walk keeps no path |
| `Overlaps(bits, length)` | Two prefixes overlap exactly when one contains the other. On the way down to the node of the prefix's stride, a prefix or fringe on the path contains it, and a leaf decides alone. In that node, with `i` the prefix's base index, three 256-bit tests decide: the node's prefixes against `ArtTables.Ancestors[i]` and `Descendants[i]`, and its fringes, leaves and children against `ArtTables.Octets[i]`, the octets that `i` covers |
| `GetSubnets(bits, length)` | Descends as `Overlaps` does, then enumerates that node with its prefixes limited to `Descendants[i]` and its octets to `Octets[i]`; below those octets everything counts. A leaf on the way is the only candidate. One iterator yields the entries without computing their bits, and allocates its stack only when it goes below the first node |
| Enumeration | `GetSubnets` with length 0: depth-first, in address order, at each octet the base indexes that start there, shortest first, then the fringe, the leaf and the child. `Freeze` reads the same walk with each prefix's bits |

## Writes, batches and readers

### Copy-on-write

A write never changes a node that readers can reach, with one exception: storing a child reference in a parent's
`KidNodes` array, which is a single reference write. To change a node, the writer copies it (`MemberwiseClone`, so the
copy shares the four arrays), changes the copy, and links the copy into the parent with `Volatile.Write`. The garbage
collector frees the replaced node once no reader holds it, so there is no compaction, no growth step and no free list.
Every write therefore does bounded work: one node and one array of at most 256 elements copied at the level that
changes, and one more node per level when a canonical collapse moves a prefix up. The number of levels is at most the
key width divided by 8, whatever the table's size.

Two fields make the copying cheap:

- **`Stamp`** names the write or batch that created a node. A node whose stamp is the current one is private to the
  writer and changes in place. Every single write and every batch takes a new stamp.
- **`Shared`** has one bit per array. A copied node starts with all four set, because it shares its arrays with the
  original. In a private node, an array marked shared is replaced by an exact-size copy before its first change, and
  an array that is not shared changes in place, growing by doubling. A node that is not private is never changed, so
  its bits do not matter until the next copy sets them all again.

`Count` on each node is the number of prefixes in its subtree, used by removal to find nodes that must collapse. Only
the writer reads it; the root's count is the trie's `Count`. A single write adjusts the counts of the published nodes
above it after it links the change in, so `Count` never runs ahead of what lookups find.

### What readers see

`_published` holds an object with the root and the block array. Readers read it once per call. A single write outside a
batch updates the root reference or a child reference, and then the blocks, in place. A batch commit and `Clear` build a
new root and a new block array privately and publish both with one reference write.

- A lookup that overlaps one write sees the trie as it was before or after it. A write changes one prefix and touches
  one path, so a reader that combines a newer block entry with an older root, or the reverse, still gets an answer that
  was correct before or after that write. A new block is filled before it is linked in, and a block always holds its
  depth-1 node.
- A lookup that overlaps two or more writes could see a state that never existed. A copied node shares its child array
  with the original, and a write stores a replaced child into the published parent's array in place, so a reader still
  holding the original parent from before one write sees the child from a later one. With a writer that adds a covering
  /8 and then removes a /16 inside it, such a reader can miss both. So `_changes` counts writes: each write outside a
  batch, each commit and each `Clear` raises it to odd before its first visible store and to even after its last.
  Every lookup reads it before and after, behind a read barrier, and looks again when the two readings show that two
  or more writes overlapped it, that is when the second exceeds the first, rounded down to even, by more than 2. A
  lookup therefore sees the trie as it was at one moment during the call. Readers still take no locks. They are
  lock-free rather than wait-free, like Linux's seqlocks: a lookup retries when a write finishes and another begins
  while it runs. At ordinary update rates that takes a reader thread paused in the middle of a lookup. Under a writer
  running flat out, the chance is about a lookup's time over a write's, so on the real tables, with lookups of 80 to
  200 ns and writes of about 0.8 µs, roughly one lookup in five to ten retries once, and few twice; each retry costs
  about one lookup. On x86 the check is two loads and a compare, and measured free on the real tables; see
  [Performance](#measured-baseline). `Version` is `_changes` halved, and must be read before the lookups it is to
  vouch for.
- A batch is all or nothing. Readers keep seeing the published root and blocks until the commit replaces both at once.
- Enumeration and `GetSubnets` during writes see each entry whole, but are not a snapshot of the whole trie at one
  instant. `GetMatches` and `Overlaps` retry as lookups do.

### The writer lock

Writes take an internal `System.Threading.Lock`, so there is no separate concurrent class. The lock is re-entrant,
which batches rely on: `BeginBatch` holds it until `Commit` or `Dispose`, and writes made in the batch enter it again.
A batch must therefore end on the thread that started it.

### Batches

During a batch the writer works on `_w`, a root that readers cannot see. A change copies every node on its path up to
that root, because changing a published parent in place would be visible. Subtree counts are adjusted only after those
copies exist, so an abandoned batch leaves the published nodes untouched. `Commit` trims the private nodes' arrays to
exact size, marks them shared, builds a new block array and publishes. A batch copies every node above a change, so a
first octet whose depth-1 node is still the same object has nothing changed below it: its block is reused, and only the
blocks under changed first octets are rebuilt. `Dispose` without `Commit` points `_w`
back at the published root.

### Building from a sequence

The constructors that take a sequence build the trie in one pass instead of adding each prefix in a batch. They copy
the items into a buffer, one per address family in the IP layer, sized exactly when the input is a collection, and
build each node once, with arrays of the exact size: no descent from the root per item, no doubling, no copying at a
commit. The pass is a most-significant-digit radix sort by octet that builds the trie as it partitions, in place and
without comparisons, so input order does not matter. For each node, the items that end in it, its prefixes and fringes,
go first; the longer ones are moved into one run per octet (an American flag sort, skipped when they are already in
octet order, as items in address order are), and a run of one becomes a leaf and a longer run a child node, which is
exactly the canonical shape. Two items with the same prefix end up at the same base index or octet of one node, which
reports the duplicate. A node visits only the octets it occupies, with its counts kept per depth in one scratch array:
visiting all 256 octets in every node made the first version slower than a batch on the IPv6 table, whose nodes mostly
hold a handful of prefixes. Built nodes are stamped 0 and share their arrays, as after a batch commit, so later writes
copy them. The tests check that the result has the same shape and statistics as a trie built a prefix at a time or in
a batch, for every key width and for sorted, shuffled and reversed input.

`MaxMindDatabase.Load` still adds each network in a batch, keeping the first of any duplicates, and decoding the records
costs more than the trie.

`Version` counts published changes: one per write outside a batch, one per commit, one per `Clear`.

## The frozen trie

`BitTrie.Freeze` builds a `FrozenBitTrie`, read-only, with lookup tables that would be too costly to keep current
under writes. It holds no nodes:

| Array | Contents |
| --- | --- |
| `_entries` | The prefix object and value of each prefix, in address order |
| `_values` | A dense copy of the values, for `TryGetMatchValue`, unless `ValueOnlyLookups` is off |
| `_keys` | Each prefix's bits, for `GetSubnets` and `Overlaps`: 4 bytes per IPv4 prefix and 16 per IPv6 prefix |
| `_lengths` | Each prefix's length |
| `_parents` | For each prefix, the nearest shorter stored prefix that contains it, or -1 |
| `_root` and `_tables`, or `_root`, `_nodes` and `_answers` | The lookup tables of one of two layouts |

A table entry is an entry index + 1, 0 for no match, or, with the top bit set, the start of a child table or the index
of a compressed node.

**Leaf-pushed**: a root table over the first 16 bits and child tables of 8 bits wherever a block still
holds a longer prefix. Every other entry holds the final answer for its whole block, so a lookup reads only tables.
`RootBits` and `TableBits` choose other shapes, such as 24/8 for IPv4.

**Compressed**, after Poptrie: a root over the first 18 bits of a full routing table, then nodes that each decide 6
bits. A node is two 64-bit
masks and two offsets: one mask marks the slots that lead to a child node, the other the slots where a run of equal
answers starts. A popcount up to the slot finds the child or the answer.

**Choosing the layout.** Unless `FrozenBitTrieOptions.Layout` names one, the builder counts the child tables the
leaf-pushed layout would need, which it must do anyway to allocate them in one array, and takes the compressed layout
when they would hold more than 256 entries per prefix and more than 4,194,304 entries in all, 16 MB, or more than an
array can hold. `FrozenBitTrie.Layout` reports the choice. The rule comes from building both layouts of eleven tables
on 2026-10-22, with a million lookups at addresses inside the stored prefixes (Stopwatch, best of five rounds; the
table's own child-table entries per prefix in parentheses):

| Table | Leaf-pushed | Compressed | Chosen |
| --- | --- | --- | --- |
| Real IPv4, 1,054,037 prefixes (6.9) | 47 MB, 26 ns | 25 MB, 28 ns | Leaf-pushed |
| Real IPv6, 239,598 prefixes (55) | 58 MB, 80 ns | 9 MB, 53 ns | Leaf-pushed |
| Random IPv4 /24, 100,000 (132) | 55 MB, 14.5 ns | 6 MB, 16.2 ns | Leaf-pushed |
| Small IPv4, every 1,054th real prefix (250) | 1.3 MB, 4.4 ns | 0.1 MB, 8.8 ns | Leaf-pushed |
| Small IPv6, every 240th real prefix (498) | 2.3 MB, 19 ns | 0.2 MB, 28 ns | Leaf-pushed |
| Random IPv4, the version 2 data, 96,944 (276) | 109 MB, 44 ns | 7 MB, 28 ns | Compressed |
| Random IPv4 /32, 100,000 (387) | 157 MB, 58 ns | 11 MB, 40 ns | Compressed |
| Random IPv6 /48 in 2000::/3, 100,000 (783) | 315 MB, 177 ns | 16 MB, 67 ns | Compressed |
| Random IPv6 /64, 100,000 (1,411) | 566 MB, 450 ns | 25 MB, 258 ns | Compressed |
| Random IPv6 /128, 100,000 (3,459) | 1.39 GB, 1,207 ns | 56 MB, 418 ns | Compressed |

Above 256 entries per prefix the compressed layout was smaller and faster on every table that did not fit in the
cache; the small tables, which do, stayed faster leaf-pushed, hence the 16 MB floor. Real IPv6 is the one table where
this quick probe and BenchmarkDotNet disagree: `RealTableBenchmarks` measured leaf-pushed about 8% faster in four
sessions the same day, so it stays leaf-pushed. The rule depends on `TableBits`, because wider child tables hold more
entries.

**Root size.** Unless `RootBits` is set, the root follows the number of prefixes, because a fixed root costs 256 KB
(leaf-pushed) or 1 MB (compressed) even in an empty table. Measured on random and real IPv4 prefixes:

- Leaf-pushed tables get 8 bits below 32 prefixes and 16 bits from there. Their size is smallest with a root that is a
  multiple of the 8-bit child tables, but at 64 random prefixes the 8-bit root made lookups 2.5 times slower (5 ns
  against 2 ns) to save 207 of 367 KB, so only nearly empty tables, such as an unused address family, get it.
- Compressed tables get about 16 root entries per prefix, from 8 bits up to 18. That was as fast or faster than the
  full root and took a fraction of the memory: 45 KB against 1.07 MB at 256 random prefixes, 161 KB against 1.13 MB at
  1,000, and 606 KB against 1.36 MB at 4,000.

**Building** reads the entries in address order, where a containing prefix always comes before the prefixes inside
it. For a block of keys, `FillAnswers` writes each entry that ends within the next table's bits over the slots it
covers; because of that order, a later entry that overlaps an earlier one lies inside it and rightly wins. The entries
longer than that form one contiguous run per slot (`Runs`), and each run becomes a child table or node, built the same
way. The `_parents` links come from one pass with a stack of open containing prefixes.

**Queries without nodes.** Every stored prefix that contains a prefix also contains its first address, so it lies on
the parent chain from that address's longest match:

- `GetMatches` follows the chain from the longest match, and given a prefix, from the entry `TryGetMatch` finds.
- `TryGetMatch(bits, length)` follows it to the first entry no longer than `length`.
- `TryGetValue` and `ContainsPrefix` do the same and check that the length is equal.

The prefixes inside a prefix are one run of entries, because the entries are in address order with a shorter prefix
first: those that contain the prefix and start at its address are shorter, so they come before the run. `GetSubnets`
finds the run's start by a binary search over `_keys` and `_lengths` and reads on while the entries lie inside.
`Overlaps` first takes the longest match of the prefix's first address: a stored prefix that contains that address
either contains the prefix or lies inside it. Failing that, a prefix inside it starts at a later address, and the binary
search's first entry is it. Without `_keys` neither query has a way in, because the lookup tables hold only answers, and
a prefix shadowed by longer ones answers no address at all. Keeping them costs 4.0 MB on the real IPv4 table and 3.7 MB
on the real IPv6 table: 6.6% more for leaf-pushed IPv4 and 30% more for compressed IPv6, the smallest form.

Freezing holds the source trie's writer lock while it copies the prefixes out, so writes wait for that part; the
tables are built from the copy after the lock is released. Readers of the source are not affected.

## The IP layer

`IPPrefixLookup<TPrefix, TAdapter, TValue>` holds a `BitTrie<Bits32, …>` for IPv4 and a `BitTrie<Bits128, …>` for IPv6
and routes each call by family. `TAdapter` is a struct implementing `IIPPrefixAdapter<TPrefix>`, whose static members
read a prefix's family, network bits and length. Generic code over a struct is specialized by the JIT, so reading the
caller's type costs no indirection.

**One copy of the read-only members.** The read-write and frozen lookups answer the same queries from different trie
types. Their read-only members are written once, in the internal struct `IPLookupReader<TPrefix, TAdapter, TValue,
TForm>`. Each public class holds one in a field and forwards each member to it in one line, so no infrastructure type
is public. The reader holds each family's trie through `IReadOnlyBitTrie`, but never calls through it, which would be
an interface call that does not inline. `TForm` is an empty internal struct, `ReadWriteForm` or `FrozenForm`. Each
call into a trie tests `typeof(TForm)` and casts the trie to that form's sealed class with `Unsafe.As`. The JIT
compiles generic code separately for each struct type argument and resolves the test as it compiles, so each form
keeps only its direct, inlinable call. The reader's members are `AggressiveInlining`, so each forwarder compiles to
what its own copy of the member would.

The shared code first lived in a public base class, which, measured against the duplicated classes it replaced in one
session with old and new back to back, was as fast or faster in all five cases: IPv4 and IPv6 from integers,
read-write and frozen, and IPv4 from `IPAddress` with a reference-type prefix, whose generic code the runtime shares
across reference types. The reader replaced the base class so that no infrastructure type is public. The JIT's output
for each public lookup is the base class's, with the reader and the trie's `TryGetMatch` inlined: the read-write form
calls only `BitTrie.Find` and the frozen form only `FrozenBitTrie.FindLeafPushed` and `FindCompressed`. Each forwarder
adds two instructions, a null check of the lookup and the offset of the reader field, which do not show in the timings:
in four alternating runs of `LayerBenchmarks`, the IP layer cost 0.5% to 6% over the bare trie with the base class and
2.5% to 5% with the reader, and in one run each of `RealTableBenchmarks` the ten cases ranged from 15% faster to 5%
slower, inside the container's drift.

- **Addresses** convert without allocating: IPv4 through the obsolete `IPAddress.Address` property, which was about
  30% faster end to end than `TryWriteBytes`, and IPv6 through `TryWriteBytes` into a stack buffer. The span, `uint` and
  `UInt128` overloads skip `IPAddress` entirely. `IPAddressBits` exposes the conversions for adapter authors; its public
  conversions check the address family, and the lookups use unchecked copies after sorting the address by family.
- **IPv4-mapped addresses** such as `::ffff:10.1.2.3` are looked up as the IPv4 address they hold when
  `IPLookupOptions.MapIPv4MappedAddresses` is on, so that they always get the same answer as the IPv4 address. It is off
  by default: the main use cases are routing tables and packet classification, where every address should be looked up
  as given and a mapped address on the wire is malformed or crafted (RFC 4942, section 2.2). Turn it on for addresses
  from dual-mode sockets. MaxMind lookups turn it on by default, because MaxMind's own reader answers mapped addresses
  from the IPv4 networks. The queries about networks, `TryGetMatch`, `GetMatches`, `GetSubnets` and `Overlaps`, follow
  the address lookups, so that they agree with them: an IPv6 network inside `::ffff:0:0/96` becomes the IPv4 network
  it holds, and a network that contains all of `::ffff:0:0/96` also contains every IPv4 prefix, which `GetSubnets`
  lists first, as enumeration does, and `Overlaps` counts. Writes and the exact lookups take prefixes as given, so with
  mapping on an IPv6 prefix inside `::ffff:0:0/96` could never answer an address lookup; adding one throws
  `ArgumentException`, before anything changes, so a batch it was written in still commits. The check costs one test
  of a flag on IPv6 lookups only.
- **Batches** begin a batch on both tries. On commit each family publishes atomically, IPv4 first.
- **Freezing** copies both families' prefixes out under both tries' writer locks, taken in the order batches take
  them, so the frozen copy is the lookup at one moment even though a reader of the read-write lookup can see one
  family's batch a moment before the other's. Both families' tables are built after the locks are released.
- **`Version`** is the sum of the two tries' versions, which changes whenever either does.
- **Concrete classes** such as `IPNetworkLookup<TValue>` fix the prefix and adapter types, and redeclare `Freeze` with
  `new` to return their own frozen class. A frozen class's protected constructor takes the read-write lookup, so
  adapter packages can define their own pairs.
- Null prefixes are rejected without boxing struct prefixes.

## Adapter packages

Each package defines an adapter struct and a read-write and frozen class pair. Two need more than that:

- **Sets without values** need no package: `NoValueLookupExtensions` in the core adds `TryAdd(prefix)`, `Add(prefix)`
  and `TryGetMatch(address, out prefix)` to any lookup whose values are `NoValue`, such as `IPNetworkLookup<NoValue>`.
  `ContainsMatch` is a member of both read-only interfaces. A `NetworkPrefix` package once kept version 2's value-less
  `NetworkPrefixLookup`; it was dropped with `NetworkPrefix` itself.
- **MaxMind** adds `MaxMindDatabase.Load<T>`, which reads every network through `Reader.FindAll<T>` into one batch.
  MaxMind's IPv6 databases keep the IPv4 networks under `::/96`, which `FindAll` reports as IPv4, and point
  `::ffff:0:0/96`, `2001::/32` and `2002::/16` at them, so a full walk returns every IPv4 network four times. The loader
  skips the three aliases by default, and the lookup it returns maps IPv4-mapped addresses, so `::ffff:` addresses still
  find their IPv4 network without a second copy of it. `IncludeAliasedNetworks` loads the aliases and stores each IPv4
  network once more under `::/96`, for exact agreement with `Reader.Find` at every address, which the tests check at
  every network boundary of two MaxMind test databases, in IPv4, IPv4-compatible and IPv4-mapped form. It skips the
  `::ffff:0:0/96` alias when the lookup maps those addresses: such a lookup refuses prefixes there, and answers the
  addresses from the IPv4 networks, as the alias would.

## Diagnostics

`ToTextTree(format)` prints one line per base-index prefix, fringe, leaf and child node, indented by depth, with each
node's prefix count. Equal text means equal shape, which the tests use to check the canonical shape. The IP layer's
`ToTextTree(AddressFamily)` formats bits as addresses:

```text
prefix 0.0.0.0/0
fringe 10.0.0.0/8
node 10.0.0.0/8, 3 prefixes
  fringe 10.1.0.0/16
  node 10.1.0.0/16, 2 prefixes
    fringe 10.1.1.0/24
    fringe 10.1.2.0/24
leaf 192.168.0.0/16
```

`ToDotTree` writes Graphviz DOT. `GetStatistics` counts prefixes and nodes and estimates bytes; `FrozenBitTrie.Bytes`
sums its arrays.

## Performance

### Measured baseline

All numbers come from a 4-core cloud container (Intel Xeon, 2.8 GHz) on .NET 10.0.13 with BenchmarkDotNet 0.15.8, on
2026-10-19 and 20. Lookups allocate nothing in either form. Timings in this container drift by about 10% between
sessions, so compare ratios within a table rather than times across tables.

The random-table and comparison benchmarks below stored version 2's `NetworkPrefix` objects, through an adapter
package that has since been dropped. They now store `System.Net.IPNetwork` values, which are 8 bytes larger per entry
than a reference: the random table's read-write trie reports 9.4 MB instead of 8.6 MB.

**Against the original library.** `PerformanceTest.V2` runs the library as it was before the redesign, at commit
`b512201`, on the same tables as `ComparisonBenchmarks`: `NetworkPrefix` prefixes with `int` values in both, through
the adapter package of the time, and
1,000,000 lookups from `IPAddress` objects. The small tables are every 1,054th prefix of the real IPv4 table and every
240th of the real IPv6 table. The original ran on .NET 7, as it shipped, and on .NET 10; version 3 runs on .NET 10.
The three sessions ran back to back on 2026-10-20.

| Lookups, ms per million | Original, .NET 7 | Original, .NET 10 | Version 3 | Version 3, frozen | Faster than the original on .NET 7 |
| --- | --- | --- | --- | --- | --- |
| Small IPv4, 1,000 prefixes | 187 | 218 | 20 | 13 | 9.2 times, 14 frozen |
| Small IPv6, 1,000 prefixes | 227 | 242 | 50 | 44 | 4.5 times, 5.2 frozen |
| Random IPv4, 96,944 prefixes | 1,294 | 1,722 | 120 | 70 | 11 times, 18 frozen |
| Real IPv4, 1,054,037 prefixes | 3,315 | 3,396 | 220 | 141 | 15 times, 24 frozen |
| Real IPv6, 239,603 prefixes | 2,330 | 3,425 | 503 | 388 | 4.6 times, 6.0 frozen |

The original allocates 64 bytes per IPv4 lookup and 104 per IPv6 lookup; version 3 allocates nothing. IPv6 gains least:
its prefixes are deeper, converting a 16-byte address costs more, and `NetworkPrefix` is a class, so reading the
returned prefix's length is a cache miss in both versions.

| Building, one prefix at a time | Original, .NET 7 | Original, .NET 10 | Version 3 | Version 3, one batch |
| --- | --- | --- | --- | --- |
| Small IPv4 | 0.44 ms | 0.38 ms | 0.80 ms | 0.42 ms |
| Small IPv6 | 0.72 ms | 0.58 ms | 0.51 ms | 0.27 ms |
| Random IPv4 | 162 ms | 390 ms | 222 ms | 37 ms |
| Real IPv4 | 496 ms, 1,044 MB allocated | 1,987 ms | 484 ms, 858 MB allocated | 201 ms, 71 MB |
| Real IPv6 | 165 ms | 464 ms | 153 ms | 69 ms |

On .NET 7 the original builds as fast as version 3 adding one prefix at a time on the real tables, and faster on the
small IPv4 and random tables, because each copy-on-write insert allocates. A batch is 2.4 to 4.4 times faster than the
original on .NET 7, and ties it on the small IPv4 table. On .NET 10 the original builds 2.3 to 4 times slower than on
.NET 7, because of garbage collection, so version 3 builds faster there on every table but small IPv4.

| Memory held | Original | Version 3 | Version 3, frozen leaf-pushed | Version 3, frozen compressed |
| --- | --- | --- | --- | --- |
| Small IPv4 | 0.4 MB | 0.4 MB | 1.2 MB | 0.1 MB |
| Small IPv6 | 0.5 MB | 0.1 MB | 2.2 MB | 0.2 MB |
| Random IPv4 | 37.0 MB | 8.7 MB | 104.7 MB | 7.6 MB |
| Real IPv4 | 415.6 MB | 22.3 MB | 52.9 MB | 32.2 MB |
| Real IPv6 | 106.2 MB | 11.8 MB | 56.6 MB | 10.3 MB |

Memory is heap growth after full collections, not counting the callers' `NetworkPrefix` objects; the original held the
same on both runtimes. The read-write trie holds 5 to 19 times less than the original on the larger tables and the same
on the small IPv4 table. Leaf-pushed frozen tables hold more than the original on small and scattered tables; compressed
ones hold less everywhere.

**The version 2 benchmarks.** `RandomTableBenchmarks` repeats the benchmarks of version 2 on the same seeded data:
96,944 distinct random IPv4 prefixes and 1,000,000 random addresses. Version 2 cannot run in the same process, because
its assembly has the same name, so its row comes from its own `PerformanceTest`, run about an hour earlier.

| Benchmark | Version 2 | Version 3, read-write | Version 3, frozen |
| --- | --- | --- | --- |
| 1,000,000 lookups by `IPAddress` | 1,405 ms, 61 MB allocated | 154 ms, nothing allocated | 88 ms leaf-pushed |
| 1,000,000 lookups by `uint` | | 119 ms | 56 ms leaf-pushed |
| 100,000 adds, one at a time | 427 ms, 95 MB allocated | 434 ms, 217 MB allocated | |
| 100,000 adds, one batch | | 79 ms, 21 MB allocated | |
| Freezing | | | 193 ms, 111 MB allocated |
| Memory held | 37.0 MB, earlier session | 9.1 MB | 105.3 MB leaf-pushed, 8.2 MB compressed |

Scattered long prefixes, as in this random data, give the leaf-pushed layout a child table under almost every root
entry, so its tables outgrow the cache. In a separate session the compressed layout was faster as well as smaller here:
0.36 of the read-write time against 0.48 for leaf-pushed. Real routing tables are clustered and favor leaf-pushed.

**Real tables.** `RealTableBenchmarks`: one million lookups from precomputed keys, random addresses inside randomly
chosen announced prefixes of the CAIDA RouteViews tables of 2025-09-30. Version 2 is from
[Optimization-Log.md](Optimization-Log.md#results-on-real-tables), an earlier session.

| Lookup | Real IPv4, 1,054,037 prefixes | Ratio | Real IPv6, 239,603 prefixes | Ratio |
| --- | --- | --- | --- | --- |
| Version 2, by `IPAddress` | 3,466 ms | | 2,632 ms | |
| Read-write, by `uint` or `UInt128` | 170 ms | 1.00 | 368 ms | 1.00 |
| Read-write, by `IPAddress` | 227 ms | 1.34 | 417 ms | 1.14 |
| Frozen, leaf-pushed 16/8 | 94 ms | 0.56 | 213 ms | 0.59 |
| Frozen, leaf-pushed, value only | 74 ms | 0.44 | 162 ms | 0.45 |
| Frozen, compressed 18 | 133 ms | 0.79 | 280 ms | 0.77 |

The `IPAddress` rows read a million separate `IPAddress` objects, so much of their extra time is cache misses on those
objects rather than conversion.

**Building the real tables** (`RealBuildBenchmarks`):

| Build | Real IPv4 | Real IPv6 |
| --- | --- | --- |
| Version 2, one at a time, earlier session | 3,246 ms, 966 MB allocated | 700 ms, 244 MB |
| One at a time | 885 ms, 1,108 MB allocated | |
| One batch | 301 ms, 100 MB allocated | 107 ms, 35 MB |
| Freezing, leaf-pushed | 220 ms | |
| Freezing, compressed | 240 ms | |

**Memory held** (`--memory`, heap growth after full collections, not counting the callers' prefix objects):

| Form | Real IPv4 | Real IPv6 |
| --- | --- | --- |
| Version 2, earlier session | 415.6 MB | 106.2 MB |
| Read-write, 25,083 and 33,330 nodes | 31.9 MB | 15.6 MB |
| Frozen, leaf-pushed 16/8 | 61.2 MB | 58.7 MB |
| Frozen, compressed 18 | 40.5 MB | 12.3 MB |

Frozen tables have since kept each prefix's bits, for `GetSubnets` and `Overlaps`: 4.0 MB more on real IPv4 and 3.7 MB
more on real IPv6. See [Since the baseline](#since-the-baseline).

**Shortcut blocks against the flat table** (`WriteBenchmarks` and lookups, one session, two jobs; the flat table
was removed after this measurement):

| Benchmark | Flat 65,536-slot table | Blocks |
| --- | --- | --- |
| 1,000,000 real IPv4 lookups | 131 ms | 117 ms (0.91) |
| 1,000,000 real IPv6 lookups | 286 ms | 264 ms (0.94) |
| 1,000,000 random-table lookups | 88 ms | 71 ms (0.83) |
| Small batch on the real IPv4 table, 10 removals and 10 additions | 1,366 µs, 2.2 MB allocated | 29 µs, 104 KB |
| 2,110 single writes on the real IPv4 table | 2.32 ms | 2.03 ms |

Timed one by one, 3,000 such small batches took a median of 817 µs with the flat table, 5.2 ms at the 99th percentile
and up to 47 ms, with about 150 full collections, because each commit allocated a new 1 MB table. With blocks: a median
of 42 µs, 0.13 ms at the 99th percentile, 13 ms at most and one full collection. An empty trie holds 2.3 KB, and an empty
IP lookup about 4.5 KB, where both used to start at 1 MB per address family.

**The retry for lookups that overlap two writes** (one session, each case in its own process, the variants in the
order A B C A B C; the switch between the variants was removed after this measurement). The second pass ran into heavy
interference, so these are the medians of the first four rounds, in ms per million lookups from integers:

| Lookups | No check | Retry loop around the search, as shipped | Check at the search's single exit |
| --- | --- | --- | --- |
| Real IPv4 table | 79.8 and 81.3 | 80.8 | 82.9 |
| Real IPv6 table | 188.2 and 184.4 | 190.9 | 203.4 |
| Small IPv4 table, every 1,054th prefix | 13.08 and 13.10 | 13.90 | 13.88 |

On the real tables the check costs nothing measurable; on a small table that fits in cache it costs about 0.8 ns per
lookup. The single-exit form, which saves a call, measured slower, presumably because the jump back makes the whole
search one loop for the JIT. The numbers above this paragraph were measured before the check.

**The IP layer** adds nothing measurable: `LayerBenchmarks` took 153.6 ms per million real IPv4 lookups on a bare
`BitTrie` and through `IPNetworkLookup`, in one session.

**MaxMind** (`MaxMindBenchmarks`): 100,000 lookups in MaxMind's GeoIP2 Country test database. The database is small and
fits in cache, so this shows the cost per lookup, not what a full GeoIP2 database would give.

| Variant | Time | Allocated |
| --- | --- | --- |
| `Reader.Find`, which decodes the record every time | 608 ms | 469 MB |
| Loaded, read-write | 7.6 ms | 0 |
| Loaded, frozen | 3.4 ms | 0 |
| Loading the database | 4.1 ms | 4.9 MB |

### Since the baseline

Measured on 2026-10-22 in a different 4-core container (Intel Xeon, 2.1 GHz, .NET 10.0.13), where the same
benchmarks ran about three times as fast as on 2026-10-20. Compare within these tables only.

**Lookups without the node stack.** One session, the walk with a stack, kept in the code for deep backtracks, against
the lookup as shipped; bare `BitTrie`, one million lookups from precomputed keys:

| Table | With the stack | As shipped | Ratio |
| --- | --- | --- | --- |
| Real IPv4 | 60.2 ms | 53.1 ms | 0.89 |
| Real IPv6 | 113.1 ms | 105.0 ms | 0.93 |
| Random, 96,944 prefixes | 38.5 ms | 35.5 ms | 0.93 |
| Small, every 1,054th real IPv4 prefix | 12.7 ms | 10.7 ms | 0.85 |

Two earlier sessions of the performance review measured 0.86 and 0.93 on real IPv4, 0.86 and 0.89 on real IPv6 and
0.93 and 0.86 on the small table.

**Enumeration and freezing** (`RealBuildBenchmarks`, the commit before against this one, back to back; the batch
rows, whose code did not change, moved by less than 4%). Enumeration visits only the octets at which something starts,
instead of testing 256 octets and 8 lengths in every node. Writers wait only while a freeze copies the prefixes out:
69 ms for real IPv4 and 24 ms for real IPv6, measured on their own.

| Benchmark | Real IPv4, before | After | Real IPv6, before | After |
| --- | --- | --- | --- | --- |
| Enumerating | 118.5 ms | 59.5 ms | 118.5 ms | 19.6 ms |
| Freezing, leaf-pushed | 153.3 ms | 98.0 ms | 158.9 ms | 55.7 ms |
| Freezing, compressed | 173.1 ms | 111.8 ms | 156.5 ms | 48.7 ms |
| Writers wait during a freeze | the whole freeze | 69 ms | the whole freeze | 24 ms |
| One batch, unchanged | 172.0 ms | 174.7 ms | 40.6 ms | 42.0 ms |

**`ContainsMatch`** against the extension method it replaced, which called `TryGetMatchValue` through the interface
(one session, a million lookups from `IPAddress` objects, frozen tables leaf-pushed):

| Lookup | Extension | `ContainsMatch` | Ratio |
| --- | --- | --- | --- |
| Real IPv4, read-write | 107.0 ms | 76.4 ms | 0.72 |
| Real IPv4, frozen | 41.9 ms | 26.4 ms | 0.64 |
| Real IPv6, read-write | 217.5 ms | 177.4 ms | 0.83 |
| Real IPv6, frozen | 120.8 ms | 99.6 ms | 0.85 |

The member inlines, and it stops at the walk's answer: the read-write trie reads no entry and the frozen one no value.

**Loading a whole table** (`RealBuildBenchmarks`, one session, the RouteViews tables with each network once): a batch of
`TryAdd` calls against the constructor from a sequence, with the networks in address order and shuffled. The Gen0 and
Gen1 columns are collections per thousand loads.

| Load | Batch | From a sequence | Ratio | Allocated, batch / sequence | Gen0 and Gen1, batch / sequence |
| --- | --- | --- | --- | --- | --- |
| Real IPv4, address order | 222.6 ms | 150.0 ms | 0.67 | 96.5 / 62.6 MB | 5,500, 5,000 / 1,667, 1,333 |
| Real IPv4, shuffled | 1,128.5 ms | 292.1 ms | 0.26 | 97.1 / 62.6 MB | 5,000, 4,000 / 1,500, 1,000 |
| Real IPv6, address order | 84.1 ms | 64.5 ms | 0.77 | 30.9 / 24.7 MB | 1,833, 1,667 / 778, 667 |
| Real IPv6, shuffled | 194.4 ms | 151.2 ms | 0.78 | 32.0 / 24.7 MB | 1,667, 1,000 / 833, 667 |

**Against the prototype.** Back to back, `BitTrie` alone took 142 to 154 ms per million real IPv4 lookups and the
prototype `BartTrie` 147 to 156 ms. On IPv6 the library took 341 ms and the prototype 331 ms.

**Queries about networks** (`RealNetworkQueryBenchmarks`, 2026-10-23, one session of 15 iterations per case in a noisy
container, with standard deviations of 6 to 12%). Each family has 100,000 networks, each an announced prefix made up to
8 bits shorter or longer, so that some hold announced prefixes and some lie inside them. The table gives the time and
the allocation per query, through the interface; the frozen tables are leaf-pushed, as the default picks for both.

| Query | Real IPv4, read-write | Real IPv4, frozen | Real IPv6, read-write | Real IPv6, frozen |
| --- | --- | --- | --- | --- |
| `TryGetMatch(network)`, for comparison | 143 ns | 140 ns | 290 ns | 209 ns |
| `Overlaps` | 61 ns | 44 ns | 136 ns | 150 ns |
| `GetMatches(network)`, as a list | 333 ns, 153 B | 298 ns, 118 B | 480 ns, 154 B | 405 ns, 127 B |
| `GetSubnets(network).Any()` | 465 ns, 357 B | 370 ns, 88 B | 498 ns, 446 B | 334 ns, 128 B |
| `GetSubnets(network)`, enumerated to the end | 3.6 µs | 0.98 µs | 1.8 µs | 0.48 µs |

`Overlaps` is the cheapest query, because it stops at the first sign of overlap and reads no entry. On the read-write
trie `TryGetMatch(network)` and `GetMatches(network)` still keep the whole path in a `NodeStack`, whose stores cost the
write barriers that the address lookup shed, and they start at the root instead of a shortcut block; `Overlaps` keeps
only the current node. On the read-write trie `GetSubnets` allocates its two iterators and the enumeration's stack.

**`GetMatches` by key and enumeration**, which now share their code with the queries about networks: the code before,
kept as a copy for one session, against the code after, on the read-write trie of each real table (`GetMatches` into a
span at 200,000 keys; enumeration of the whole trie):

| | Real IPv4 | Real IPv6 |
| --- | --- | --- |
| `GetMatches` by key | 57.3 ms before, 53.5 ms after: 0.93 | 59.5 ms, 62.9 ms: 1.07 |
| Enumeration | 38.6 ms, 39.9 ms: 1.04 | 12.6 ms, 13.2 ms: 1.05 |

The `GetMatches` ratios point in opposite directions, inside one standard deviation (0.09 and 0.13). Enumeration's are
also inside it (0.08), but both are higher, so the check for the top node on every frame of the walk may cost a few
percent. Each enumeration allocates 176 to 192 bytes more, for the larger iterator.

**The read-write walks for networks, without the node stack** (one session on 2026-10-23, 15 iterations per case, the
code before kept as a copy; bare `BitTrie` on the real tables; 100,000 networks as in `RealNetworkQueryBenchmarks`,
200,000 keys for `GetMatches` by key). `TryGetMatch` for a network now starts from a shortcut block and keeps only the
parent; `GetMatches` collects on the way down and reverses; `GetSubnets` and enumeration are one iterator that yields
entries without their bits and allocates its stack only below the first node.

| Query | Real IPv4, before | After | Ratio | Real IPv6, before | After | Ratio |
| --- | --- | --- | --- | --- | --- | --- |
| `TryGetMatch` for a network | 12.4 ms | 10.5 ms | 0.85 | 20.0 ms | 17.2 ms | 0.86 |
| `GetMatches` for a network, into a span | 18.5 ms | 14.9 ms | 0.81 | 26.9 ms | 20.4 ms | 0.76 |
| `GetMatches` for a key, into a span | 53.5 ms | 42.5 ms | 0.81 | 66.4 ms | 51.0 ms | 0.77 |
| `GetSubnets(...).Any()` | 33.2 ms, 332 B per call | 28.4 ms, 171 B | 0.85 | 37.1 ms, 420 B | 32.3 ms, 203 B | 0.88 |
| `GetSubnets`, enumerated to the end | 290.9 ms | 235.7 ms | 0.82 | 147.9 ms | 104.1 ms | 0.71 |
| Enumerating the whole trie | 44.5 ms | 41.3 ms | 0.93 | 15.8 ms | 12.1 ms | 0.77 |
| The walk with bits that `Freeze` reads | 39.9 ms | 38.3 ms | 0.96 | 13.5 ms | 13.1 ms | 0.98 |

The last row is the walk restored to its form for the whole trie, after `GetSubnets` stopped sharing it; it is inside
the noise, as was the cost of sharing it, measured earlier as 1.04 and 1.05.

**Memory held by the frozen tables** (`--memory`, the commit before against this one): they now keep each prefix's
bits.

| Form | Real IPv4, before | After | Real IPv6, before | After |
| --- | --- | --- | --- | --- |
| Read-write, unchanged | 30.4 MB | 30.4 MB | 13.6 MB | 13.6 MB |
| Frozen, leaf-pushed 16/8 | 61.0 MB | 65.0 MB | 58.4 MB | 62.1 MB |
| Frozen, compressed 18 | 40.2 MB | 44.2 MB | 12.1 MB | 15.7 MB |

### Where the time goes

- A read-write lookup reads one shortcut block, then for each node below depth 2 tests a bit and ranks it with
  four popcounts to find the child. The deepest node or its parent then answers nearly every lookup. Each node visited is a likely
  cache miss on the real tables.
- A leaf-pushed lookup reads the root entry and one child table per 8 bits still undecided: about 2 reads on real IPv4
  and 4.6 on IPv6, measured in the prototype. Then it reads the entry with the prefix and value, at a random place in an
  array of 24 MB on real IPv4, which costs about as much as finding it; the value-only lookup reads a smaller array.
- A write copies one node, about 200 bytes, and one array of up to 256 elements. That is 1.4 KB per write on the real
  IPv4 table in the prototype and 2.2 KB on the random table here, which is why writing one prefix at a time allocates
  more than version 2 did. A batch copies each node once.

### Optimization candidates

| Idea | Expected effect | Status |
| --- | --- | --- |
| Copy only the array that changes, not the node and the array | Less allocation per write, fewer collections under churn | Not tried |
| Branch-free level steps in the read-write lookup | Fewer mispredicted branches, mainly on small tables | Not tried |
| Build a batch bottom-up from sorted input | Faster bulk loads than adding one prefix at a time to private nodes | Done, for input in any order, in the constructors: 1.3 to 3.9 times as fast, a third less allocation; see [Building from a sequence](#building-from-a-sequence) |
| A third shortcut level for keys wider than 32 bits | Real IPv6 lookups 0.87 in one session, for 126 KB; IPv4 would lose (1.07, 49 MB) | Measured in the performance review; not done, pending a memory check on MaxMind and worst-case tables |
| Lookups without the node stack, the parent in a local | 0.85 to 0.93 of the time on every table | Done; see [Since the baseline](#since-the-baseline) |
| Enumeration over set bits only; freeze copies under the lock and builds after | Enumeration 2 to 6 times faster, freezing 1.5 to 3 times, writers wait 69 ms instead of 153 ms on real IPv4 | Done; see [Since the baseline](#since-the-baseline) |
| `GetMatches(IPAddress)` into a stack buffer instead of a list | Less garbage | Measured: 1.45 to 1.7 times slower; not done |
| `TryGetMatch` and `GetMatches` for a network without the node stack, from the shortcut blocks | They took 2.1 to 5.5 times as long as `Overlaps` on the read-write trie | Done: 0.76 to 0.86 of the time, and `GetMatches` by key 0.77 to 0.81; see [Since the baseline](#since-the-baseline) |
| `GetSubnets` on the read-write trie as one iterator, without bits, with its stack only when needed | Less than its 357 to 446 bytes per call | Done: half the allocation, 0.71 to 0.88 of the time, and enumeration 0.77 to 0.93; see [Since the baseline](#since-the-baseline) |
| Lookups without bounds checks | | Measured: no difference; not done |
| Choose the frozen layout from the data | Leaf-pushed for clustered tables, compressed for scattered ones | Done; see [The frozen trie](#the-frozen-trie) |
| A 24-bit root for frozen IPv4 tables | Already possible with `RootBits = 24`: 0.37 of the read-write time in the prototype, at about twice the memory | Option exists |
| Pruned backtracking, best match per shortcut slot, child positions cached per node | | Tried in the prototype, slower; see [Bart-Comparison.md](Bart-Comparison.md#remedies-tried) |
| Shortcut blocks per first octet instead of a flat 65,536-slot table | Small tries hold almost nothing; batch commits stop allocating 1 MB | Done; see below |

[Optimization-Log.md](Optimization-Log.md#what-did-not-help) lists more ideas that did not help.

## Testing and benchmarking

```sh
dotnet test Test -c Release                                    # all tests, about 2 minutes
dotnet run -c Release --project PerformanceTest                # all benchmarks
dotnet run -c Release --project PerformanceTest -- --filter '*RandomTable*'
dotnet run -c Release --project PerformanceTest -- --memory    # memory per form and bytes allocated per lookup
```

Set `ROUTEVIEWS_DIR` for the real-table benchmarks and memory report; without it only `RandomTableBenchmarks` runs.

| Test class | What it checks |
| --- | --- |
| `Core/BitTrieTests` | Every query of `BitTrie` and seven frozen layouts against an independent reference, for 8-, 16-, 32- and 128-bit keys, including `GetMatches`, `GetSubnets` and `Overlaps` at the stored prefixes, one bit shorter and longer, and at every length (8 and 16 are test-only types; 8 has no shortcut blocks), before and after removals; canonical shape; batches commit and discard; a commit that throws, a batch ended on another thread, a batch in which a write threw; `Version`; frozen root sizes; lock-free readers during churn; lookups that overlap several writes; concurrent writers |
| `IP/IPNetworkLookupTests` | The v2 test scenarios as lookups; every address form; the queries about networks; the counts per family; IPv4-mapped addresses and networks with mapping off and on; address conversions; writes and batches, including failed and wrong-thread batches; freezing both families at one moment; one freeze layout for both families; a reader during churn |
| `Adapters/AdapterTests` | Each adapter package in both forms; MaxMind databases against `Reader.Find`, at IPv4, IPv4-compatible and IPv4-mapped addresses, and a hand-built database with a record above the IPv4 networks |

## Limitations

- **Garbage under heavy churn.** Copy-on-write allocates about 1.4 KB per write on the real IPv4 table, mostly the copied
  array of the changed node. In the prototype a million writes at full speed caused 195 collections and pauses of up to
  38 ms. At ordinary routing-table update rates this is rare; copying smaller pieces would reduce it.
- **Small tables that fit in cache** look up several times slower than in the prototype's binary trie with an index,
  because a lookup walks several levels and backtracks: on the version 2 benchmark data, 154 ms per million lookups
  against 29 ms in an earlier session. Freeze such tables, with the compressed layout if their prefixes are scattered.
- **Leaf-pushed frozen tables grow with scattered prefixes**: 109 MB for the 96,944 random prefixes of the version 2
  benchmark, where the compressed layout needs 7 MB, and 1.4 GB against 56 MB for 100,000 random IPv6 /128 prefixes;
  from about 650,000 such prefixes the leaf-pushed builder refuses, because its tables would exceed 2^31 entries. The
  default now chooses the compressed layout for such tables, so this applies only when a caller asks for leaf-pushed.
  Between the two thresholds a table can take up to 256 child-table entries per prefix leaf-pushed: 55 MB for 100,000
  random /24 prefixes, against 6 MB compressed.
- **Freezing blocks writers** while it copies the prefixes out: 69 ms for the real IPv4 table and 24 ms for the real
  IPv6 table, measured under [Since the baseline](#since-the-baseline). The tables are built after the writer lock is
  released.
- **Close pairs of long prefixes use more memory**: two /128 prefixes that share 120 bits need a chain of 15 nodes, so
  100,000 such pairs held 281 MB where 200,000 random /128 prefixes held 17 MB. Each write still does bounded work.
- **Batches cannot span an await**: the writer lock belongs to the thread that took it. Ending a batch on another thread
  throws and leaves it open, and a batch that is never ended blocks every writer.
- **MaxMind databases must be trusted**: the loader walks every path of the search tree, and a hostile file whose tree
  shares subtrees can expand into exponentially many networks. A database whose own data covers Teredo or 6to4
  addresses loses it by default, because the loader takes those networks for MaxMind's aliases.
- **Small frozen leaf-pushed tables** of 32 prefixes or more keep the full 16-bit root, 256 KB, for speed. Set
  `RootBits = 8` or use the compressed layout when many small frozen tables must be kept, such as one per VRF.
- **Batches are atomic per address family**, not across both. A frozen copy does hold both families at one moment.
- **IPv6 scope IDs are ignored**, so `fe80::1%7` and `fe80::1%9` are one key.
- **Enumeration and `GetSubnets` are not snapshots** while writes run.

The known issues of version 2, listed in [Architecture-v2.md](Architecture-v2.md#known-issues), do not apply: the code
they describe is gone, and the tests cover `Count`, removal of absent prefixes, `Clear`, `GetOrAdd` and enumeration.
