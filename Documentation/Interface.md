# Public Interface

This document describes the public interface of version 3 as it ships: what each type is for, how the types fit
together, and the rules behind them. The XML documentation in the code is the reference for every member;
[Architecture.md](Architecture.md) describes the internals and the measurements quoted here. The interface began as a
proposal built on [Abstraction-Study.md](Abstraction-Study.md); [How the interface got here](#how-the-interface-got-here)
records where the shipped interface differs from that proposal and why.

## Overview

Three layers, each knowing less than the one above it:

| Layer | Types | Knows about |
| --- | --- | --- |
| IP layer | `IPPrefixLookup<TPrefix, TAdapter, TValue>`, `IPNetworkLookup<TValue>`, the adapter packages' lookups, their frozen forms | `IPAddress`, IPv4 and IPv6, your prefix type |
| Core | `BitTrie<TKey, TPrefix, TValue>`, `FrozenBitTrie<TKey, TPrefix, TValue>` | Bits and lengths only |
| Keys | `IBitKey<TSelf>`, `Bits32`, `Bits128` | Bit arithmetic for one fixed width |

Every layer comes in two forms, which implement the same read-only interface, so code that only looks things up
accepts either:

| Form | For | Lookups on the real IPv4 table | Changes |
| --- | --- | --- | --- |
| Read-write | Tables that change during their life | 15 times as fast as version 2 | Any number of readers without locks while one writer works; every write does a bounded amount of work, with no pauses for growth or compaction |
| Frozen | Tables loaded once, then only read | About 1.8 times as fast again | None; freeze a new copy to change it |

There is one read-write design, not a choice of write modes. A second code path is worth its cost only when it is
clearly faster, as the frozen form is.

| Package | Contents | Depends on |
| --- | --- | --- |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup` | Keys, core, IP layer, `IPNetworkLookup` for `System.Net.IPNetwork` | Nothing |
| `….NetworkPrefixLookup.NetworkPrefix` | `NetworkPrefixLookup<TValue>` and the value-less `NetworkPrefixLookup` | `Bitvantage.InternetProtocol.NetworkPrefix` |
| `….NetworkPrefixLookup.IPNetwork2` | `IPNetwork2Lookup<TValue>` | `IPNetwork2` |
| `….NetworkPrefixLookup.MaxMind` | `MaxMindNetworkLookup<TValue>` and `MaxMindDatabase.Load` | `MaxMind.Db` |

All packages target .NET 10 and are at version 3.0.0. Everything is in the `Bitvantage.InternetProtocol` namespace.

## Keys

A key is a fixed-width bit string, left-aligned: bit 0 is the most significant bit. The core needs only these
operations. All are `static abstract`, so the JIT compiles each key type into plain machine instructions, and all are
internal:

```csharp
public interface IBitKey<TSelf> where TSelf : struct, IBitKey<TSelf>
{
    internal static abstract int Width { get; }                                // a multiple of 8, at most 128

    internal static abstract TSelf Truncate(in TSelf key, int length);         // clear every bit from length on
    internal static abstract TSelf MaskFor(int length);                        // the first length bits set
    internal static abstract bool MatchesUnderMask(in TSelf key, in TSelf bits, in TSelf mask);

    internal static abstract int GetBits(in TSelf key, int offset, int count); // 1 to 24 bits, as 0 to 2^count - 1
    internal static abstract TSelf SetBits(in TSelf key, int offset, int count, int value);
    internal static virtual int GetTopBits(in TSelf key, int count) => TSelf.GetBits(key, 0, count);
}

public readonly struct Bits32 : IBitKey<Bits32>, IEquatable<Bits32>   { public readonly uint Value;    public Bits32(uint value); }
public readonly struct Bits128 : IBitKey<Bits128>, IEquatable<Bits128> { public readonly UInt128 Value; public Bits128(UInt128 value); }
```

- **Two key types**, `Bits32` for IPv4 and `Bits128` for IPv6, and no others. The interface is public because the
  tries' type parameters are constrained on it, but its members are internal, so no other assembly can implement it:
  the tries index their bit sets and tables with `GetBits` without bounds checks, and a key type whose `GetBits` strayed
  outside 0 to 2^count - 1 would corrupt memory. The test project defines 8- and 16-bit keys through
  `InternalsVisibleTo`.
- **Widths are fixed at compile time** and limited to 128 bits. The read-write trie throws `NotSupportedException` for
  a width that is not a multiple of 8.
- **Context such as a VRF** is served by one lookup per context, not by wider keys.

## Core

### Read-only interface

```csharp
public readonly record struct PrefixEntry<TPrefix, TValue>(TPrefix Prefix, TValue Value);

public interface IReadOnlyBitTrie<TKey, TPrefix, TValue> : IReadOnlyCollection<PrefixEntry<TPrefix, TValue>>
    where TKey : struct, IBitKey<TKey>
{
    // the longest stored prefix that contains a full-width key, such as an address
    bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value);
    bool TryGetMatchValue(in TKey key, out TValue value);
    bool ContainsMatch(in TKey key);                                     // whether any stored prefix contains the key

    // the longest stored prefix that contains the prefix (bits, length), which may be the prefix itself
    bool TryGetMatch(in TKey bits, int length, out TPrefix prefix, out TValue value);

    // exactly this prefix, as a dictionary does
    bool TryGetValue(in TKey bits, int length, out TValue value);
    bool TryGetValue(in TKey bits, int length, out TPrefix prefix, out TValue value);
    bool ContainsPrefix(in TKey bits, int length);

    // every stored prefix that contains the key, longest first
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(in TKey key);
    int GetMatches(in TKey key, Span<PrefixEntry<TPrefix, TValue>> destination); // TKey.Width + 1 entries always suffice

    // the stored prefixes around and inside the prefix (bits, length), each including the prefix itself
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(in TKey bits, int length);     // containing it, longest first
    int GetMatches(in TKey bits, int length, Span<PrefixEntry<TPrefix, TValue>> destination);
    IEnumerable<PrefixEntry<TPrefix, TValue>> GetSubnets(in TKey bits, int length);       // inside it, in address order
    bool Overlaps(in TKey bits, int length);                                              // whether either is non-empty
}
```

- **`out` parameters.** All measured result shapes tied, and this one keeps the node layout private. Every `Try…`
  method's `out` parameters carry `[MaybeNullWhen(false)]`, as `Dictionary.TryGetValue`'s do.
- **Naming.** Everything named `Match` finds the longest containing prefix, and `GetMatches` every containing prefix;
  `GetSubnets` finds the prefixes inside; `TryGetValue` and `ContainsPrefix` match exactly.
- **Around, inside, either.** Two prefixes either nest or share no address, so `GetMatches` and `GetSubnets` between them
  find every stored prefix that shares an address with the given one, and `Overlaps` tells whether there is any, faster
  than either and without allocating. `GetSubnets` produces its prefixes as the result is enumerated, so
  `GetSubnets(bits, length).Any()` stops at the first; its length is checked at the call.
- **`TryGetMatchValue`** skips the prefix object. A frozen trie answers it from a dense array of values, which is faster.
- **Lengths** outside 0 to `TKey.Width` throw `ArgumentOutOfRangeException`. Bits after a prefix's length are ignored.
- **Enumeration** returns entries in address order, a shorter prefix before a longer one at the same address.
- **Allocation.** Lookups allocate nothing, except the list form of `GetMatches`; the span form writes into the
  caller's span.
- **A span that is too small** throws `ArgumentException`, as `Span.CopyTo` does. `TKey.Width + 1` entries always
  suffice, 33 for IPv4 and 129 for IPv6, so a caller who passes that many never sees it. A `TryGetMatches` that returns
  false instead was considered and left out: it would double or reshape every span form for a case that only misuse
  reaches.
- **The prefix object** is whatever the caller stores with a prefix and wants back from lookups. The core never creates
  one, so the diagnostics below take a formatter for bits and lengths instead.

### Read-write trie

```csharp
public sealed class BitTrie<TKey, TPrefix, TValue> : IReadOnlyBitTrie<TKey, TPrefix, TValue>
    where TKey : struct, IBitKey<TKey>
{
    public BitTrie();
    public BitTrie(IEnumerable<(TKey Bits, int Length, TPrefix Prefix, TValue Value)> items);   // built in one pass, any order

    public bool TryAdd(in TKey bits, int length, TPrefix prefix, TValue value);
    public void Add(in TKey bits, int length, TPrefix prefix, TValue value);                     // throws if present
    public bool AddOrUpdate(in TKey bits, int length, TPrefix prefix, TValue value);             // true when added
    public TValue GetOrAdd(in TKey bits, int length, TPrefix prefix, Func<TPrefix, TValue> valueFactory);
    public bool TryUpdate(in TKey bits, int length, TPrefix prefix, TValue newValue, TValue expectedValue);
    public bool Remove(in TKey bits, int length);
    public bool Remove(in TKey bits, int length, out TValue value);                              // returns the value
    public bool TryRemove(in TKey bits, int length, TValue expectedValue);
    public void Clear();

    public Batch BeginBatch();
    public long Version { get; }
    public FrozenBitTrie<TKey, TPrefix, TValue> Freeze(FrozenBitTrieOptions? options = null);
    public BitTrieStatistics GetStatistics();
    public string ToTextTree(Func<TKey, int, string> format);
    public string ToDotTree(Func<TKey, int, string> format);

    public sealed class Batch : IDisposable { public void Commit(); public void Dispose(); }
}

public readonly record struct BitTrieStatistics(int Prefixes, int Nodes, long Bytes);
```

- **Writes** are serialized by an internal lock, so there is no separate concurrent class. Each does a bounded amount of
  work: no step grows with the size of the table.
- **Atomic conditional writes**, as in `ConcurrentDictionary`: `GetOrAdd` adds only when the prefix is absent, and
  `TryUpdate` and `TryRemove` change it only when its value equals the one expected, compared with
  `EqualityComparer<TValue>.Default`. Each checks and writes under the writer lock, so no other write lands in between;
  inside a batch the check sees the batch's own changes.
- **Loading a whole table.** The constructor from a sequence builds the trie in one pass, with the prefixes in any
  order: on the real tables 1.3 to 3.9 times as fast as adding them in a batch, with a third less allocation. A prefix
  that appears twice throws `ArgumentException`.
- **Batches** are atomic. Changes made after `BeginBatch` build privately, and readers keep seeing the table as it was;
  `Commit` publishes them all with one reference write, and disposing the batch without committing discards them. A
  batch holds the writer lock until it ends, and the lock belongs to the thread that took it, so a batch must end on
  the thread that began it and cannot span an `await`; ending it on another thread throws and leaves it open. If a
  write inside the batch throws, `Commit` discards the batch and throws.
- **`Version`** changes with every published change, so a caller can cache lookup results and check them with one
  comparison. Read it before the lookups it is to vouch for.
- **`Freeze`** copies the prefixes out under the writer lock and builds the frozen tables after releasing it, so
  writers wait only for the copy; readers never wait.
- **`GetOrAdd`** calls its factory under the writer lock, so the factory must not write to the same trie.
- **Diagnostics.** `ToTextTree` and `ToDotTree` render the node structure, one for tests and people, one for Graphviz;
  the IP layer supplies an address formatter.

### Frozen trie

```csharp
public sealed class FrozenBitTrie<TKey, TPrefix, TValue> : IReadOnlyBitTrie<TKey, TPrefix, TValue>
    where TKey : struct, IBitKey<TKey>
{
    public FrozenBitTrieOptions Options { get; }   // as given to Freeze
    public FrozenLayout Layout { get; }            // the layout built, which Options may leave to the data
    public long Bytes { get; }                     // memory held by the tables and entries
}

public sealed record FrozenBitTrieOptions
{
    public FrozenLayout? Layout { get; init; }          // null: chosen from the data
    public int? RootBits { get; init; }                 // null: sized to the number of prefixes
    public int TableBits { get; init; } = 8;            // leaf-pushed child tables
    public bool ValueOnlyLookups { get; init; } = true; // keep a dense copy of the values for TryGetMatchValue
}

public enum FrozenLayout { LeafPushed, Compressed }
```

- **Built only by `Freeze`.** A frozen trie never changes. To change it, update the read-write trie, freeze again and
  swap the reference; readers of the old copy are unaffected.
- **Layouts.** `LeafPushed` is the fastest on clustered tables such as routing tables. `Compressed`, after Poptrie, is
  much smaller, and also faster when prefixes are scattered across the address space, where leaf-pushed tables grow
  very large: 100,000 random IPv6 /128 prefixes take 1.4 GB leaf-pushed against 56 MB compressed. Unless the options
  name a layout, `Freeze` takes leaf-pushed, or compressed when the leaf-pushed child tables would hold more than 256
  entries per prefix and more than 16 MB in all. Measured on eleven tables, compressed was both smaller and faster
  there, and the real routing tables, at 7 (IPv4) and 55 (IPv6) entries per prefix, stay leaf-pushed;
  [Architecture.md](Architecture.md#the-frozen-trie) has the table.
- **No nodes.** A frozen trie keeps only its tables and entries. Each entry records its bits, its length and the nearest
  shorter stored prefix that contains it, which answer the queries about prefixes without the nodes: `GetMatches` and
  `TryGetMatch` follow the nearest shorter prefixes, and `GetSubnets` and `Overlaps` binary-search the entries, which
  are in address order. The bits cost 4 bytes per IPv4 prefix and 16 per IPv6 prefix.
- **`ValueOnlyLookups`** costs one value per prefix. Turn it off for large value types if `TryGetMatchValue` is not used.

### What readers see

- **Read-write trie.** Any number of threads may read while one writes, and reads take no locks. Each lookup sees the
  table as it was at one moment: before or after any single change, never part of one. A lookup that overlaps two
  writes looks again; so do `GetMatches` and `Overlaps`. Enumeration and `GetSubnets` during writes are safe and see
  each entry whole, but are not a snapshot of the whole table.
- **Frozen trie.** Immutable; any number of threads may read it.

## IP layer

The IP layer holds one read-write or frozen trie for IPv4 (`Bits32`) and one for IPv6 (`Bits128`), sends each call to
the family it belongs to, and converts addresses without allocating. Your prefix type plugs in through a struct
adapter, whose static members the JIT inlines:

```csharp
public interface IIPPrefixAdapter<in TPrefix>
{
    static abstract bool IsIPv4(TPrefix prefix);
    static abstract uint GetIPv4Bits(TPrefix prefix);    // left-aligned
    static abstract UInt128 GetIPv6Bits(TPrefix prefix); // left-aligned
    static abstract int GetLength(TPrefix prefix);
}

public static class IPAddressBits
{
    public static uint ToIPv4Bits(IPAddress address);    // throws for an IPv6 address
    public static UInt128 ToIPv6Bits(IPAddress address); // throws for an IPv4 address
    public static IPAddress FromIPv4Bits(uint bits);
    public static IPAddress FromIPv6Bits(UInt128 bits);
}
```

### Read-only interface and options

```csharp
public interface IReadOnlyIPPrefixLookup<TPrefix, TValue> : IReadOnlyCollection<PrefixEntry<TPrefix, TValue>>
{
    int IPv4Count { get; }                                                               // Count is both families
    int IPv6Count { get; }

    // the longest stored prefix that contains an address
    bool TryGetMatch(IPAddress address, out TPrefix prefix, out TValue value);
    bool TryGetMatch(ReadOnlySpan<byte> address, out TPrefix prefix, out TValue value); // 4 or 16 bytes, network order
    bool TryGetMatchIPv4(uint address, out TPrefix prefix, out TValue value);           // host order
    bool TryGetMatchIPv6(UInt128 address, out TPrefix prefix, out TValue value);
    bool TryGetMatchValue(IPAddress address, out TValue value);                          // and for spans, uint and UInt128
    bool ContainsMatch(IPAddress address);                                               // and for spans, uint and UInt128

    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(IPAddress address);           // longest first; and for spans, uint and UInt128
    int GetMatches(IPAddress address, Span<PrefixEntry<TPrefix, TValue>> destination);   // 129 entries always suffice

    // networks: the stored prefixes around and inside one, each including the network itself
    bool TryGetMatch(TPrefix network, out TPrefix prefix, out TValue value);             // the longest stored prefix containing it
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(TPrefix network);             // every one containing it, longest first
    int GetMatches(TPrefix network, Span<PrefixEntry<TPrefix, TValue>> destination);
    IEnumerable<PrefixEntry<TPrefix, TValue>> GetSubnets(TPrefix network);               // every one inside it, in address order
    bool Overlaps(TPrefix network);                                                      // whether any shares an address with it

    // exactly this prefix
    bool TryGetValue(TPrefix prefix, out TValue value);
    bool ContainsPrefix(TPrefix prefix);
}

public sealed record IPLookupOptions
{
    public bool MapIPv4MappedAddresses { get; init; }   // off by default; MaxMind lookups turn it on
}
```

- **Addresses.** Every address lookup, `TryGetMatch`, `TryGetMatchValue`, `ContainsMatch` and `GetMatches`, takes an
  address in four forms: an `IPAddress`, bytes in network order (`ReadOnlySpan<byte>`), and a `uint` or `UInt128` for
  methods suffixed `IPv4` and `IPv6`. The last three serve callers that never hold an `IPAddress`, such as packet
  parsers. Scope IDs are ignored.
- **Enumeration** returns the IPv4 prefixes, then the IPv6 prefixes, each in address order. `IPv4Count` and
  `IPv6Count` count each family without a walk; each is read on its own, so during writes they need not add up to a
  `Count` read a moment apart.
- **IPv4-mapped addresses** such as `::ffff:10.1.2.3` are looked up as given by default, which suits routing tables and
  packet classification, where a mapped address on the wire is malformed or crafted (RFC 4942, section 2.2). With
  `MapIPv4MappedAddresses` on, as for addresses from dual-mode sockets, an address lookup treats a mapped address as the
  IPv4 address it holds, strictly: it gets exactly the answer the IPv4 address gets. The queries about networks follow
  the address lookups: an IPv6 network inside `::ffff:0:0/96` gets the answers of the IPv4 network it holds, so
  `::ffff:10.1.0.0/112` gets those of `10.1.0.0/16`, and a network that contains all of `::ffff:0:0/96`, such as `::/0`,
  contains every IPv4 prefix too: `GetSubnets` lists them first, as enumeration does, and `Overlaps` counts them. Writes
  and the exact lookups take a prefix as given, so with mapping on, adding a prefix inside `::ffff:0:0/96` throws
  `ArgumentException`: no address lookup could ever return it.
- **Overlap is not allocation.** A stored default route overlaps every network of its family, and a pool overlaps the
  networks taken from it. To find whether anything is allocated inside a network, ask `GetSubnets(network).Any()`.

### Read-write and frozen lookups

```csharp
public class IPPrefixLookup<TPrefix, TAdapter, TValue> : IReadOnlyIPPrefixLookup<TPrefix, TValue>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>
{
    public IPPrefixLookup(IPLookupOptions? options = null);
    public IPPrefixLookup(IEnumerable<KeyValuePair<TPrefix, TValue>> items, IPLookupOptions? options = null);   // one pass

    public IPLookupOptions Options { get; }
    public long Version { get; }

    public bool TryAdd(TPrefix prefix, TValue value);
    public void Add(TPrefix prefix, TValue value);
    public bool AddOrUpdate(TPrefix prefix, TValue value);
    public TValue GetOrAdd(TPrefix prefix, Func<TPrefix, TValue> valueFactory);
    public bool TryUpdate(TPrefix prefix, TValue newValue, TValue expectedValue);
    public bool Remove(TPrefix prefix);
    public bool Remove(TPrefix prefix, out TValue value);
    public bool TryRemove(TPrefix prefix, TValue expectedValue);
    public void Clear();

    public Batch BeginBatch();
    public FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> Freeze(FrozenBitTrieOptions? options = null);   // both families
    public FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> Freeze(FrozenBitTrieOptions ipv4, FrozenBitTrieOptions ipv6);
    public IPLookupStatistics GetStatistics();
    public string ToTextTree(AddressFamily family);
    public string ToDotTree(AddressFamily family);
}

public class FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> : IReadOnlyIPPrefixLookup<TPrefix, TValue>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>
{
    protected internal FrozenIPPrefixLookup(IPPrefixLookup<TPrefix, TAdapter, TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6);
    public IPLookupOptions Options { get; }    // those of the lookup it was frozen from
    public long Bytes { get; }
}

public readonly record struct IPLookupStatistics(BitTrieStatistics IPv4, BitTrieStatistics IPv6);
```

- **Null prefixes** are rejected with `ArgumentNullException`, without boxing struct prefixes.
- **Batches** begin a batch on both families. On commit each family publishes atomically, IPv4 first, so a reader can
  see the new IPv4 prefixes a moment before the new IPv6 ones. If either half's writes failed, nothing is published.
- **Freezing** copies both families under both writer locks, so a frozen copy is the lookup at one moment.
  `Freeze(options)` applies one layout to both families; `Freeze(ipv4, ipv6)` takes one for each.
- **Shared code.** The read-only members of the two classes are written once, in an internal struct that each class
  forwards to, so no infrastructure type is public.
- **Adapter packages** derive their own pair: the frozen class's protected constructor takes the read-write lookup to
  copy.

Concrete classes hide the generic parameters, because C# cannot infer the key and adapter types. Each read-write class
redeclares `Freeze` with `new` to return its own frozen class:

| Package | Read-write | Frozen | Adapter |
| --- | --- | --- | --- |
| Core package | `IPNetworkLookup<TValue>` | `FrozenIPNetworkLookup<TValue>` | `IPNetworkAdapter` for `System.Net.IPNetwork` |
| `NetworkPrefix` | `NetworkPrefixLookup<TValue>`, `NetworkPrefixLookup` | `FrozenNetworkPrefixLookup<TValue>`, `FrozenNetworkPrefixLookup` | `NetworkPrefixAdapter` |
| `IPNetwork2` | `IPNetwork2Lookup<TValue>` | `FrozenIPNetwork2Lookup<TValue>` | `IPNetwork2Adapter` for `System.Net.IPNetwork2` |
| `MaxMind` | `MaxMindNetworkLookup<TValue>` | `FrozenMaxMindNetworkLookup<TValue>` | `MaxMindNetworkAdapter` for `MaxMind.Db.Network` |

[Adapter-Survey.md](Adapter-Survey.md) explains why these libraries get packages, and which popular types need none.

### Lookups without values

`NoValue` is an empty struct for sets of prefixes. `NoValueLookupExtensions` adds `TryAdd(prefix)` and `Add(prefix)`
to any read-write lookup whose value type is `NoValue`, and `TryGetMatch(address, out prefix)` to any read-only one.
`ContainsMatch`, the question a set is usually asked, is a member of both read-only interfaces: it stops at the
answer and reads no entry or value, so it is faster than a match. The `NetworkPrefix` package's value-less
`NetworkPrefixLookup` keeps the version 2 class name.

### MaxMind databases

```csharp
public static class MaxMindDatabase
{
    public static MaxMindNetworkLookup<T> Load<T>(string path, MaxMindLoadOptions? options = null) where T : class;
    public static MaxMindNetworkLookup<T> Load<T>(Reader reader, MaxMindLoadOptions? options = null) where T : class;
}

public sealed record MaxMindLoadOptions
{
    public bool IncludeAliasedNetworks { get; init; }   // also store MaxMind's alias networks
    public int CacheSize { get; init; } = 16384;
    public InjectableValues? Injectables { get; init; }
    public IPLookupOptions? LookupOptions { get; init; } // default: mapping on
}
```

The loader reads every network through `Reader.FindAll<T>` into one batch. MaxMind's IPv6 databases keep the IPv4
networks under `::/96` and point three alias networks at them; the loader stores the IPv4 networks once, as IPv4, and
the lookup maps IPv4-mapped addresses, so lookups answer as MaxMind's reader does. `IncludeAliasedNetworks` also stores
the copies under `::/96`, `2001::/32` and `2002::/16`, and under `::ffff:0:0/96` when the lookup does not map those
addresses, so that every address answers as MaxMind's reader does. Databases must be trusted:
a hostile file can expand into a huge number of networks.

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

Many changes that readers should see at once:

```csharp
using (var batch = routes.BeginBatch())
{
    foreach (var (prefix, asn) in update)
        routes.AddOrUpdate(prefix, asn);
    batch.Commit();
}
```

A table loaded once, then only read, and reloaded without stopping readers:

```csharp
private volatile FrozenIPNetworkLookup<int> _origins = Load();

static FrozenIPNetworkLookup<int> Load() => new IPNetworkLookup<int>(LoadPrefixes()).Freeze();

void Reload() => _origins = Load();   // readers switch on their next lookup

// on many threads
if (_origins.TryGetMatchValue(packet.SourceAddress, out var asn)) Count(asn);
```

One routing table per VRF:

```csharp
var vrfs = new Dictionary<int, IPNetworkLookup<int>>();
vrfs[7] = new IPNetworkLookup<int>(LoadRoutes(vrf: 7));
vrfs[7].TryGetMatchValue(destination, out var nextHop);
```

What lies inside a block, and whether a new rule collides with an existing one:

```csharp
foreach (var (network, owner) in allocations.GetSubnets(IPNetwork.Parse("10.1.0.0/16")))
    Console.WriteLine($"{network} {owner}");

bool free = !allocations.GetSubnets(candidate).Any();   // nothing allocated inside it
bool conflict = rules.Overlaps(newRule);                // some rule contains it or lies inside it
```

## How the interface got here

The interface was first proposed for review, before the read-write core was chosen. Where the shipped interface
differs from that proposal:

| Part | Proposed | Shipped, and why |
| --- | --- | --- |
| Read-write core | Chunked storage with continuous cleanup, `BitTrieOptions` for capacity and index tuning, `Compact()` | Built on the BART-style design measured as `BartTrie` ([Bart-Comparison.md](Bart-Comparison.md)): copy-on-write multibit nodes, bounded writes with nothing to tune or compact. No options, no `Compact`, no garbage counts in `BitTrieStatistics` |
| Key interface | Also `CommonPrefixLength`, `SplitFor`, `IsSet` and `FromTopBits`, open to user key types | Those four removed: a binary-trie core needed them, the multibit core does not. The members are internal, so only the library's key types implement it, because the tries rely on them for memory safety |
| Key types | `Bits32`, `Bits64`, `Bits128`, `Bits192` | `Bits32` and `Bits128`. `Bits192` was for a VRF in front of the address, which one lookup per context serves better; `Bits64` was for keys such as MAC prefixes, which come in a few fixed lengths that a hash table handles |
| Exact lookups | `TryGetValue(bits, length, out value)` | Also an overload that returns the prefix object |
| Frozen root | 16 or 18 bits always | Sized to the number of prefixes unless `RootBits` is given, so small and empty tables stay small |
| Frozen layout | Leaf-pushed unless the caller chose otherwise | Chosen from the data unless the caller chooses, because leaf-pushed tables can grow to gigabytes on scattered prefixes |
| Freezing | `Freeze(ipv4 = null, ipv6 = null)` | `Freeze(options)` for both families and `Freeze(ipv4, ipv6)` for one each: the single method let `Freeze(compressed)` compress only IPv4. Writers wait only while the prefixes are copied |
| IPv4-mapped addresses | Not covered | `IPLookupOptions.MapIPv4MappedAddresses`, off by default and on for MaxMind lookups, with prefixes inside `::ffff:0:0/96` rejected while it is on. The queries about networks follow it, so that `TryGetMatch(::ffff:10.1.2.3/128)` answers as the address `::ffff:10.1.2.3` does |
| Queries about networks | `TryGetMatch` for a network | Also `GetMatches`, `GetSubnets` and `Overlaps` for a network, in both read-only interfaces, as Go's BART has. A frozen trie keeps each prefix's bits to answer the last two |
| Null state | Plain `out` parameters | `[MaybeNullWhen(false)]` on every `Try…` method |
| Address conversions | Unchecked | `IPAddressBits.ToIPv4Bits` and `ToIPv6Bits` throw for an address of the other family |
| Shared code of the IP lookups | Not covered | First a public base class, then an internal struct each class forwards to, so that no infrastructure type is public |
| Ranges | `IPRangeLookup` and an `IPAddressRange` adapter package | Built, then removed: the owner had not asked for them, and they added a second write path whose writes were not bounded when many ranges shared a piece. Ranges with arbitrary ends are left to a separate library |
| MaxMind | An adapter | Also `MaxMindDatabase.Load<T>`, with alias networks loaded only on request |

## Open questions

- **Whether `Add` should exist** next to `TryAdd`, `AddOrUpdate` and `GetOrAdd`. It is there for familiarity with
  `Dictionary`.
- **Further operations** suggested by the review, not yet decided: one `Batch` type for every lookup. Overlap tests between two whole tables were left out: looping over the smaller
  table with `Overlaps` answers the same question.
