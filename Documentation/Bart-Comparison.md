# Comparison: Bart.NET and the BART Design

[Bart.NET](https://github.com/watfordsuzy/bart.net) is the most relevant other .NET routing table on NuGet. It is a
port of [BART](https://github.com/gaissmai/bart), a Go library for IP routing tables. This document compares both
with this project's design, to decide whether the project is worth doing and what to take from BART.

## Summary

- **Bart.NET is not better in every way.** On the real routing tables, this project's read-write design looks up
  addresses 2.7 times faster for IPv4 and 1.2 times faster for IPv6, and the frozen design 5.3 and 2.5 times faster.
  Bart.NET allocates 184 bytes on every lookup, cannot remove a prefix, does not return the matched prefix, cannot list
  or count its contents, and is not safe to read while writing. It was written in three days in May 2024 and has not
  changed since.
- **Bart.NET is much smaller and faster to build.** It holds the real IPv4 table in 14 MB against 113 MB, and builds
  it in 103 ms against 724 ms.
- **The current Go BART is the real competitor.** Bart.NET ports an early version. The current design, written in C#
  for this comparison, holds IPv4 in 25 MB and IPv6 in 10 MB. Its lookups beat this project's read-write design on IPv6
  and lose on IPv4, and lose to the frozen design on both.
- **The project is worth doing, with a different read-write core.** No maintained .NET implementation of the current
  BART exists. Combining its node design and its way of updating with this project's frozen tables, generic keys and
  adapters would give the best memory, bounded writes and the fastest lookups. That needs a prototype; see
  [Recommendation](#recommendation).

## What BART does

BART is a multibit trie with 8-bit strides, so an IPv4 lookup visits at most 4 nodes and an IPv6 lookup at most 16.

- **Prefixes inside a stride.** A node holds the prefixes that end inside its 8 bits, from /0 to /7 relative to the
  node, as a complete binary tree numbered 1 to 255. That numbering is the base index from Knuth's Allotment Routing
  Table (ART). A 256-bit set marks which entries exist, and the values sit in an array with one slot per set bit. A
  popcount of the set below an entry gives its slot, so a node stores only the prefixes it has.
- **Longest match inside a node in one step.** A precomputed table gives, for each of the 256 octet values, the set of
  tree entries above it. AND that with the node's set, and the highest common bit is the longest match. Bart.NET still
  walks up the tree one entry at a time.
- **Children** are found the same way: a 256-bit set and an array with one slot per child.
- **Path compression.** A prefix that ends exactly on a stride boundary is stored as a *fringe* in the parent's child
  slot, and a lone longer prefix as a *leaf* holding the whole prefix. So a /24 needs no node of its own, and isolated
  long prefixes do not create a chain of nodes. Bart.NET has neither.
- **Lookups** descend to the deepest node, then check nodes from the deepest up; the first match is the longest.
- **Updates and concurrency.** Ordinary updates change nodes in place. `InsertPersist` and `DeletePersist` instead copy
  the nodes on the path and return a new table that shares every other node with the old one. Readers load the current
  table from an atomic reference, writers take a mutex, and the garbage collector frees replaced nodes. There is no
  compaction and no array that grows.
- **Variants.** `Table` is the balanced default. `Fast` adds a 256-byte rank cache per node for about 25% faster
  levels. `Lite` stores no values, for allow lists, and can aggregate its prefixes.
- **API.** Besides insert, delete, get and lookup: lookup by prefix with the matched prefix, subnets and supernets of a
  prefix, overlap tests against a prefix or a whole table, union, equality, clone, sorted iteration, size, text and
  JSON output.

## Measurements

The real RouteViews tables, as in [Optimization-Log.md](Optimization-Log.md): 1,054,037 IPv4 and 239,603 IPv6
prefixes. All four designs returned the same answer as the reference for all 1,000,000 keys of each table.

- **Bart.NET 1.2.1**, as published.
- **BART-style trie in C#**: `BartStyleTrie` in the prototype, the current Go design (8-bit strides, base-index sets,
  one AND per level, fringes and leaves), written for this comparison in one pass. It is not tuned: every insert copies
  a node's array, children are classes told apart by type checks, and there is no rank cache.
- **Read-write design**: `SnapshotTrie`, bulk loaded, multi-level index with threshold 8.
- **Frozen design**: leaf-pushed tables, 16-bit root and 8-bit tables.

**Lookups.** BenchmarkDotNet, one session, one million lookups from `IPAddress` objects, Bart.NET as the baseline.
Bart.NET and the BART-style trie return only the value. The read-write and frozen rows also return the matched prefix,
and the value-only row reads from a dense array of values, as a frozen lookup could.

| Design | Real IPv4 | Ratio | Real IPv6 | Ratio | Allocated per lookup |
| --- | --- | --- | --- | --- | --- |
| Bart.NET | 495 ms | 1.00 | 599 ms | 1.00 | 184 bytes |
| Current library | 2,903 ms | 5.92 | 2,229 ms | 3.74 | 64 to 104 bytes |
| BART-style trie in C# | 290 ms | 0.59 ± 0.08 | 342 ms | 0.57 ± 0.07 | none |
| Read-write design | 186 ms | 0.38 ± 0.04 | 490 ms | 0.82 ± 0.09 | none |
| Frozen design | 94 ms | 0.19 ± 0.02 | 237 ms | 0.40 ± 0.05 | none |
| Frozen design, value only | 65 ms | 0.13 ± 0.02 | 176 ms | 0.30 ± 0.03 | none |

An earlier session gave the same picture: the read-write design at 0.43 and 0.66, the frozen design at 0.22 and 0.35.

**Building.** Same session, Bart.NET as the baseline.

| Design | Real IPv4 | Allocated | Real IPv6 | Allocated |
| --- | --- | --- | --- | --- |
| Bart.NET | 103 ms | 26 MB | 54 ms | 21 MB |
| BART-style trie in C# | 395 ms | 338 MB | 118 ms | 73 MB |
| Read-write design, bulk load | 724 ms | 558 MB | 349 ms | 246 MB |

The BART-style trie's build is slow only because it copies a node's array on every insert; growing the arrays by
doubling, as Bart.NET does, should bring it close to Bart.NET.

**Memory** retained after building, measured as heap growth after full collections:

| Design | Real IPv4 | Real IPv6 |
| --- | --- | --- |
| Bart.NET | 14.2 MB | 16.6 MB |
| BART-style trie in C# | 25.4 MB | 10.2 MB |
| Read-write design | 113.2 MB | 64.0 MB |
| Frozen design, with nodes | 131.8 MB | 90.9 MB |
| Frozen design without nodes, estimated in [Interface.md](Interface.md#frozen-trie) | about 61 MB | about 58 MB |

The BART-style trie uses more than Bart.NET on IPv4 because of fringes. 655,674 of the 1,054,037 IPv4 prefixes are
/24s, and most have nothing below them, so each becomes a small fringe object; Bart.NET stores a /24 as one slot in its
parent's value array. Storing fringes the same way would remove most of the difference.

## What each does better

| | Bart.NET | Current BART design | This project |
| --- | --- | --- | --- |
| Memory | Smallest on IPv4 | Smallest on IPv6 | 4 to 6 times larger |
| Build | Fastest | Close, once tuned | Slowest |
| IPv4 lookups | Slowest, and allocates | Middle | Fastest: read-write 1.6 times and frozen 3.1 times faster than the BART design |
| IPv6 lookups | Slowest, and allocates | Beats the read-write design | Frozen fastest, 1.4 times faster than the BART design |
| Bounded writes and lock-free readers | No | Yes, by copying the path | Designed, not built: chunked storage and incremental cleanup |
| Remove, matched prefix, listing, count | No | Yes | Yes |
| Key widths | IP only | IP only | Any fixed width, including non-IP keys |
| Returns the caller's prefix object | No | Its own prefix type | Yes, through adapters for popular types |

## Ideas worth taking

1. **Copy the path instead of managing storage.** BART's persistent updates give bounded writes and lock-free readers
   with no chunk lists, no incremental cleanup and no repointing of index entries: an update copies at most 4 IPv4 or 16
   IPv6 small nodes and publishes a new root, and the garbage collector does the rest. That would replace the hardest
   part of the bounded-write design in [Interface.md](Interface.md#bounded-writes).
2. **Many prefixes per node.** Storing up to 255 prefixes per node in a popcount-compressed array, instead of one node
   per prefix, is where most of BART's memory advantage comes from.
3. **One AND per level.** The precomputed ancestor sets find the longest match inside a node without a loop. The same
   trick could serve any multibit structure here.
4. **Fringes and leaves** for path compression, with fringe values stored in the parent's arrays rather than as
   objects.
5. **API**: prefix lookups that return the matched prefix, subnets, supernets, overlap tests, union, equality, sorted
   iteration, and a value-less set variant with aggregation for allow lists.

## Recommendation

Keep the project, and prototype a BART-style read-write core before building the real one:

- 8-bit multibit nodes with base-index sets, fringes stored in the parent, leaves, and a rank cache if it pays.
- Updates by path copying, published with one reference write, as BART does persistently.
- Generic over `IBitKey<TSelf>`, with strides taken from the key, so non-IP keys still work.

Measure it against `SnapshotTrie` on the real tables for lookups, inserts, removals, memory and the tail-latency test.
If its IPv4 lookups come within about 10% of `SnapshotTrie`, adopt it as the read-write form: it is 4 to 6 times
smaller, faster to build, faster on IPv6, and its writes are bounded by construction. Keep the frozen leaf-pushed tables
either way; they can be built from any read-write form and remain the fastest lookups measured.
