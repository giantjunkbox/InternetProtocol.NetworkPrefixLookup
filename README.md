# Bitvantage.InternetProtocol.NetworkPrefixLookup

Fast longest-prefix-match lookups for IPv4 and IPv6: given an address, find the most specific stored network that
contains it, and its value. Suited to routing tables, packet classification, access lists and GeoIP.

- **Lookups allocate nothing**, from `IPAddress` objects, byte spans or integers.
- **Read-write tables** that any number of threads can read without locks while one thread writes. Each lookup sees the
  table as it was at one moment, never part of a write, and every write does a bounded amount of work, so there are no
  pauses for growth or compaction.
- **Frozen tables** for data that is loaded once and then only read, with lookups up to 1.8 times as fast again.
- **Your network type**: `System.Net.IPNetwork` is built in, and small packages add `NetworkPrefix`, `IPNetwork2` and
  MaxMind databases.
- **No dependencies** in the core package. Targets .NET 10.

## Packages

| Package | For |
| --- | --- |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup` | `System.Net.IPNetwork`, prefixes of your own types, and keys other than IP addresses |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.NetworkPrefix` | `Bitvantage.InternetProtocol.NetworkPrefix`, with the class names of version 2 |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.IPNetwork2` | `System.Net.IPNetwork2` from the IPNetwork2 package |
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
    Console.WriteLine(match.Prefix); // 10.1.0.0/16, then 10.0.0.0/8
```

## Looking up

| Method | Finds |
| --- | --- |
| `TryGetMatch(address, out prefix, out value)` | The most specific stored network that contains an address |
| `TryGetMatchValue(address, out value)` | The same, when only the value is needed; slightly faster |
| `TryGetMatchIPv4(uint, …)`, `TryGetMatchIPv6(UInt128, …)`, `TryGetMatch(ReadOnlySpan<byte>, …)` | The same from an integer or bytes in network order, for callers such as packet parsers that never build an `IPAddress` |
| `GetMatches(address)` | Every stored network that contains an address, most specific first; an overload fills a span instead |
| `TryGetMatch(network, out prefix, out value)` | The most specific stored network that contains another network, which may be the network itself |
| `TryGetValue(network, out value)`, `ContainsPrefix(network)` | Exactly this network, as a dictionary would |
| `ContainsMatch(address)` | Whether any stored network contains an address |

`Count` and enumeration cover both address families: the IPv4 networks, then the IPv6 ones, each in address order. The
scope ID of an IPv6 address, as in `fe80::1%7`, is ignored, so link-local rules cannot be told apart by interface.

### Addresses from dual-mode sockets

A socket that accepts both IPv4 and IPv6 connections reports an IPv4 client as an IPv4-mapped IPv6 address, such as
`::ffff:203.0.113.5`. By default the lookups take every address as given, so such an address only matches IPv6
networks. When your addresses come from such a socket, turn mapping on, and `::ffff:203.0.113.5` gets exactly the answer
of `203.0.113.5`:

```csharp
var acl = new IPNetworkLookup<bool>(new IPLookupOptions { MapIPv4MappedAddresses = true });
```

Leave it off for addresses read from packets: there a mapped address is malformed or crafted, and treating it as IPv4
would let it match IPv4 rules. MaxMind lookups turn mapping on by default, because MaxMind's own reader works that way.

## Tables that change

Writes follow dictionary rules: `TryAdd`, `Add`, `AddOrUpdate`, `GetOrAdd`, `Remove` and `Clear`.

- **Reads never wait.** Any number of threads can look up while writes happen. Lookups take no locks, and each sees the
  table as it was at one moment: before or after every write, never in between.
- **Writes are serialized** by an internal lock, so several threads can write without coordinating.
- **Every write does bounded work.** A write copies only the small node it changes, and the garbage collector frees
  the old one, so there are no pauses for growth or compaction however large the table gets.

A batch publishes many changes at once, and is the fastest way to load a table:

```csharp
using (var batch = routes.BeginBatch())
{
    foreach (var (prefix, asn) in feed)
        routes.AddOrUpdate(prefix, asn);
    batch.Commit(); // readers see all of it from here; disposing without Commit discards it
}
```

Each address family commits all at once, IPv4 a moment before IPv6. Constructing a lookup from a sequence loads it as
one batch.

A batch holds the writer lock until it ends, so end it promptly: other threads' writes wait meanwhile. The lock belongs
to the thread that started the batch, so a batch cannot span an `await`. Ending it on another thread throws and leaves
it open. If a write inside the batch throws, `Commit` discards the batch and throws, since the write may have left it
half done.

`Version` changes with every published change, so cached results can be checked with one comparison. Read it before the
lookups it is to vouch for.

## Tables that are only read

`Freeze` builds a read-only copy with faster lookups:

```csharp
FrozenIPNetworkLookup<int> origins = new IPNetworkLookup<int>(LoadPrefixes()).Freeze();

// on many threads
if (origins.TryGetMatchValue(packet.SourceAddress, out var asn)) Count(asn);
```

To change a frozen table, change the read-write one, freeze it again and swap the reference; readers switch on their
next lookup. Freezing makes writers wait only while it copies the prefixes out, never readers.

A frozen table has one of two layouts. Leaf-pushed is the fastest for clustered tables such as routing tables.
Compressed uses much less memory, and is also faster when prefixes are scattered across the address space, such as
random host routes, for which leaf-pushed tables can grow very large: 100,000 random IPv6 /128 prefixes take 1.4 GB
leaf-pushed against 56 MB compressed. By default `Freeze` chooses for each address family from its data, so a table of
scattered prefixes gets the compressed layout. To choose yourself:

```csharp
var small = routes.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });
```

`Freeze(ipv4, ipv6)` takes different options for each address family.

## MaxMind databases

```csharp
using Bitvantage.InternetProtocol;
using MaxMind.Db;

var countries = MaxMindDatabase.Load<Dictionary<string, object>>("GeoLite2-Country.mmdb").Freeze();
countries.TryGetMatchValue(IPAddress.Parse("81.2.69.160"), out var record);
```

On MaxMind's test database the read-write lookup answered about 80 times as fast as MaxMind's own reader, and the frozen
one about 180 times as fast. The loader stores each IPv4 network once. MaxMind's IPv6 databases also answer some IPv6
addresses from the IPv4 data, through IPv4-compatible, 6to4 and Teredo aliases. Set
`MaxMindLoadOptions.IncludeAliasedNetworks` to load those too, and every address then gets the answer
`Reader.Find` gives, at the cost of storing each IPv4 network four more times.

Load only databases you trust. A database's search tree can point many branches at one subtree, as MaxMind's aliases do,
and a hostile file of a few hundred bytes can expand into millions of networks. The records are shared between networks
and must not be changed.

## Your own network type

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

The adapter's members are static, so the JIT compiles them into the lookups at no cost.

## Sets of networks without values

Use `NoValue` as the value type. The extension methods `TryAdd(prefix)`, `Add(prefix)` and
`TryGetMatch(address, out prefix)` then need no value:

```csharp
var blocked = new IPNetworkLookup<NoValue>();
blocked.Add(IPNetwork.Parse("192.0.2.0/24"));
if (blocked.ContainsMatch(clientAddress)) Reject();
```

## Keys other than IP addresses

The IP lookups are built on `BitTrie<TKey, TPrefix, TValue>` and `FrozenBitTrie<TKey, TPrefix, TValue>`, which work
on bits and lengths only. They are public, with `Bits32` keys for IPv4 and `Bits128` keys for IPv6, for callers who want
one address family without the IP layer. Other fixed-width keys, up to 128 bits, can implement `IBitKey<TSelf>`, which
needs six members.

For context such as a VRF, use one lookup per VRF rather than a wider key. An empty lookup holds only a few KB.

## Performance

One million lookups from `IPAddress` objects in a 4-core cloud container, against version 2 as it shipped on .NET 7,
with `NetworkPrefix` prefixes in both. Version 3 allocates nothing per lookup; version 2 allocated 64 to 104 bytes. The
real tables are the CAIDA RouteViews tables of September 2025; the small ones are 1,000 prefixes sampled from them.

| Table | Version 2 | Version 3 | Version 3, frozen |
| --- | --- | --- | --- |
| Small IPv4, 1,000 prefixes | 187 ms | 20 ms | 13 ms |
| Small IPv6, 1,000 prefixes | 227 ms | 50 ms | 44 ms |
| Random IPv4, 96,944 prefixes | 1,294 ms | 120 ms | 70 ms |
| Real IPv4, 1,054,037 prefixes | 3,315 ms | 220 ms | 141 ms |
| Real IPv6, 239,603 prefixes | 2,330 ms | 503 ms | 388 ms |

From integer addresses, version 3 takes 170 ms on the real IPv4 table, 94 ms frozen, and 74 ms frozen when only the value
is needed.

Loading the real IPv4 table as one batch takes 0.2 seconds and holds 22 MB, against 0.5 seconds and 416 MB in version 2.
The frozen copy holds 53 MB, or 32 MB with the compressed layout. For scattered prefixes the difference is larger: on
the random table the default frozen layout grew to 105 MB, where the compressed one needed 8 MB and was faster.

[Documentation/Architecture.md](Documentation/Architecture.md#performance) has the details, with builds, writes and
memory for every table, and how each number was measured.

## How it works

The read-write table is a multibit trie in the style of Go's BART. Each node decides 8 bits of the key and keeps its
prefixes, child nodes and lone longer prefixes in arrays compressed with 256-bit sets and popcounts. A write copies the
one node it changes and links the copy in with a single reference write, so readers never see half a change. A lookup
that overlaps two writes notices it and looks again. Shortcut blocks, one per busy first octet, let most lookups skip
the first two levels. The shape of the trie depends only on what it holds, never on the order of the writes.

Frozen tables push every answer down into flat lookup tables, so a lookup reads tables and nothing else.

`ToTextTree` and `ToDotTree` show the structure, and `GetStatistics` the memory. The documents in
[Documentation](Documentation) describe the design in detail and record how it was chosen and measured.

## Upgrading from version 2

Version 3 is a new design with a new interface.

| Version 2 | Version 3 |
| --- | --- |
| `NetworkPrefixLookup<TValue>`, `NetworkPrefixLookup` | Same names, in the `….NetworkPrefixLookup.NetworkPrefix` package |
| `ConcurrenceNetworkPrefixLookup` | Not needed: readers take no locks, and writes are serialized internally |
| `GetMatch(address)`, returning an item | `TryGetMatch(address, out prefix, out value)` |
| `GetMatches` | `GetMatches`, returning `PrefixEntry` records |
| `TryRemove` | `Remove` |
| `ToTextTree(IPVersion)`, `ToDotSvgTree` | `ToTextTree(AddressFamily)`, `ToDotTree(AddressFamily)`; render DOT with Graphviz |

## License

[GNU Affero General Public License v3.0](LICENSE.md).
