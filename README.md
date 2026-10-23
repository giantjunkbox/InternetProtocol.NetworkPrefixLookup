# Bitvantage.InternetProtocol.NetworkPrefixLookup

Longest-prefix-match lookups for IPv4 and IPv6 in .NET. Given an address, a lookup finds the most specific stored
network that contains it and the value stored with that network. The library is built for routing tables, access lists,
packet classification and GeoIP data, and it scales to full Internet routing tables.

- **Fast lookups.** On a full routing table of a million IPv4 prefixes, a lookup takes about 100 ns on one 2.8 GHz cloud
  core, and a frozen table returns the value for an integer address in 43 ns. Lookups allocate nothing.
- **Faster than Go's BART.** Lookups of addresses spread across a full routing table are 1.1 to 2.6 times as fast as in
  BART, the Go library whose design the read-write table follows, and writes that concurrent readers can share are 2.3
  to 3.2 times as fast.
- **Reads never wait.** Any number of threads look up without locks while another thread writes. Each lookup sees the
  table as it was at one moment, and every write does a bounded amount of work, so there are no pauses for growth or
  compaction.
- **Frozen tables** for data that is loaded once and then only read.
- **Queries about networks**: the routes that cover a subnet, the networks allocated inside a block, and whether a new
  rule overlaps an existing one.
- **MaxMind databases** load into a lookup that answers 5 to 10 times as fast as MaxMind's own reader.
- **Any network type.** `System.Net.IPNetwork` is built in, two packages add `IPNetwork2` and MaxMind networks, and an
  adapter of four static methods adds your own type.
- **No dependencies** in the core package. It targets .NET 10, supports trimming and native AOT, and ships Source Link
  and symbols.

## Quick start

```sh
dotnet add package Bitvantage.InternetProtocol.NetworkPrefixLookup
```

```csharp
using System.Net;
using Bitvantage.InternetProtocol.NetworkPrefixLookup;

var routes = new IPNetworkLookup<int>
{
    { IPNetwork.Parse("10.0.0.0/8"), 64512 },
    { IPNetwork.Parse("10.1.0.0/16"), 64513 },
    { IPNetwork.Parse("2001:db8::/32"), 64514 },
};

// the most specific stored network that contains the address
if (routes.TryGetMatch(IPAddress.Parse("10.1.2.3"), out var network, out var asn))
    Console.WriteLine($"{network} -> AS{asn}"); // 10.1.0.0/16 -> AS64513

// every stored network that contains it, most specific first
foreach (var match in routes.GetMatches(IPAddress.Parse("10.1.2.3")))
    Console.WriteLine(match.Prefix); // 10.1.0.0/16, then 10.0.0.0/8

// a read-only copy with faster lookups
var snapshot = routes.Freeze();
```

| Package | Adds |
| --- | --- |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup` | Lookups over `System.Net.IPNetwork` and your own network types, and tries over keys other than IP addresses |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.IPNetwork2` | Lookups over `System.Net.IPNetwork2` from the IPNetwork2 package |
| `Bitvantage.InternetProtocol.NetworkPrefixLookup.MaxMind` | Lookups over MaxMind networks, and loading a whole GeoIP2 or GeoLite2 database |

## Performance

Measured on 2026-10-23 on a 4-core cloud container with an Intel Xeon at 2.8 GHz and .NET 10, with the CAIDA RouteViews
tables of September 2025: 1,054,037 IPv4 and 239,598 IPv6 prefixes. Each of a million lookups is for a random address
inside a randomly chosen prefix, so most memory reads miss the CPU cache, as they do in a busy server. The times are the
median per lookup.

| Lookup | Read-write, IPv4 | Read-write, IPv6 | Frozen, IPv4 | Frozen, IPv6 |
| --- | --- | --- | --- | --- |
| `TryGetMatch` from an `IPAddress`, returning the network and the value | 107 ns | 204 ns | 83 ns | 208 ns |
| `TryGetMatchValue` from a `uint` or `UInt128` | 86 ns | 168 ns | 43 ns | 103 ns |
| `ContainsMatch` from an `IPAddress` | 39 ns | 91 ns | | |

With the compressed layout, the frozen IPv6 table returns a value in 68 ns. When the addresses and the parts of the
table they reach stay in cache, a read-write lookup from an integer takes 10 ns for IPv4 and 29 ns for IPv6, and a frozen
one 4 ns and 14 ns. No lookup allocates.

**Memory.** The read-write table holds 32 MB for the IPv4 table and 14 MB for the IPv6 table. Each stored `IPNetwork`
adds an `IPAddress` object of 40 bytes for an IPv4 prefix and 80 bytes for an IPv6 prefix. A frozen copy holds 68 MB for
IPv4 and 65 MB for IPv6 with the leaf-pushed layout, which `Freeze` chooses for these tables, or 46 MB and 17 MB with the
compressed layout.

**Writes.** Building a table from a sequence of prefixes takes 0.14 s for the IPv4 table and 0.05 s for the IPv6 table.
A single write takes 0.5 to 0.6 µs when the prefixes arrive in address order and 3 to 4.5 µs for random changes to a full
table. Each write allocates 1.1 to 1.8 KB, because it copies the node it changes instead of changing it under readers.

**Compared with Go's BART.** Both libraries ran on the same machine with the same tables and addresses, and returned the
same answers. BART takes `netip.Addr` values; this library took `IPAddress` objects. Each cell gives IPv4, then IPv6.

| | BART `Table` | This library, read-write | This library, frozen |
| --- | --- | --- | --- |
| Lookup returning the value | 199 / 246 ns | 99 / 221 ns | 75 / 148 ns |
| Lookup returning the network and the value | 279 / 303 ns | 107 / 204 ns | 83 / 208 ns |
| Contains | 84 / 126 ns | 39 / 91 ns | |
| Memory | 24 / 10 MB | 32 / 14 MB, plus the `IPNetwork` objects | |
| A write that concurrent readers can share | 10.4 / 9.3 µs | 4.5 / 2.9 µs | |

BART uses less memory, and its in-place writes take 0.4 to 0.5 µs when nothing reads the table at the same time. Its
`Fast` table beats this library's read-write table on IPv6 when everything stays in cache.
[Bart-Comparison.md](Documentation/Bart-Comparison.md#against-gos-bart) has the method and every measurement, and
[Architecture.md](Documentation/Architecture.md#performance) has BenchmarkDotNet measurements of the other operations.

## Choosing a table

| Your data | Use |
| --- | --- |
| Changes while it is read, such as a routing table fed by BGP | `IPNetworkLookup<TValue>` |
| Loaded once and then only read, such as a GeoIP or blocklist snapshot | `Freeze()`, which returns a `FrozenIPNetworkLookup<TValue>` |
| Networks without values, such as an access list | `IPNetworkLookup<NoValue>` |
| A MaxMind GeoIP2 or GeoLite2 database | `MaxMindDatabase.Load` |
| `IPNetwork2` networks | `IPNetwork2Lookup<TValue>` |
| Networks of your own type | `IPPrefixLookup<TPrefix, TAdapter, TValue>` with an adapter |
| Keys that are not IP addresses | `BitTrie<TKey, TPrefix, TValue>` |

A frozen table returns values in 0.4 to 0.6 of the read-write time. Every lookup type has the same read-only interface,
`IReadOnlyIPPrefixLookup<TPrefix, TValue>`, so code that only reads works with either form.

## Looking up

| Method | Finds |
| --- | --- |
| `TryGetMatch(address, out prefix, out value)` | The most specific stored network that contains an address |
| `TryGetMatchValue(address, out value)` | The same when only the value is needed, which is slightly faster |
| `TryGetMatchIPv4(uint, …)`, `TryGetMatchIPv6(UInt128, …)`, `TryGetMatch(ReadOnlySpan<byte>, …)` | The same from an integer or from bytes in network order, for callers such as packet parsers that never build an `IPAddress` |
| `ContainsMatch(address)` | Whether any stored network contains an address |
| `GetMatches(address)` | Every stored network that contains an address, most specific first. An overload fills a span instead of a list, and the integer and byte forms exist as well |
| `TryGetMatch(network, out prefix, out value)` | The most specific stored network that contains another network, which may be the network itself |
| `GetMatches(network)` | Every stored network that contains another network, most specific first: the routes that cover a subnet |
| `GetSubnets(network)` | Every stored network inside another network, in address order: what is allocated inside a block. The networks are produced as you enumerate, so `GetSubnets(block).Any()` stops at the first |
| `Overlaps(network)` | Whether any stored network contains another network or lies inside it, such as a new rule that collides with an existing one |
| `TryGetValue(network, out value)`, `ContainsPrefix(network)` | Exactly this network, as a dictionary would |

`Count` and enumeration cover both address families: the IPv4 networks first, then the IPv6 networks, each in address
order. `IPv4Count` and `IPv6Count` count one family each. Lookups ignore the scope ID of an IPv6 address such as
`fe80::1%7`, so link-local rules cannot be told apart by interface.

## Tables that change

Writes follow dictionary rules: `TryAdd`, `Add`, `AddOrUpdate`, `GetOrAdd`, `Remove` and `Clear`. As in
`ConcurrentDictionary`, `TryUpdate` and `TryRemove` change a prefix only when its value is the expected one. The check and
the write happen under the writer lock, so no other thread's write can land between them.

- **Reads never wait.** Any number of threads can look up while writes happen. Lookups take no locks, and each sees the
  table as it was at one moment: before or after every write, never in between.
- **Writes are serialized** by an internal lock, so several threads can write without coordinating.
- **Every write does bounded work.** A write copies only the node it changes, and the garbage collector frees the old
  one, so no write pauses for growth or compaction however large the table gets.

A batch publishes many changes at once:

```csharp
using (var batch = routes.BeginBatch())
{
    foreach (var (prefix, asn) in feed)
        routes.AddOrUpdate(prefix, asn);
    batch.Commit(); // readers see all of it from here; disposing without Commit discards it
}
```

Each address family commits all at once, IPv4 a moment before IPv6. A batch holds the writer lock until it ends, so
other threads' writes wait until then. The lock belongs to the thread that started the batch, so a batch cannot span an
`await`, and ending it on another thread throws and leaves it open. If a write inside the batch throws, `Commit` throws
and discards the batch, because the write may have left it half done.

To load a whole table, pass the prefixes to the constructor. It builds the trie in one pass from prefixes in any order,
1.3 to 4 times as fast as a batch, with a third less allocation. It takes key-value pairs such as a dictionary, or the
entries of another lookup or of a query:

```csharp
var copy = new IPNetworkLookup<int>(routes);
var site = new IPNetworkLookup<int>(routes.GetSubnets(IPNetwork.Parse("10.1.0.0/16")));
```

`Version` changes with every published change, so a cache of lookup results can be checked with one comparison. Read it
before the lookups it is to vouch for.

## Frozen tables

`Freeze` builds a read-only copy with faster lookups:

```csharp
FrozenIPNetworkLookup<int> origins = new IPNetworkLookup<int>(LoadPrefixes()).Freeze();

// on many threads
if (origins.TryGetMatchValue(sourceAddress, out var origin))
    Count(origin);
```

To change a frozen table, change the read-write table, freeze it again and swap the reference. Readers switch on their
next lookup. Freezing makes writers wait only while it copies the prefixes out, and it never makes readers wait.

A frozen table has one of two layouts. The leaf-pushed layout is the fastest for clustered tables such as routing
tables. The compressed layout uses much less memory, and it is also faster when the prefixes are scattered across the
address space, as random host routes are. Leaf-pushed tables of scattered prefixes grow very large: 100,000 random IPv6
/128 prefixes take 1.4 GB leaf-pushed and 56 MB compressed. By default `Freeze` chooses the layout for each address family
from its data. To choose it yourself:

```csharp
var small = routes.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });
```

`Freeze(ipv4, ipv6)` takes separate options for each address family.

## MaxMind databases

MaxMind's `Reader` maps a database file and, on every lookup, walks the file's search tree one bit at a time and decodes
the record it reaches. `MaxMindDatabase.Load` decodes every record once and builds a lookup from the networks:

```csharp
using System.Net;
using Bitvantage.InternetProtocol.NetworkPrefixLookup;

var countries = MaxMindDatabase.Load<Dictionary<string, object>>("GeoLite2-Country.mmdb").Freeze();
if (countries.TryGetMatchValue(IPAddress.Parse("81.2.69.160"), out var record))
    Console.WriteLine(((Dictionary<string, object>)record["country"])["iso_code"]); // GB
```

On a database the size of a full routing table, 1,240,422 IPv4 and 413,309 IPv6 networks written from the RouteViews
tables:

| | MaxMind `Reader.Find` | Read-write lookup | Frozen lookup |
| --- | --- | --- | --- |
| Time per lookup | 835 ns | 149 ns | 86 ns |
| Allocated per lookup | 344 bytes | Nothing | Nothing |

The lookup costs time to load and memory to hold. That database took 2.4 to 4.2 seconds to load and the read-write lookup
held 225 MB, where the reader opens at once and maps the 19 MB file. The lookup pays off when an application looks up
many addresses, such as a server that checks every request or a job that processes logs. For a few lookups, or where
memory is tight, MaxMind's reader is the better choice.

The lookup also answers questions the reader cannot, through the queries about networks, and it can hold networks of your
own next to MaxMind's or replace some of them before you freeze it.

MaxMind lookups map IPv4-mapped addresses such as `::ffff:81.2.69.160` to IPv4 by default, as MaxMind's reader does.
MaxMind's IPv6 databases also answer some IPv6 addresses from the IPv4 data, through IPv4-compatible, 6to4 and Teredo
aliases. Set `MaxMindLoadOptions.IncludeAliasedNetworks` to load those too. Every address then gets the answer that
`Reader.Find` gives, and each IPv4 network is stored three more times: under `::/96`, `2001::/32` and `2002::/16`.

Load only databases you trust. A database's search tree can point many branches at one subtree, as MaxMind's aliases do,
so a hostile file of a few hundred bytes can expand into millions of networks. The records are shared between networks
and must not be changed.

## IPNetwork2 and your own network types

The IPNetwork2 package works the same way:

```csharp
using System.Net;
using Bitvantage.InternetProtocol.NetworkPrefixLookup;

var zones = new IPNetwork2Lookup<string> { { IPNetwork2.Parse("192.168.0.0/16"), "office" } };
```

For a network type of your own, implement `IIPPrefixAdapter<TPrefix>` on an empty struct and use
`IPPrefixLookup<TPrefix, TAdapter, TValue>`:

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

## More features

### Addresses from dual-mode sockets

A socket that accepts both IPv4 and IPv6 connections reports an IPv4 client as an IPv4-mapped IPv6 address, such as
`::ffff:203.0.113.5`. By default the lookups take every address as given, so such an address only matches IPv6
networks. When your addresses come from such a socket, turn mapping on, and `::ffff:203.0.113.5` gets exactly the answer
of `203.0.113.5`:

```csharp
var acl = new IPNetworkLookup<bool>(new IPLookupOptions { MapIPv4MappedAddresses = true });
```

Leave mapping off for addresses read from packets. There a mapped address is malformed or crafted, and treating it as
IPv4 would let it match IPv4 rules.

With mapping on, the queries about networks follow the addresses: `::ffff:10.1.0.0/112` gets the answers of
`10.1.0.0/16`, and a network that contains all of `::ffff:0:0/96`, such as `::/0`, contains every IPv4 network too. An
IPv6 prefix inside `::ffff:0:0/96` could never match an address, so adding one throws `ArgumentException`. Add the IPv4
prefix instead.

### Sets of networks without values

Use `NoValue` as the value type. The extension methods `TryAdd(prefix)`, `Add(prefix)` and
`TryGetMatch(address, out prefix)` then need no value:

```csharp
var blocked = new IPNetworkLookup<NoValue> { IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("2001:db8::/32") };
if (blocked.ContainsMatch(clientAddress))
    Reject();
```

### Keys other than IP addresses

The IP lookups are built on `BitTrie<TKey, TPrefix, TValue>` and `FrozenBitTrie<TKey, TPrefix, TValue>`, which work on
bits and lengths only. Both are public, with `Bits32` keys for IPv4 and `Bits128` keys for IPv6, for callers who want one
address family without the IP layer, or any other key that fits in 32 or 128 bits:

```csharp
var trie = new BitTrie<Bits32, string, int>();
trie.Add(new Bits32(0x0A000000), 8, "10.0.0.0/8", 1);
trie.TryGetMatch(new Bits32(0x0A010203), out var prefix, out var value); // "10.0.0.0/8", 1
```

`Bits32` and `Bits128` are the only key types. The trie trusts its keys' bit arithmetic for memory safety, so no other
assembly can implement `IBitKey<TSelf>`. For context such as a VRF, use one lookup per VRF rather than a wider key. An
empty lookup holds only a few KB.

### Inspecting a table

`ToTextTree` prints the trie of one address family, and `ToDotTree` writes it in the DOT language for Graphviz.
`GetStatistics` reports the number of nodes and the bytes they hold.

## How it works

The read-write table follows BART, the Balanced Routing Table by Karl Gaissmaier, a multibit trie for Go.

- **Nodes of 8 bits.** Each node decides one octet of the address, so an IPv4 lookup passes at most 4 nodes and an IPv6
  lookup at most 16.
- **Prefixes inside a node.** A prefix that ends 0 to 7 bits into a node's octet takes one of 255 positions in a
  complete binary tree, numbered as in Donald Knuth's Allotment Routing Table: position 1 is the prefix of length 0,
  which covers every value of the octet, and position i has the children 2i and 2i + 1. A 256-bit set marks the positions that hold a prefix, and an array holds
  one slot per set bit. The slot of a position is the number of set bits below it, which one popcount instruction
  counts, so a node stores only the prefixes it has.
- **The longest match in one step.** For each of the 256 values of an octet, a precomputed 256-bit set holds the
  positions that contain it. The AND of that set and the node's set leaves the prefixes that contain the address, and
  the highest remaining bit is the longest of them.
- **Fringes and leaves.** A prefix that ends exactly at the end of a node's octet, such as a /24 in the node for the
  third octet, is stored as a fringe in that node. A single longer prefix below an octet is stored whole as a leaf.
  Neither needs a node of its own, so a typical /24 or an isolated long prefix costs one array slot. Child nodes are
  found with a 256-bit set and a popcount in the same way, and a child exists only where two or more prefixes lie below
  an octet. The shape of the trie therefore depends only on the prefixes it holds.
- **Lookups** descend as far as the address leads, then check the nodes from the deepest one up. The first match is the
  longest.

This library adds three things to that design:

- **Shortcut blocks.** For each first octet that leads to a node, a block of 256 references points at the nodes two
  levels down, so most lookups start at the third level after one read.
- **Copy-on-write with lock-free readers.** A write copies the one node it changes, finishes the copy, and links it in
  with a single reference write, so readers never see half a change and never take a lock. A lookup that overlaps two
  writes notices it through a change counter and looks again.
- **Frozen tables.** `Freeze` flattens the trie into tables indexed by the address bits. The leaf-pushed layout pushes
  every answer down to the end of its path, so a lookup reads a 16-bit root table and one 8-bit table for each further
  octet that still needs deciding, and reads nothing else. The compressed layout stores the same answers in nodes of 6
  bits with popcount-compressed bitmaps, as Poptrie does, and needs far less memory for scattered prefixes.

[Architecture.md](Documentation/Architecture.md) describes the design in detail, and the other documents in
[Documentation](Documentation) record how it was chosen and measured.

## Credits

- **BART** by Karl Gaissmaier, [github.com/gaissmai/bart](https://github.com/gaissmai/bart), MIT License. The read-write
  trie takes its node layout, its use of the ART numbering, and its fringes and leaves from BART.
- **The Allotment Routing Table** by Donald E. Knuth, and Yoichi Hariguchi's description of it, which BART builds on.
- **Poptrie** by Hirochika Asai and Yasuhiro Ohara: "Poptrie: A Compressed Trie with Population Count for Fast and
  Scalable Software IP Routing Table Lookup", ACM SIGCOMM 2015. The compressed frozen layout follows it.
- **Leaf pushing** by V. Srinivasan and George Varghese: "Fast Address Lookups Using Controlled Prefix Expansion", ACM
  Transactions on Computer Systems, volume 17, number 1, 1999. The leaf-pushed frozen layout follows it.
- **The CAIDA UCSD RouteViews Prefix-to-AS mappings dataset** for IPv4 and IPv6 supplies the real tables that the
  benchmarks use.
- **MaxMind** publishes the MaxMind DB format and the test databases in `Test/Data/MaxMind`, under the Apache License 2.0
  or the MIT License.

## License

[GNU Affero General Public License v3.0](LICENSE.md). Changes between releases are listed in
[CHANGELOG.md](CHANGELOG.md).
