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
- **A BART-style read-write core has been prototyped**, with results under
  [Prototype](#prototype-a-bart-style-read-write-core): on the real tables it looks up 12 to 20% faster than
  `SnapshotTrie`, uses a quarter of the memory, builds 2.5 to 3.7 times faster and never stalls a write for compaction
  or growth. It is 2.9 times slower on a small table that fits in cache, and it allocates on every write.
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

## Prototype: a BART-style read-write core

`BartTrie` in the prototype implements that recommendation:

- **Nodes.** 8-bit strides, generic over `IBitKey<TSelf>`. Each node has four 256-bit sets, each with a
  popcount-compressed array: prefixes inside the stride (ART base indexes), fringes, child nodes and leaves. Fringes and
  leaves are stored as structs in their node's arrays, not as objects.
- **Shape.** Canonical: a child node exists exactly when two or more prefixes lie below its octet, so the shape depends
  only on the stored prefixes.
- **Writes.** A write copies the one node it changes, finishes the copy, and links it in with one reference write.
  Readers take no locks, and the garbage collector frees replaced nodes; there is no compaction and no array that grows
  while readers can see it. A bulk load builds privately and publishes once.
- **Shortcut table.** A 65,536-slot table points at the depth-1 and depth-2 nodes of each /16 block, so lookups skip
  the first two levels. A write updates one slot, or at most 256 when it replaces a node nearer the root. This was the
  change that made IPv4 lookups competitive; without it they were 25% slower than `SnapshotTrie`.
- **Tests.** `BartTrieTests` checks it against the reference trie, against the current library on both real tables,
  for canonical shape after removals and in any insertion order, for an invisible bulk load, and with a concurrency
  stress test: 2.5 to 5.3 million writes alongside 62 to 88 million lookups, with no failures.

### Results against SnapshotTrie

**Lookups.** BenchmarkDotNet, one session, one million lookups, `SnapshotTrie` as the baseline. The real tables start
from `IPAddress` objects; the random table is the 96,944-prefix benchmark table, which fits in cache, with precomputed
keys.

| Table | `SnapshotTrie` | `BartTrie` | Ratio | Frozen, for reference |
| --- | --- | --- | --- | --- |
| Real IPv4 | 211 ms | 169 ms | 0.80 ± 0.08 | 103 ms |
| Real IPv6 | 410 ms | 360 ms | 0.88 ± 0.09 | 254 ms |
| Random table in cache | 32 ms | 90 ms | 2.87 ± 0.33 | |

**Building**, same session:

| Table | `SnapshotTrie` bulk load | `BartTrie` bulk load | `BartTrie` one at a time, published after each |
| --- | --- | --- | --- |
| Real IPv4 | 758 ms, 558 MB allocated | 303 ms, 102 MB | 877 ms, 1,159 MB |
| Real IPv6 | 340 ms, 246 MB | 91 ms, 33 MB | 230 ms, 256 MB |

**Memory** retained, with the same `IPNetwork` prefixes and `int` values stored:

| Table | `SnapshotTrie` | `BartTrie` | After removing and re-adding half the prefixes |
| --- | --- | --- | --- |
| Real IPv4 | 113.2 MB | 30.9 MB | no growth |
| Real IPv6 | 64.0 MB | 14.6 MB | 0.4 MB growth |

**Writes under churn.** The tail-latency test: one writer makes a million random removals and re-additions on real
IPv4 as fast as it can while one reader times every lookup.

| | `SnapshotTrie`, compacting when garbage exceeds live values | `BartTrie` |
| --- | --- | --- |
| Median write | 3.1 µs | 1.5 µs |
| 99.99th percentile | 273 to 328 µs | 2.2 to 2.5 ms |
| Slowest write | 378 to 804 ms, compacting or growing | 38 ms, a garbage collection |
| Most work in one write | the whole table | 256 array elements and nodes copied |
| Garbage collections | 0 or 1 | 195, 2.2 to 2.4 s paused in total |
| Allocated per write | none, apart from growth | 1,434 bytes |
| Total for a million writes | 5.5 to 6.4 s | 4.9 to 5.1 s |

### Against the targets

| Target, from [Interface.md](Interface.md#bounded-writes) | Result |
| --- | --- |
| Work per write does not grow with the table | **Met**: at most 256 elements and nodes copied |
| Memory within 1.5 times the compacted size under churn | **Met**: no growth, and 3.7 to 4.4 times smaller to begin with |
| Lookups within 10% of `SnapshotTrie` on the real tables | **Met**: 20% faster on IPv4, 12% faster on IPv6 |
| Lookups within 10% on a table that fits in cache | **Missed**: 2.9 times slower |
| Average writes and bulk loads within 10% | **Met**: bulk loads 2.5 to 3.7 times faster, churn as fast or faster |
| No write slower than the idle control's stalls, 12 ms | **Missed**: garbage collections stall writes for up to 38 ms, against 804 ms today |

### What the misses mean

- **Small tables.** When the whole table fits in cache, `SnapshotTrie` resolves a lookup through its index in about one
  node, while `BartTrie` walks down several levels, takes a data-dependent branch at each, and often backtracks to the
  root. Mispredicted branches are the likely cost; that has not been profiled. 90 ns against 32 ns per lookup matters only for very hot small tables, which can be frozen. The remedies
  tried so far did not help; see [Remedies tried](#remedies-tried).
- **Garbage collection.** Copy-on-write moves the cost of cleanup from rare long compactions into allocation: 1.4 KB per
  write, mostly the copied array of the changed node. At full speed that meant a collection about every 5,000 writes,
  each pausing for about 12 ms on average. At a thousand updates per second, a realistic rate for a routing table, that
  works out to roughly one collection every 5 seconds; this is extrapolated, not measured. Copying smaller pieces, such as one of a node's four arrays instead of the whole node, would cut
  the allocation further.

### Remedies tried

Three lookup changes were measured in one BenchmarkDotNet session each, as separate jobs, with one million lookups
from precomputed keys. Times are per million lookups; a ratio above 1 is slower.

| Change | Random table | Real IPv4 | Real IPv6 |
| --- | --- | --- | --- |
| Plain lookup, the baseline | 114.2 ms | 150.8 ms | 333.6 ms |
| Pruned backtracking | 142.9 ms (1.26) | 171.4 ms (1.14) | 349.2 ms (1.06) |
| Pruned backtracking and the best match of the first two levels kept in each shortcut slot | 112.8 ms (0.99) | 172.8 ms (1.15) | 317.6 ms (0.96) |
| Child positions cached per node, as Go's `Fast` variant does, against a plain lookup of 115.4, 152.8 and 342.7 ms | 125.0 ms (1.16) | 178.7 ms (1.18) | 353.6 ms (1.04) |

- **Pruned backtracking** borrows the idea behind Linux's suffix length: each node records whether it holds any prefix
  of its own, the lookup remembers only the levels that could match, and it backtracks straight to the deepest one.
  Recording those levels costs more than the backtracking it saves.
- **Best match in the shortcut slot** lets a lookup that finds nothing deeper than depth 2 skip backtracking into the
  first two levels. It helps a little on IPv6 and costs as much on IPv4, and it makes writes refresh up to 256 slots
  when a short prefix changes.
- **Cached child positions** replace a popcount with a load from a 256-byte array per node. The popcount is one
  instruction, while the extra array costs memory and cache misses.

None of them is worth its code, so `BartTrie` keeps the plain lookup; the switches `BART_PRUNE=1` and `BART_SLOTBEST=1`
still turn the first two on for measurement. Branch-free level steps have not been tried.

### Verdict

`BartTrie` is the better read-write core for the tables this library exists for: faster lookups and builds on real
routing tables, a quarter of the memory, bounded writes, and no compaction or growth stalls. Its two weaknesses are
small cache-resident tables and allocation under heavy churn. The lookup remedies tried did not help; copying smaller
pieces of a node is still untried for the allocation. The recommendation
is to adopt it as the read-write core, with those two items as the next optimization steps and the frozen tables on
top.

Version 3 of the library follows this recommendation: its `BitTrie` is a production version of `BartTrie`, described in
[Architecture.md](Architecture.md).
