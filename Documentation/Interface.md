# Proposed Public Interface

This document describes the public interface of the redesigned library: the generic core from
[Abstraction-Study.md](Abstraction-Study.md), the frozen lookups from [Optimization-Log.md](Optimization-Log.md), and
the IP layer on top. It is a design for review. Names are proposals, and [Status](#status) says which parts the
prototype already implements.

## Overview

Three layers, as in the study, each knowing less than the one above it:

| Layer | Types | Knows about |
| --- | --- | --- |
| IP layer | `IPPrefixLookup<TPrefix, TAdapter, TValue>`, `IPNetworkLookup<TValue>`, their frozen forms | `IPAddress`, IPv4 and IPv6, your prefix type |
| Core | `BitTrie<TKey, TPrefix, TValue>`, `FrozenBitTrie<TKey, TPrefix, TValue>` | Bits and lengths only |
| Keys | `IBitKey<TSelf>`, `Bits32`, `Bits64`, `Bits128`, `Bits192` | Bit arithmetic for one fixed width |

Every layer comes in two forms, and the read-write form has two write modes:

| Form | For | Lookups | Changes |
| --- | --- | --- | --- |
| Read-write, throughput mode (default) | Tables that change during their life | Multi-level index, about 14 times faster than today on real IPv4 | Fastest on average; growth and compaction pause the writer |
| Read-write, [real-time mode](#real-time-mode) | Tables that change and must never stall a write | Target: within 10% of throughput mode | Every write does a bounded amount of work |
| Frozen | Tables loaded once, then only read | Leaf-pushed tables, a further 1.9 to 2.7 times faster | None; freeze a new copy to change |

All of them implement the same read-only interface, so code that only looks things up accepts any of them.

## Keys

A key is a fixed-width bit string, left-aligned: bit 0 is the most significant bit. The core needs only these
operations. All are `static abstract`, so the JIT compiles each key type into plain machine instructions.

```csharp
public interface IBitKey<TSelf> where TSelf : struct, IBitKey<TSelf>
{
    static abstract int Width { get; }                                         // 32, 64, 128, 192 ...

    static abstract TSelf Truncate(in TSelf key, int length);                  // clear every bit from length on
    static abstract int CommonPrefixLength(in TSelf a, in TSelf b, int max);   // routing node for two siblings
    static abstract TSelf MaskFor(int length);                                 // cached in each node
    static abstract TSelf SplitFor(int length);                                // the bit after length; cached
    static abstract bool IsSet(in TSelf key, in TSelf split);                  // which child
    static abstract bool MatchesUnderMask(in TSelf key, in TSelf bits, in TSelf mask);

    static abstract int GetBits(in TSelf key, int offset, int count);          // 1 to 24 bits, for tables
    static abstract TSelf SetBits(in TSelf key, int offset, int count, int value);
    static virtual int GetTopBits(in TSelf key, int count) => TSelf.GetBits(key, 0, count);
    static virtual TSelf FromTopBits(int top, int count) => TSelf.SetBits(default, 0, count, top);
}
```

- `Width` is new. The prototype passes the width to each trie's constructor instead.
- `GetBit` and `StartsWith` from the prototype are dropped. Only the uncached tries used them.
- `GetTopBits` and `FromTopBits` get default implementations, so a new key type writes nine members. The shipped types
  override them where that saves an instruction.
- Shipped key types: `Bits32`, `Bits64`, `Bits128`, and `Bits192` for IPv6 plus up to 64 bits of context. Each has a
  constructor from its integer or words.

## Core

### Read-only interface

```csharp
public readonly record struct PrefixEntry<TPrefix, TValue>(TPrefix Prefix, TValue Value);

public interface IReadOnlyBitTrie<TKey, TPrefix, TValue> : IReadOnlyCollection<PrefixEntry<TPrefix, TValue>>
    where TKey : struct, IBitKey<TKey>
{
    // longest match for a full-width key, such as an address
    bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value);
    bool TryGetMatchValue(in TKey key, out TValue value);

    // longest stored prefix that contains the given prefix, which may be the prefix itself
    bool TryGetMatch(in TKey bits, int length, out TPrefix prefix, out TValue value);

    // exact prefix, dictionary-style
    bool TryGetValue(in TKey bits, int length, out TValue value);
    bool ContainsPrefix(in TKey bits, int length);

    // every stored prefix that contains the key, longest first
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(in TKey key);
    int GetMatches(in TKey key, Span<PrefixEntry<TPrefix, TValue>> destination); // returns the count; Width + 1 always fits
}
```

- **`out` parameters** for lookups, as decided: all measured result shapes tied, and this one keeps the node layout
  private.
- **`TryGetMatchValue`** skips the prefix. When frozen, it reads a dense array of values, which was 12 to 54% faster in
  quick timings on the real tables.
- **Naming.** `TryGetValue` and `ContainsPrefix` match exactly, as `Dictionary` does. Everything named `Match` finds the
  longest containing prefix.
- **Enumeration** returns entries in address order, a shorter prefix before a longer one at the same address. `Count`
  is an `int`: the node arrays use `int` indexes, so a table cannot hold more.
- **Lengths** outside 0 to `TKey.Width` throw `ArgumentOutOfRangeException`. Bits after the length are ignored.

### Read-write trie

```csharp
public sealed class BitTrie<TKey, TPrefix, TValue> : IReadOnlyBitTrie<TKey, TPrefix, TValue>
    where TKey : struct, IBitKey<TKey>
{
    public BitTrie(BitTrieOptions? options = null);
    public BitTrie(IEnumerable<(TKey Bits, int Length, TPrefix Prefix, TValue Value)> items, BitTrieOptions? options = null);

    public bool TryAdd(in TKey bits, int length, TPrefix prefix, TValue value);
    public void Add(in TKey bits, int length, TPrefix prefix, TValue value);      // throws if the prefix is present
    public bool AddOrUpdate(in TKey bits, int length, TPrefix prefix, TValue value); // true when added
    public TValue GetOrAdd(in TKey bits, int length, TPrefix prefix, Func<TPrefix, TValue> valueFactory);
    public bool Remove(in TKey bits, int length);                                 // false for a routing node
    public void Clear();

    public BulkLoad BeginBulkLoad();               // dispose to finish
    public void Compact(bool trimExcess = false);
    public BitTrieStatistics GetStatistics();
    public FrozenBitTrie<TKey, TPrefix, TValue> Freeze(FrozenBitTrieOptions? options = null);

    public string ToTextTree(Func<TKey, int, string> format);
    public string ToDotTree(Func<TKey, int, string> format);
}

public sealed record BitTrieOptions
{
    public int Capacity { get; init; }                                  // expected prefixes; sizes the arrays once
    public int IndexRootBits { get; init; } = 16;
    public int IndexTableBits { get; init; } = 8;
    public int IndexThreshold { get; init; } = 8;                       // 32: smaller index, cheaper updates
    public CompactionPolicy Compaction { get; init; } = CompactionPolicy.Automatic;
    public WriteMode WriteMode { get; init; } = WriteMode.Throughput;
}

public enum CompactionPolicy { Manual, Automatic }

public enum WriteMode
{
    Throughput, // contiguous arrays: fastest on average; growth and compaction pause the writer
    RealTime    // chunked storage and incremental cleanup: no write does work proportional to the table size
}

public readonly record struct BitTrieStatistics(int Prefixes, int Nodes, long Bytes, int GarbageNodes, int GarbageValues, int GarbageIndexEntries);
```

- **Construction from a sequence bulk loads**, the fastest way to fill a table. `BeginBulkLoad` returns a disposable
  scope for loading in pieces: inside it the index is not maintained, and readers walk from the root, which is slower
  but correct. Disposing it builds the index and compacts.
- **Compaction.** `Automatic` compacts once garbage exceeds the live size. That bounds memory to about twice the
  compacted size, but the write that crosses the line pays for it: 0.1 to 1.4 seconds on the real tables. Readers are
  never blocked. `Manual` leaves it to the application, which can watch `GetStatistics`. `trimExcess` drops all
  headroom, for a table that will no longer grow.
- **Index tuning.** A threshold of 8 was fastest on the real tables. A threshold of 32 made lookups about 10% slower,
  with a smaller index and cheaper updates. The root and table widths are exposed for unusual key widths. 16 and 8
  were best for both IP families.
- **Diagnostics** take a formatter for bits and length, because routing nodes have no prefix object. The IP layer
  supplies one.

### Real-time mode

`WriteMode.RealTime` is for applications where a single slow write is a failure, such as a router following a live
feed or a service with a latency budget per request. It keeps the same methods and the same lookup results; only the
write path and the storage change.

**What it removes.** Worst-case latency was measured on the real IPv4 table, with one writer making a million random
removals and re-additions as fast as it could while one reader timed every lookup (`tail-latency` in the prototype):

| | Median write | 99.99th percentile | Slowest writes |
| --- | --- | --- | --- |
| Never compacting | 3.4 µs | 535 µs | 162 to 434 ms when the arrays grew |
| Compacting when garbage exceeds live values | 3.1 µs | 273 µs | 378 to 804 ms compacting, 29 to 315 ms growing |

Apart from those, the slowest writes took 4 to 12 ms, after changes to prefixes from /8 to /24. Some of that is index
refresh, which is known to be expensive for short prefixes, but the reader alone stalled for up to 12 ms with an idle
writer, so these writes cannot be told apart from scheduling noise in this container. Readers were never held up by the
writer: lookup latency had the same tail with an idle writer. Garbage collection paused for at most 1 ms per run.

**What changes inside.** Each source of a long write gets a bounded replacement:

| Today, in one write | In real-time mode |
| --- | --- |
| A full array grows by copying all of it: tens of MB | Storage is a list of fixed-size chunks; growing adds a chunk and copies nothing |
| Compaction rebuilds everything at once | Each write also moves a few live nodes out of the emptiest chunk. An empty chunk leaves the next snapshot, and the garbage collector frees it once no reader holds an older snapshot, so readers need no extra bookkeeping |
| A change to a short prefix rewrites every index entry beneath it | Each index entry records only the best match whose length falls in its own level, and a lookup combines the levels as it descends. A change then rewrites entries in one level: at most 256, or up to 65,536 root entries for a prefix shorter than 16 bits |
| Bulk loads and compaction allocate arrays of tens of MB | Chunks are small and long-lived, so writes make no large allocations |

Removing a node also has to repoint the index entries whose walk starts at it. Keeping that bounded is the hardest part
of the design and the first thing the prototype has to settle.

**Targets.** These are what the prototype must show on the real tables, with the same tail-latency test:

- **Bounded work.** The test should also count the nodes and index entries each write touches, which timer noise
  cannot distort. The largest count must not grow with the table.
- **Latency.** No write slower than the worst stall of the idle control, 12 ms in this container, against 804 ms today.
  On a quiet machine the target is 1 ms.
- **Memory** within 1.5 times the compacted size under sustained churn. Never compacting reached 4.8 times in the
  churn test.
- **Lookups** within 10% of throughput mode. Each node and index read goes through a chunk table, one extra load from a
  table small enough to stay in L1. On tables larger than the cache that should cost a few percent; on small tables
  that fit in cache it costs more.

**How the other members behave.**

- `Compaction` is ignored, because cleanup is continuous. `Compact()` still runs a full compaction when called, for an
  application that has a quiet moment and wants the memory back at once.
- `Capacity` allocates the chunks up front, so that even the first writes after loading allocate nothing.
- Bulk loading is a load, not a write: it runs at full speed and may pause. A table loaded that way is in real-time
  shape when the load finishes.
- `Freeze` reads the whole table, so it blocks writers for its duration in either mode.
- Average write throughput is lower than in throughput mode, because each write pays for its share of the cleanup.

### Frozen trie

```csharp
public sealed class FrozenBitTrie<TKey, TPrefix, TValue> : IReadOnlyBitTrie<TKey, TPrefix, TValue>
    where TKey : struct, IBitKey<TKey>
{
    public FrozenBitTrieOptions Options { get; }
    public long Bytes { get; }
}

public sealed record FrozenBitTrieOptions
{
    public FrozenLayout Layout { get; init; } = FrozenLayout.LeafPushed;
    public int? RootBits { get; init; }                 // default: 16 for leaf-pushed, 18 for compressed
    public int TableBits { get; init; } = 8;            // leaf-pushed child tables
    public bool ValueOnlyLookups { get; init; } = true; // keep a dense copy of the values
}

public enum FrozenLayout { LeafPushed, Compressed }
```

- **Built only by `Freeze`.** To build one straight from a sequence, construct a `BitTrie` from it and freeze that; the
  temporary trie is then garbage. A frozen trie never changes. To change it, update the read-write trie and freeze
  again, then swap the reference.
- **Layouts.** `LeafPushed` with a 16-bit root is the default: 2.4 times faster than the read-write design on real IPv4
  and 1.9 times on IPv6. `Compressed` is the low-memory choice: tables of 7 MB on real IPv4 and 4.5 MB on IPv6,
  against 28 and 51 MB, for lookups about 1.4 times faster than the read-write design. A 24-bit root with 8-bit tables
  is the fastest IPv4 shape, at 66 MB.
- **Freezing** blocks writers while it runs. Building the tables alone took 0.5 to 2.6 seconds on the real tables,
  depending on the layout. Readers of the read-write trie are not affected.
- **`ValueOnlyLookups`** costs one value per prefix: 4 MB for `int` values on real IPv4. Turn it off for large value
  types if `TryGetMatchValue` is not used.

**Keeping the full interface without the nodes.** Lookups read only the tables and the entries, so a frozen trie can
drop the trie nodes, which are 44 MB on real IPv4. The other queries still work if each entry records its length and the
entry of the nearest shorter prefix that contains it. That costs 5 bytes per prefix:

- `GetMatches` finds the longest match, then follows those links up to the shortest.
- `TryGetMatch` for a prefix finds the longest match for the prefix's first address, then follows the links until the
  length is no longer than the query's.
- `TryGetValue` does the same and checks that the length is equal. If the prefix is stored, it lies on that chain,
  because it contains its own first address.

Estimated memory, computed from array sizes including those links and the dense `int` values:

| Design | Real IPv4 | Real IPv6 |
| --- | --- | --- |
| Read-write, threshold 8, measured | 113 MB | 64 MB |
| Frozen, leaf-pushed 16/8 | about 61 MB | about 58 MB |
| Frozen, compressed, 18-bit root | about 40 MB | about 12 MB |

### Thread safety

- **Read-write trie.** Any number of threads may read while writes happen. Reads take no locks, and each sees the
  table either before or after any single change, never in between. Writes are serialized by an internal lock, so
  there is no separate concurrent class. An uncontended lock costs roughly 20 ns, about 3% of an insert on the real
  tables; that is an estimate, not a measurement. Enumeration during writes is safe and sees each entry whole, but is
  not a snapshot of the entire table at one instant.
- **Frozen trie.** Immutable after `Freeze` returns. Any number of threads may read.

## IP layer

The IP layer holds one core for IPv4 (`Bits32`) and one for IPv6 (`Bits128`), routes each call by address family, and
converts addresses without allocating. Your prefix type plugs in through a struct adapter, as in the study:

```csharp
public interface IIPPrefixAdapter<TPrefix>
{
    static abstract bool IsIPv4(TPrefix prefix);
    static abstract uint GetIPv4Bits(TPrefix prefix);    // left-aligned
    static abstract UInt128 GetIPv6Bits(TPrefix prefix); // left-aligned
    static abstract int GetLength(TPrefix prefix);
}
```

### Read-only interface

```csharp
public interface IReadOnlyIPPrefixLookup<TPrefix, TValue> : IReadOnlyCollection<PrefixEntry<TPrefix, TValue>>
{
    // longest match for an address
    bool TryGetMatch(IPAddress address, out TPrefix prefix, out TValue value);
    bool TryGetMatch(ReadOnlySpan<byte> address, out TPrefix prefix, out TValue value); // 4 or 16 bytes, network order
    bool TryGetMatchIPv4(uint address, out TPrefix prefix, out TValue value);           // host order
    bool TryGetMatchIPv6(UInt128 address, out TPrefix prefix, out TValue value);
    bool TryGetMatchValue(IPAddress address, out TValue value);
    // ... TryGetMatchValue also for spans, uint and UInt128

    // prefixes
    bool TryGetMatch(TPrefix network, out TPrefix prefix, out TValue value); // longest stored prefix containing it
    bool TryGetValue(TPrefix prefix, out TValue value);                      // exact
    bool ContainsPrefix(TPrefix prefix);

    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(IPAddress address);
    int GetMatches(IPAddress address, Span<PrefixEntry<TPrefix, TValue>> destination);
}
```

The span, `uint` and `UInt128` overloads serve callers that never have an `IPAddress`, such as packet parsers. The
`IPAddress` overloads convert through the `Address` property for IPv4, which was about 30% faster end to end than
`TryWriteBytes`. They use `TryWriteBytes` for IPv6.

### Read-write and frozen lookups

```csharp
public class IPPrefixLookup<TPrefix, TAdapter, TValue> : IReadOnlyIPPrefixLookup<TPrefix, TValue>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>
{
    public IPPrefixLookup(BitTrieOptions? options = null);
    public IPPrefixLookup(IEnumerable<KeyValuePair<TPrefix, TValue>> items, BitTrieOptions? options = null);

    public bool TryAdd(TPrefix prefix, TValue value);
    public void Add(TPrefix prefix, TValue value);
    public bool AddOrUpdate(TPrefix prefix, TValue value);
    public TValue GetOrAdd(TPrefix prefix, Func<TPrefix, TValue> valueFactory);
    public bool Remove(TPrefix prefix);
    public void Clear();

    public BulkLoad BeginBulkLoad();
    public void Compact(bool trimExcess = false);
    public IPLookupStatistics GetStatistics();     // per family
    public FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> Freeze(FrozenBitTrieOptions? ipv4 = null, FrozenBitTrieOptions? ipv6 = null);

    public string ToTextTree(AddressFamily family);
    public string ToDotTree(AddressFamily family);
}

public class FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> : IReadOnlyIPPrefixLookup<TPrefix, TValue>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>;
```

Concrete classes hide the generic parameters, because C# cannot infer the key and adapter types:

| Package | Read-write | Frozen | Adapter |
| --- | --- | --- | --- |
| Core package, no dependencies | `IPNetworkLookup<TValue>` | `FrozenIPNetworkLookup<TValue>` | `IPNetworkAdapter` for `System.Net.IPNetwork` |
| `NetworkPrefix` package | `NetworkPrefixLookup<TValue>` | `FrozenNetworkPrefixLookup<TValue>` | `NetworkPrefixAdapter` |

Each concrete read-write class redeclares `Freeze` with `new` so that it returns its own frozen class. Today's
value-less `NetworkPrefixLookup` becomes a lookup whose value type is an empty struct.

## Examples

A routing table that follows a live feed:

```csharp
var routes = new IPNetworkLookup<int>();
routes.TryAdd(IPNetwork.Parse("10.0.0.0/8"), 64512);
routes.AddOrUpdate(IPNetwork.Parse("10.1.0.0/16"), 64513);
routes.Remove(IPNetwork.Parse("10.0.0.0/8"));

if (routes.TryGetMatch(IPAddress.Parse("10.1.2.3"), out var network, out var asn))
    Console.WriteLine($"{network} -> AS{asn}");
```

A table loaded once, then only read, such as an IP-to-AS map:

```csharp
FrozenIPNetworkLookup<int> origins = new IPNetworkLookup<int>(LoadPrefixes()).Freeze();

// on many threads
if (origins.TryGetMatchValue(packet.SourceAddress, out var asn)) Count(asn);
```

Reloading the read-only table without stopping readers:

```csharp
private volatile FrozenIPNetworkLookup<int> _origins;

void Reload() => _origins = new IPNetworkLookup<int>(LoadPrefixes()).Freeze(); // readers switch on their next lookup
```

A non-IP key, using the core directly. A 48-bit MAC address prefix is left-aligned in a `Bits64`:

```csharp
var vendors = new BitTrie<Bits64, string, string>();
vendors.TryAdd(new Bits64(0x001A2BUL << 40), 24, "00:1A:2B", "Example vendor");
vendors.TryGetMatch(new Bits64(macAddress << 16), out var oui, out var vendor);
```

## Status

| Part | State |
| --- | --- |
| `IBitKey<TSelf>` and the key types | In the prototype, `WideKeys.cs`, except `Bits64` and the new `Width` member |
| Read-write lookups, `TryAdd`, removal, bulk load, `Compact`, garbage counts | In the prototype, `SnapshotTrie.cs` |
| Frozen leaf-pushed and compressed lookups | In the prototype, `PooledTrie` in `FastTrie.cs` |
| Value-only lookups | Timed with a dense value array; not yet a method |
| Exact lookups, `GetMatches`, matches for a prefix, `GetOrAdd`, `AddOrUpdate`, enumeration | In today's library, not in the prototype |
| Frozen tries without nodes, through parent links | Design only |
| Internal writer lock, automatic compaction | Design only |
| Real-time mode | Design only. The `tail-latency` test that measures it runs against `SnapshotTrie` today |
| Index entries that record only their own level's best match | Design only; would also make short-prefix updates cheaper in throughput mode |
| IP layer | Adapters and address conversion in the prototype; the layer itself is design only |

## Open questions

- **Package names**, and whether the core, the IP layer and the `IPNetwork` adapter ship as one package, as the study
  recommends.
- **IPv4-mapped IPv6 addresses**, such as `::ffff:10.1.2.3`. Today they are looked up in the IPv6 table. An option could
  map them to IPv4.
- **Automatic compaction as the default.** It bounds memory, but in throughput mode a write can occasionally take most
  of a second. `Manual` never pauses on its own, but memory grows until the application compacts. Applications that
  cannot accept either should use real-time mode.
- **A cleanup budget for real-time mode.** The design picks the amount of cleanup per write internally, from how much
  garbage there is. Exposing it as an option would let applications trade memory for write latency.
- **Real-time mode as the default.** If the prototype meets its lookup target, real-time mode may be the better
  default, with throughput mode an option for bulk-heavy workloads.
- **Whether `Add` should exist** next to `TryAdd`, `AddOrUpdate` and `GetOrAdd`. It is there for familiarity with
  `Dictionary`.
