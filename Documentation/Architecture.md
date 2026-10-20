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
9. [Ranges](#ranges)
10. [Adapter packages](#adapter-packages)
11. [Diagnostics](#diagnostics)
12. [Performance](#performance)
13. [Testing and benchmarking](#testing-and-benchmarking)
14. [Limitations](#limitations)

## Purpose and packages

The library answers one question quickly: given a key such as an IP address, which stored prefix is the most specific
one that contains it? It can also return every stored prefix that contains a key, find the longest stored prefix that
contains a given prefix, and look prefixes up exactly.

| Package | Contents | Depends on |
| --- | --- | --- |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup` | The keys, the core tries, the IP layer, range lookups, and `IPNetworkLookup` for `System.Net.IPNetwork` | Nothing |
| `….NetworkPrefixLookup.NetworkPrefix` | `NetworkPrefixLookup<TValue>` and the value-less `NetworkPrefixLookup` | `Bitvantage.InternetProtocol.NetworkPrefix` |
| `….NetworkPrefixLookup.IPNetwork2` | `IPNetwork2Lookup<TValue>` | `IPNetwork2` |
| `….NetworkPrefixLookup.IPAddressRange` | `IPAddressRangeLookup<TValue>`, a range lookup | `IPAddressRange` |
| `….NetworkPrefixLookup.MaxMind` | `MaxMindNetworkLookup<TValue>` and `MaxMindDatabase.Load` | `MaxMind.Db` |

[Adapter-Survey.md](Adapter-Survey.md) explains why these libraries get packages. All projects target .NET 10 only.

## Repository layout

| Path | Contents |
| --- | --- |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup/Keys/` | `IBitKey<TSelf>`, `Bits32` and `Bits128` |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup/Core/` | `BitTrie`, `FrozenBitTrie`, `IReadOnlyBitTrie`, `BitSet256` and the ART tables, `PrefixEntry`, `NoValue` |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup/IP/` | Adapter interfaces, `IPAddressBits`, `IPPrefixLookup`, `IPRangeLookup`, their frozen forms, `IPNetworkLookup` |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.*/` | One adapter package each |
| `Test/` | NUnit tests: `Core/`, `IP/`, `Adapters/`, and MaxMind test databases in `Data/` |
| `PerformanceTest/` | BenchmarkDotNet benchmarks and a memory report |
| `Prototypes/GenericTrie/` | The prototypes the design was measured with; not part of the solution |

`IPPrefixLookup` and `FrozenIPPrefixLookup` share their read-only members word for word, as do `IPRangeLookup` and
`FrozenIPRangeLookup`. The classes differ only in the type of their two cores, and keeping concrete field types lets
the JIT call the cores directly instead of through an interface. A change to one class's lookups must be made to the
other too; a comment in each says so.

## Layers

| Layer | Types | Knows about |
| --- | --- | --- |
| IP layer | `IPPrefixLookup`, `IPRangeLookup`, `IPNetworkLookup`, the adapter packages, their frozen forms | `IPAddress`, address families, the caller's prefix or range type |
| Core | `BitTrie<TKey, TPrefix, TValue>`, `FrozenBitTrie<TKey, TPrefix, TValue>` | Bits and lengths only |
| Keys | `IBitKey<TSelf>` and the four key types | Bit arithmetic for one fixed width |

The core stores a *prefix object* and a *value* with each prefix. The prefix object is whatever the caller wants back
from a lookup: the IP layer stores the caller's `IPNetwork`, `NetworkPrefix` or range. Routing tables and the core
never create prefix objects, so diagnostics take a formatter for bits and lengths instead.

Both forms of every layer implement one read-only interface, `IReadOnlyBitTrie` in the core and
`IReadOnlyIPPrefixLookup` in the IP layer, so code that only reads accepts either. Lookups return the prefix and value
through `out` parameters, as decided in [Abstraction-Study.md](Abstraction-Study.md).

## Keys

A key is a fixed-width bit string, left-aligned: bit 0 is the most significant bit. `IBitKey<TSelf>` has only static
members, so each key type is a struct the JIT compiles into plain instructions. Widths must be multiples of 8, because the trie
consumes 8 bits per level, from 8 to 128, so that a path fits in a fixed stack of 17 nodes. The library ships `Bits32`
for IPv4 and `Bits128` for IPv6. A user type needs nine members; `GetTopBits` and `FromTopBits` have default
implementations. The test project's `Bits16` is written that way.

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

`Find` walks down while the key's next octet has a child node, pushing each node on a stack (an inline array of 17
nodes). If the last node has a leaf at the key's octet and the leaf matches, that
is the answer. Otherwise it backtracks from the deepest node up: in each node a fringe at the key's octet beats any
prefix inside the stride, then the `Prefixes AND Ancestors` test finds the longest base index. The first hit is the
longest match.

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
| `TryGetMatch(bits, length)` | As `Find`, but only prefixes no longer than `length` count, so the walk stops descending at the node that holds `length` |
| `TryGetValue`, `ContainsPrefix` | Descend to the node of the prefix's stride and test one base index, fringe or leaf |
| `GetMatches` | As `Find`, but collects every hit while backtracking, walking each node's base indexes `i, i/2, …, 1` |
| Enumeration | Depth-first, in address order: at each octet the base indexes that start there, shortest first, then the fringe, the leaf and the child |

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
the writer reads it; the root's count is the trie's `Count`.

### What readers see

`_published` holds an object with the root and the block array. Readers read it once per call. A single write outside a
batch updates the root reference or a child reference, and then the blocks, in place. A batch commit and `Clear` build a
new root and a new block array privately and publish both with one reference write.

- Each lookup sees the trie as it was before or after any single write. A write changes one prefix and touches one
  path, so a reader that combines a newer block entry with an older root, or the reverse, still gets an answer that was
  correct before or after that write. A new block is filled before it is linked in, and a block always holds its
  depth-1 node.
- A batch is all or nothing. Readers keep seeing the published root and blocks until the commit replaces both at once.
- Enumeration during writes sees each entry whole, but is not a snapshot of the whole trie at one instant.

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
back at the published root. Constructing a trie from a sequence is a batch.

`Version` counts published changes: one per write outside a batch, one per commit, one per `Clear`.

## The frozen trie

`BitTrie.Freeze` builds a `FrozenBitTrie`, read-only, with lookup tables that would be too costly to keep current
under writes. It holds no nodes:

| Array | Contents |
| --- | --- |
| `_entries` | The prefix object and value of each prefix, in address order |
| `_values` | A dense copy of the values, for `TryGetMatchValue`, unless `ValueOnlyLookups` is off |
| `_lengths` | Each prefix's length |
| `_parents` | For each prefix, the nearest shorter stored prefix that contains it, or -1 |
| `_root` and `_tables`, or `_root`, `_nodes` and `_answers` | The lookup tables of one of two layouts |

A table entry is an entry index + 1, 0 for no match, or, with the top bit set, the start of a child table or the index
of a compressed node.

**Leaf-pushed**, the default: a root table over the first 16 bits and child tables of 8 bits wherever a block still
holds a longer prefix. Every other entry holds the final answer for its whole block, so a lookup reads only tables.
`RootBits` and `TableBits` choose other shapes, such as 24/8 for IPv4.

**Compressed**, after Poptrie: a root over the first 18 bits of a full routing table, then nodes that each decide 6
bits. A node is two 64-bit
masks and two offsets: one mask marks the slots that lead to a child node, the other the slots where a run of equal
answers starts. A popcount up to the slot finds the child or the answer.

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

- `GetMatches` follows the chain from the longest match.
- `TryGetMatch(bits, length)` follows it to the first entry no longer than `length`.
- `TryGetValue` and `ContainsPrefix` do the same and check that the length is equal.

Freezing holds the source trie's writer lock while it reads, so writes wait; readers of the source are not affected.

## The IP layer

`IPPrefixLookup<TPrefix, TAdapter, TValue>` holds a `BitTrie<Bits32, …>` for IPv4 and a `BitTrie<Bits128, …>` for IPv6
and routes each call by family. `TAdapter` is a struct implementing `IIPPrefixAdapter<TPrefix>`, whose static members
read a prefix's family, network bits and length. Generic code over a struct is specialized by the JIT, so reading the
caller's type costs no indirection.

- **Addresses** convert without allocating: IPv4 through the obsolete `IPAddress.Address` property, which was about
  30% faster end to end than `TryWriteBytes`, and IPv6 through `TryWriteBytes` into a stack buffer. The span, `uint` and
  `UInt128` overloads skip `IPAddress` entirely. `IPAddressBits` exposes the conversions for adapter authors.
- **IPv4-mapped addresses** such as `::ffff:10.1.2.3` are looked up as IPv4 when `IPLookupOptions.MapIPv4MappedAddresses`
  is on, the default. Prefixes are always stored and looked up as given.
- **Batches** begin a batch on both tries. On commit each family publishes atomically, IPv4 first.
- **`Version`** is the sum of the two tries' versions, which changes whenever either does.
- **Concrete classes** such as `IPNetworkLookup<TValue>` fix the prefix and adapter types, and redeclare `Freeze` with
  `new` to return their own frozen class. A frozen class's protected constructor takes the read-write lookup, so
  adapter packages can define their own pairs.
- Null prefixes are rejected without boxing struct prefixes.

## Ranges

`IPRangeLookup<TRange, TAdapter, TValue>` stores ranges with a first and a last address. `IIPRangeAdapter<TRange>`
reads the two ends.

- **Pieces.** A range is stored as the fewest prefixes that cover it exactly: from the first address, repeatedly the
  largest aligned block that fits. That is one prefix for a CIDR block, at most 62 for IPv4 and 254 for IPv6.
- **Claims.** Each piece's prefix object is the winning range, and its value is a `RangePiece`: the winner's value and,
  rarely, the other ranges that split into exactly the same piece, smallest first. A lookup reads the winner directly,
  as fast as a prefix lookup.
- **Nesting.** An address in nested ranges gets the inner range, because the inner range's covering piece is longer or,
  when the pieces are identical, the inner range is the smaller claim. Removing a range removes its claims, uncovering
  the others. Partly overlapping ranges get whichever range's covering piece is longer.
- **The range list.** An `ImmutableSortedDictionary` keyed by family, first and last address answers `Count`,
  enumeration, `TryGetValue`, `ContainsPrefix` and `Remove`. The writer keeps its own view, which runs ahead of the
  published one during a batch.
- **`TryGetMatch(range)`** returns the smallest stored range that contains the whole range. Every stored range that
  contains it has a piece containing its first address, so the candidates are the claims on that address's matches.
- **Writes** take the range lookup's own lock, then the tries' locks. A range of several pieces changes them one at a
  time; a batch publishes them together.

## Adapter packages

Each package defines an adapter struct and a read-write and frozen class pair. Two need more than that:

- **NetworkPrefix** adds the value-less `NetworkPrefixLookup`, deriving from `NetworkPrefixLookup<NoValue>`.
  `NoValueLookupExtensions` in the core adds `TryAdd(prefix)`, `Add(prefix)`, `TryGetMatch(address, out prefix)` and
  `ContainsMatch(address)` for any value-less lookup.
- **MaxMind** adds `MaxMindDatabase.Load<T>`, which reads every network through `Reader.FindAll<T>` into one batch.
  MaxMind's IPv6 databases point `::ffff:0:0/96`, `2001::/32` and `2002::/16` at the IPv4 networks, so a full walk
  returns every IPv4 network four times. The loader skips the three aliases by default; with IPv4-mapped addresses
  mapped, `::ffff:` addresses still find their IPv4 network. `IncludeAliasedNetworks` loads them for exact agreement
  with `Reader.Find`, which the tests check at every network boundary of two MaxMind test databases.

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

**Against the original library.** `PerformanceTest.V2` runs the library as it was before the redesign, at commit
`b512201`, on the same tables as `ComparisonBenchmarks`: `NetworkPrefix` prefixes with `int` values in both, and
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

**Against the prototype.** Back to back, `BitTrie` alone took 142 to 154 ms per million real IPv4 lookups and the
prototype `BartTrie` 147 to 156 ms. On IPv6 the library took 341 ms and the prototype 331 ms.

### Where the time goes

- A read-write lookup reads one shortcut block, then for each node below depth 2 tests a bit and ranks it with
  four popcounts to find the child, and finally backtracks through the nodes it passed. Each node visited is a likely
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
| Build a batch bottom-up from sorted input | Faster bulk loads than adding one prefix at a time to private nodes | Not tried |
| Choose the frozen layout from the data | Leaf-pushed for clustered tables, compressed for scattered ones | Not tried; the caller chooses today |
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
| `Core/BitTrieTests` | Every query of `BitTrie` and seven frozen layouts against an independent reference, for 16-, 32- and 128-bit keys (16 is a test-only type), before and after removals; canonical shape; batches commit and discard; `Version`; lock-free readers during churn; concurrent writers |
| `IP/IPNetworkLookupTests` | The v2 test scenarios as lookups; every address form; IPv4-mapped addresses; writes and batches; a reader during churn |
| `IP/IPRangeLookupTests` | Exact coverage, the fewest pieces, nested ranges and removal, random nested ranges against brute force, batches, the frozen form |
| `Adapters/AdapterTests` | Each adapter package in both forms; MaxMind databases against `Reader.Find` |

## Limitations

- **Garbage under heavy churn.** Copy-on-write allocates about 1.4 KB per write on the real IPv4 table, mostly the copied
  array of the changed node. In the prototype a million writes at full speed caused 195 collections and pauses of up to
  38 ms. At ordinary routing-table update rates this is rare; copying smaller pieces would reduce it.
- **Small tables that fit in cache** look up several times slower than in the prototype's binary trie with an index,
  because a lookup walks several levels and backtracks: on the version 2 benchmark data, 154 ms per million lookups
  against 29 ms in an earlier session. Freeze such tables, with the compressed layout if their prefixes are scattered.
- **Leaf-pushed frozen tables grow with scattered prefixes**: 105 MB for the 96,944 random prefixes of the version 2
  benchmark, where the compressed layout needs 8 MB.
- **Freezing blocks writers** for its duration: 0.2 to 1 second on the real tables.
- **Small frozen leaf-pushed tables** of 32 prefixes or more keep the full 16-bit root, 256 KB, for speed. Set
  `RootBits = 8` or use the compressed layout when many small frozen tables must be kept, such as one per VRF.
- **Batches are atomic per address family**, not across both.
- **Range writes** outside a batch change a range's pieces one at a time.
- **Enumeration is not a snapshot** while writes run.

The known issues of version 2, listed in [Architecture-v2.md](Architecture-v2.md#known-issues), do not apply: the code
they describe is gone, and the tests cover `Count`, removal of absent prefixes, `Clear`, `GetOrAdd` and enumeration.
