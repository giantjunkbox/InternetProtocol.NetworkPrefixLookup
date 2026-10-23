# Changelog

## 3.0.0

Version 3 is a new design with a new interface. The core is a multibit trie that works on bits only, the IP layer sits
on top of it, and adapter packages connect other libraries' network types.

### Added

- Frozen tables, built by `Freeze`, with faster lookups and a layout chosen from the data.
- Lookups from bytes and integers, for callers that never create an `IPAddress`.
- Queries about networks: `TryGetMatch`, `GetMatches`, `GetSubnets` and `Overlaps` for a network.
- `ContainsMatch`, `TryGetMatchValue`, `IPv4Count` and `IPv6Count`.
- Batches through `BeginBatch` and `WriteBatch`, and the conditional writes `TryUpdate`, `TryRemove` and
  `Remove(network, out value)`.
- Constructors that build a table in one pass from key-value pairs or from another lookup's entries.
- `IPLookupOptions.MapIPv4MappedAddresses`, for addresses from dual-mode sockets.
- The `IPNetwork2` and `MaxMind` adapter packages, and `MaxMindDatabase.Load` for whole MaxMind DB files.
- `BitTrie` and `FrozenBitTrie` for keys other than IP addresses, with `Bits32` and `Bits128` keys.
- Support for trimming and native AOT, Source Link, and symbol packages.

### Changed

- The packages target .NET 10 only.
- Every type is in the namespace `Bitvantage.InternetProtocol.NetworkPrefixLookup`.
- Readers take no locks while a writer works, and writes are serialized internally.

### Removed

- The `NetworkPrefix` type is no longer supported. `System.Net.IPNetwork` covers what it did.
- `ConcurrenceNetworkPrefixLookup`, which the lock-free readers make unnecessary.

### Upgrading from 2.x

| Version 2 | Version 3 |
| --- | --- |
| `using Bitvantage.InternetProtocol;` | `using Bitvantage.InternetProtocol.NetworkPrefixLookup;` |
| `NetworkPrefixLookup<TValue>` with `NetworkPrefix` | `IPNetworkLookup<TValue>` with `System.Net.IPNetwork` |
| `NetworkPrefixLookup`, without values | `IPNetworkLookup<NoValue>` |
| `ConcurrenceNetworkPrefixLookup` | `IPNetworkLookup<TValue>`, which any number of threads can read while one writes |
| `GetMatch(address)`, returning an item | `TryGetMatch(address, out network, out value)` |
| `GetMatches(address)` | `GetMatches(address)`, returning `PrefixEntry` records |
| `TryRemove(prefix)` | `Remove(prefix)`. `TryRemove(prefix, expectedValue)` removes a prefix only when its value matches |
| `ToTextTree(IPVersion)`, `ToDotSvgTree` | `ToTextTree(AddressFamily)` and `ToDotTree(AddressFamily)`, rendered with Graphviz |
