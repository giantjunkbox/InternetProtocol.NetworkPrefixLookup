# Survey: Prefix Lookup in the Linux Kernel

This survey reads the Linux kernel's prefix-matching code for ideas this project could borrow. The source was read
from the master branch of torvalds/linux on 2026-10-19:

| File | What it is |
| --- | --- |
| `net/ipv4/fib_trie.c`, `Documentation/networking/fib_trie.rst` | The IPv4 routing table: an LC-trie |
| `net/ipv4/fib_semantics.c` | Route attributes shared between routes |
| `net/ipv6/ip6_fib.c` | The IPv6 routing table: a binary radix tree |
| `kernel/bpf/lpm_trie.c` | The BPF longest-prefix-match map |
| `net/netfilter/nft_set_pipapo.c`, `nft_set_pipapo_avx2.c` | nftables sets of concatenated fields and ranges |
| `net/netfilter/ipset/ip_set_hash_gen.h` | ipset `hash:net` and its relatives |
| `net/netfilter/nft_set_rbtree.c`, `Documentation/core-api/maple_tree.rst` | Interval sets and the maple tree |

## Summary

- **Nothing in Linux looks better for this library's job than the designs already measured here.** The IPv4 routing
  table is based on the 1999 LC-trie of Nilsson and Karlsson, tuned over many years: a binary trie with adaptive
  multi-bit nodes. This is a judgment from reading the code; the kernel structures were not benchmarked.
- **Linux has good ideas around the edges**, and one is already borrowed: nftables commits a batch of set changes
  atomically, or aborts it. `BartTrie`'s bulk load is now such a batch; an aborted one leaves no trace.
- **Worth borrowing next:** a version number for validating cached results, as IPv6 routes have; a per-node "shortest
  prefix below" for pruning lookups, as `fib_trie` keeps; and shared values, as routes share their attributes.

## What Linux does

### IPv4: an LC-trie

`fib_trie.c` implements the level- and path-compressed trie of Nilsson and Karlsson.

- **Adaptive node width.** An internal node indexes its children with a run of key bits whose length adapts to
  density. After each change, `resize()` doubles a node's child array while its fill factor stays above roughly 50%,
  and halves it below roughly 25%, or 30% and 15% at the root.
- **Path compression.** A node records the bit position it tests, so single-child chains are skipped. The skipped bits
  are checked against the key stored in the leaf.
- **One leaf per network address.** A leaf holds every prefix length stored for its address, sorted longest first, so
  10.0.0.0/8 and 10.0.0.0/24 share a leaf.
- **One comparison per step.** `index = (key XOR node.key) >> node.pos` gives the child index when it is below
  2^bits, and a value at or above 2^bits means the skipped bits differ. Both checks are one subtraction and one
  comparison.
- **Suffix length pruning.** Each node keeps the shortest prefix anywhere below it, as a suffix length. A lookup records
  a place to backtrack to only where that subtree could still match, and skips subtrees that cannot.
- **Backtracking** inside a node clears the lowest set bit of the child index, which steps to the next less specific
  child.
- **RCU.** Readers take no locks. A writer replaces nodes and frees the old ones with `call_rcu` once every reader that
  might hold them has finished. That is the same pattern as this project's copy-on-write nodes, with the garbage
  collector in the role of RCU.

### IPv6: a binary radix tree

`ip6_fib.c` walks a binary radix tree, one bit per node, and backtracks through parent pointers. Two features stand out:

- **Serial numbers.** Each node carries `fn_sernum`, renewed when routes below it change. A socket that cached a route
  keeps the serial number and revalidates the cache with one comparison instead of a new lookup.
- **Source-specific routing.** With subtrees enabled, a destination prefix can hold a second tree keyed by the source
  address, giving a two-step lookup on destination and then source.

### Shared route attributes

`fib_semantics.c` looks up every new route's attributes in a hash table and shares an identical `fib_info` between
routes, with a reference count. Nexthop objects go further: many routes point at one nexthop, and changing it changes
them all.

### The BPF trie

`lpm_trie.c` is a binary path-compressed trie with intermediate nodes, protected by RCU. Its key length is chosen when
the map is created, stored as a byte array. It offers nothing beyond the designs here, and it is the run-time key width
that [Abstraction-Study.md](Abstraction-Study.md#arbitrary-key-widths) measured as the expensive option.

### nftables PIPAPO

`nft_set_pipapo.c` matches concatenated fields, such as an address range plus a port range, against a set of entries.

- **Ranges as netmasks.** A range of b-bit values expands to at most 2b netmasks, citing Rottenstreich 2010. The range
  lookup in [Interface.md](Interface.md#ranges) relies on the same bound.
- **Bitmap intersection.** Each field is cut into 4-bit groups. For every group value, a bitmap marks the rules that
  match it, and a lookup ANDs one bitmap per group. An AVX2 version handles 256 bits at a time.
- **Transactions.** Changes go to a clone of the match data. Commit publishes the clone with one pointer swap, and
  abort throws it away, so packets never see half a batch.

### ipset hash:net

`ip_set_hash_gen.h` keeps one hash table keyed by masked address and prefix length, plus the list of prefix lengths in
use, with a count each. A lookup masks the address to each length in use, longest first, and probes the table. A
sequence count makes readers retry if the list of lengths changes underneath them. That is fast when only a few
lengths are in use, such as block lists of /32s and /24s. The real tables here use 25 lengths for IPv4 and 51 for IPv6,
so a lookup could need dozens of probes.

### Interval sets and the maple tree

`nft_set_rbtree.c` keeps interval sets in a red-black tree. The maple tree is an RCU-safe B-tree for non-overlapping
ranges, used for virtual memory areas, with lock-free readers and locked writers.

## What to borrow

| Idea | Source | Verdict |
| --- | --- | --- |
| Atomic batches with commit and abort | nftables | **Borrowed**: `BartTrie.AbortBulkLoad` discards a bulk load that readers never saw; tested |
| A version number for validating cached results | IPv6 `fn_sernum` | **Recommended**: one counter on the trie, renewed on every published change |
| Shortest prefix below each node, to prune lookups | IPv4 suffix length | **Try next** in `BartTrie`: backtracking to the root is a likely cost on small tables |
| Shared values | `fib_info`, nexthop objects | **Recommended for frozen tables**: the real IPv4 table has 1,054,037 prefixes but only 78,196 distinct origin values, so a frozen table can store a small index per prefix into a deduplicated value array |
| Two-step lookups, such as destination then source | IPv6 subtrees, ipset `hash:net,net` | **Later**: composes from today's API, a lookup whose values are lookups |
| One comparison per step on path-compressed binary nodes | IPv4 `get_cindex` | **Not now**: would help `SnapshotTrie`, which the BART-style core is set to replace |
| Adaptive node width | IPv4 `resize()` | **No**: fixed 8-bit strides with popcount compression get the memory benefit without resize logic |
| One leaf per network address | IPv4 leaves | **No**: BART nodes already hold every length within a stride |
| One hash probe per length in use | ipset | **No** for routing tables with dozens of lengths; a possible frozen layout for sets with two or three lengths |
| Bitmap intersection over concatenated fields | nftables PIPAPO | **Out of scope**: packet classification, not longest-prefix match |
| Interval trees for overlapping ranges | rbtree, maple tree | **Not needed yet**: the range lookup handles nested ranges by splitting them into prefixes |
| Lock-free readers with deferred freeing | RCU throughout | **Already the design**: copy-on-write nodes freed by the garbage collector |

One lesson from Linux's history also supports this project's direction. The IPv4 routing cache, a hash table of
recent results in front of the trie, was removed in Linux 3.6, in part because a flood of distinct destinations could
fill it. Making the lookup itself fast, as this project does, avoids that exposure.
