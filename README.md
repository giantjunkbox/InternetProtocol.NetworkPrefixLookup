# Bitvantage.InternetProtocol.NetworkPrefixLookup

Fast longest-prefix-match lookups for IPv4 and IPv6 networks, and for any other fixed-width bit-string key.

- **Lookups that allocate nothing**, from `IPAddress`, byte spans or integers.
- **Read-write tables** where any number of threads read without locks while one writes, and every write does a bounded
  amount of work: no pauses for growth or compaction.
- **Frozen tables** for data that is loaded once and then only read, with lookups up to 1.8 times as fast again.
- **Your network type**: `System.Net.IPNetwork` is built in, and small packages add `NetworkPrefix`, `IPNetwork2`,
  `IPAddressRange` ranges and MaxMind databases.
- **No dependencies** in the core package. .NET 10.

## Packages

| Package | For |
| --- | --- |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup` | `System.Net.IPNetwork`, address ranges of your own type, and custom keys |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.NetworkPrefix` | `Bitvantage.InternetProtocol.NetworkPrefix` |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.IPNetwork2` | `System.Net.IPNetwork2` from the IPNetwork2 package |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.IPAddressRange` | `NetTools.IPAddressRange` from the IPAddressRange package |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.MaxMind` | `MaxMind.Db` networks, and loading a whole GeoIP2 or GeoLite2 database |

```sh
dotnet add package Bitvantage.InternetProtocol.NetworkPrefixLookup
```

## Quick start

```csharp
using System.Net;
using Bitvantage.InternetProtocol;

var routes = new IPNetworkLookup<int>();
routes.Add(IPNetwork.Parse("10.0.0.0/8"), 64512);
routes.Add(IPNetwork.Parse("10.1.0.0/16"), 64513);
routes.Add(IPNetwork.Parse("2001:db8::/32"), 64514);

// the most specific stored network that contains the address
if (routes.TryGetMatch(IPAddress.Parse("10.1.2.3"), out var network, out var asn))
    Console.WriteLine($"{network} -> AS{asn}"); // 10.1.0.0/16 -> AS64513

// every stored network that contains it, most specific first
foreach (var match in routes.GetMatches(IPAddress.Parse("10.1.2.3")))
    Console.WriteLine(match.Prefix);
```

`TryGetMatchValue` skips the network when only the value is needed. `TryGetMatchIPv4(uint)`, `TryGetMatchIPv6(UInt128)`
and `TryGetMatch(ReadOnlySpan<byte>)` serve callers such as packet parsers that never build an `IPAddress`.
`TryGetValue` and `ContainsPrefix` match a network exactly, as a dictionary does, and `TryGetMatch(network, …)` finds
the most specific stored network that contains another network.

IPv4-mapped IPv6 addresses such as `::ffff:10.1.2.3`, which dual-mode sockets report, are looked up as IPv4. Turn this
off with `new IPLookupOptions { MapIPv4MappedAddresses = false }`.

## Tables that change

Writes follow dictionary rules: `TryAdd`, `Add`, `AddOrUpdate`, `GetOrAdd`, `Remove` and `Clear`. Any number of threads
can look up while writes happen; lookups take no locks, and each sees the table before or after any write, never in
between. Writes are serialized by an internal lock.

A batch publishes many changes at once, and is the fastest way to load a table:

```csharp
using (var batch = routes.BeginBatch())
{
    foreach (var (prefix, asn) in feed)
        routes.AddOrUpdate(prefix, asn);
    batch.Commit(); // readers see all of it from here; disposing without Commit discards it
}
```

Constructing a lookup from a sequence loads it as one batch. `Version` changes with every published change, so cached
results can be checked with one comparison.

## Tables that are only read

`Freeze` builds a read-only copy with faster lookups:

```csharp
FrozenIPNetworkLookup<int> origins = new IPNetworkLookup<int>(LoadPrefixes()).Freeze();

// on many threads
if (origins.TryGetMatchValue(packet.SourceAddress, out var asn)) Count(asn);
```

To change a frozen table, change the read-write one and freeze again, then swap the reference; readers switch on their
next lookup. The default layout is the fastest. The compressed layout uses a fraction of the memory:

```csharp
var small = routes.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });
```

## Ranges

A range lookup stores ranges with a first and a last address, which need not be CIDR blocks, and finds the smallest
stored range that contains an address. Nested ranges work as expected.

```csharp
using NetTools;

var ranges = new IPAddressRangeLookup<string>();
ranges.Add(IPAddressRange.Parse("192.168.0.0/24"), "office");
ranges.Add(IPAddressRange.Parse("192.168.0.10-192.168.0.20"), "printers");

ranges.TryGetMatchValue(IPAddress.Parse("192.168.0.15"), out var name); // "printers"
```

For a range type of your own, implement `IIPRangeAdapter<TRange>` on a struct and use
`IPRangeLookup<TRange, TAdapter, TValue>`.

## MaxMind databases

```csharp
using Bitvantage.InternetProtocol;
using MaxMind.Db;

var countries = MaxMindDatabase.Load<Dictionary<string, object>>("GeoLite2-Country.mmdb").Freeze();
countries.TryGetMatchValue(IPAddress.Parse("81.2.69.160"), out var record);
```

## Other network types

Implement `IIPPrefixAdapter<TPrefix>` on an empty struct, and use `IPPrefixLookup<TPrefix, TAdapter, TValue>`:

```csharp
public readonly struct MyNetworkAdapter : IIPPrefixAdapter<MyNetwork>
{
    public static bool IsIPv4(MyNetwork n) => n.Address.AddressFamily == AddressFamily.InterNetwork;
    public static uint GetIPv4Bits(MyNetwork n) => IPAddressBits.ToIPv4Bits(n.Address);
    public static UInt128 GetIPv6Bits(MyNetwork n) => IPAddressBits.ToIPv6Bits(n.Address);
    public static int GetLength(MyNetwork n) => n.Length;
}

var lookup = new IPPrefixLookup<MyNetwork, MyNetworkAdapter, string>();
```

## The core

The IP lookups are built on `BitTrie<TKey, TPrefix, TValue>` and its frozen form, which work on bits and lengths only,
with `Bits32` keys for IPv4 and `Bits128` keys for IPv6. They are public for callers who want one address family
without the IP layer. Other key types can implement `IBitKey<TSelf>`, up to 128 bits.

For context such as a VRF, use one lookup per VRF rather than a wider key.

## Performance

One million lookups from `IPAddress` objects in a 4-core cloud container, against version 2 as it shipped, on .NET 7,
with `NetworkPrefix` prefixes in both. Version 3 allocates nothing per lookup; version 2 allocated 64 to 104 bytes. The
real tables are the CAIDA RouteViews tables of September 2025; the small ones are 1,000 prefixes sampled from them.

| Table | Version 2 | Version 3 | Version 3, frozen |
| --- | --- | --- | --- |
| Small IPv4, 1,000 prefixes | 187 ms | 20 ms | 13 ms |
| Small IPv6, 1,000 prefixes | 227 ms | 50 ms | 44 ms |
| Random IPv4, 96,944 prefixes | 1,294 ms | 120 ms | 70 ms |
| Real IPv4, 1,054,037 prefixes | 3,315 ms | 220 ms | 141 ms |
| Real IPv6, 239,603 prefixes | 2,330 ms | 503 ms | 388 ms |

From integer addresses, version 3 takes 170 ms on the real IPv4 table, 94 ms frozen and 74 ms frozen when only the
value is needed.

Loading the real IPv4 table as one batch takes 0.2 seconds and holds 22 MB, against 0.5 seconds and 416 MB in
version 2. The frozen copy holds 53 MB, or 32 MB with the compressed layout. Prefer the compressed layout for prefixes
scattered across the address space, such as random /25 to /32 blocks: the default layout grew to 105 MB for the random
table above, where the compressed one needs 8 MB and was faster.

Loaded into a lookup, a MaxMind database answers about 80 times faster than MaxMind's reader read-write, and about 180
times faster frozen, on MaxMind's small test database.

[Documentation/Architecture.md](Documentation/Architecture.md#performance) has the details, including builds and
memory for every table, and how they were measured. Version 2 ran in its own process, back to back with version 3.

## How it works

The read-write table is a multibit trie in the style of Go's BART: each node decides 8 bits of the key and keeps its
prefixes, children and lone longer prefixes in arrays compressed with 256-bit sets and popcounts. A write copies the
one node it changes and links the copy in with a single reference write, so readers never see a half-made change and
the garbage collector frees replaced nodes. Shortcut blocks, one per busy first octet, skip the first two levels of
most lookups, and an empty table holds only a few KB, so one lookup per VRF is cheap. Frozen
tables push every answer down into flat lookup tables, so a lookup reads only tables. The shape of the trie depends
only on what it holds, never on the order of the writes.

`ToTextTree` and `ToDotTree` show the structure. [Documentation/Architecture.md](Documentation/Architecture.md)
describes the design in detail, and the other documents in [Documentation](Documentation) record how it was chosen and
measured.

## Upgrading from version 2

Version 3 is a new design with a new interface:

| Version 2 | Version 3 |
| --- | --- |
| `NetworkPrefixLookup<TValue>`, `NetworkPrefixLookup` | Same names, in the `….NetworkPrefixLookup.NetworkPrefix` package |
| `ConcurrenceNetworkPrefixLookup` | Not needed: every lookup serializes its writes |
| `GetMatch(address)` returning an item | `TryGetMatch(address, out prefix, out value)` |
| `GetMatches` | `GetMatches`, returning `PrefixEntry` records |
| `TryRemove` | `Remove` |
| `ToTextTree(IPVersion)`, `ToDotSvgTree` | `ToTextTree(AddressFamily)`, `ToDotTree(AddressFamily)`; render DOT with Graphviz |
