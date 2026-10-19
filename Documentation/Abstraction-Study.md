# Study: Decoupling the Trie from NetworkPrefix

This study looks at what it would take to make the library independent of `Bitvantage.InternetProtocol.NetworkPrefix`.
The goal is for the library to work with any network type, such as the built-in `System.Net.IPNetwork` struct, while it
focuses only on bits: storing them, structuring them, and finding them. The hard constraint is that performance must
not get worse.

The design was tested with a prototype in [`Prototypes/GenericTrie`](../Prototypes/GenericTrie). It was measured
against the current library on .NET 10.

## Summary

- **It is feasible.** The trie never needs to know what a network is. It needs a bit string and a length, and every
  operation it performs can be done with integer bit arithmetic.
- **The abstraction costs nothing.** A prototype that keeps today's node layout and algorithm, and swaps only the
  network type for a generic key, built the identical trie and returned identical results for one million lookups.
  It was not slower. The strict like-for-like variant ran lookups at 0.84 ± 0.08 of the current time.
- **The redesign it enables is much faster.** With 32-bit keys for IPv4, values stored in the node and bit arithmetic
  instead of `NetworkPrefix` methods, the prototype ran lookups about twice as fast and builds about four times as
  fast. It also kept less than half the memory per prefix.
- **`System.Net.IPNetwork` works through a 4-line adapter**, for IPv4 and IPv6, with the same performance as
  `NetworkPrefix`.
- **The main open decisions** are the package layout, how much of the v2 API to keep, and whether keys wider than 128
  bits need to be supported. They are listed under [Decisions needed](#decisions-needed).

## What the library uses from NetworkPrefix today

| Category | Members used | Where | What replaces it |
| --- | --- | --- | --- |
| Bits | `NetworkBits`, `NetworkMaskBits`, `Length`, `AddressLength` | Copied into every `Node` | An adapter supplies bits and length. The core computes masks from the length. |
| Address family | `Version`, `IPAddress.AddressFamily` | Choosing `RootV4` or `RootV6` | The IP layer picks one of two cores. The core has no notion of family. |
| Prefix arithmetic | `==`, `Contains`, `GetContainingNetwork` | Insert cases 1, 3 and 4 | Mask comparison and one leading-zero count. No objects are created. |
| Placeholder prefixes | `new NetworkPrefix(...)` for roots and routing nodes, `new NetworkPrefixKey(...)` | Root creation, insert case 4, remove | Routing nodes hold only bits. They need no prefix object at all. |
| Display | `ToString`, `Address`, `Length`, `IPVersion` | `ToTextTree`, `ToDotTree`, debugger display | A formatter for bits and length, supplied by the IP layer. |
| Public API | `NetworkPrefix` parameters, `NetworkPrefixKey` and `NetworkPrefixKeyValuePair<T>` records, the `IPVersion` enum | Every public method | Generic prefix and value parameters, plus compatibility wrappers. |

`IPVersion` is defined in the NetworkPrefix package, so it is a dependency too. `IPAddress` is part of .NET and can
stay.

## What the trie actually needs

A stored key is a **bit string**: an integer holding the bits plus a length. Every trie operation can be written in
terms of those two values:

| Operation | Today | With bits only |
| --- | --- | --- |
| Does a node contain a key? | `NetworkBits == (key & NetworkMaskBits)` | `(key & Mask(length)) == bits` |
| Which child slot? | Bit after the prefix, via `SplitMask` | Same |
| Smallest common parent of two prefixes | `NetworkPrefix.GetContainingNetwork`, which allocates | Length `min(lengthA, lengthB, LeadingZeroCount(a ^ b))` |
| Same prefix? | `NetworkPrefix ==` | Same bits and same length |

The prototype stores bits **left-aligned**: the first bit of the network sits in the most significant bit of the
integer. The current code right-aligns them, which is why every node needs `AddressLength` to find the split bit.
With left alignment, masks and split bits depend only on the prefix length, and any key up to the integer's width
works without the core knowing that width. A 48-bit MAC address prefix fits in a `ulong` without special handling.

## Proposed design

Three layers, each knowing less than the one above it:

```text
Your network type      NetworkPrefix, IPNetwork, a MAC prefix, ...
      │  adapter       small struct: "give me the bits and the length"
      ▼
Domain layer           IP: chooses IPv4 or IPv6, converts IPAddress, formats prefixes for diagnostics
      │
      ▼
Core                   BitTrie<TBits, TPrefix, TValue>: bits, lengths and nodes only
```

### Core

```csharp
public sealed class BitTrie<TBits, TPrefix, TValue>
    where TBits : unmanaged, IBinaryInteger<TBits>   // uint, ulong, UInt128, ...
{
    public bool TryAdd(TBits bits, int length, TPrefix prefix, TValue value);
    public bool TryRemove(TBits bits, int length);
    public bool TryGetMatch(TBits key, out TPrefix prefix, out TValue value);             // longest match for a full key
    public bool TryGetMatch(TBits bits, int length, out TPrefix prefix, out TValue value); // longest match for a prefix
    public int GetMatches(TBits key, ICollection<KeyValuePair<TPrefix, TValue>> results);
    public long Count { get; }
    public void Clear();
}
```

The core stores the caller's prefix object and value in the node, but never looks inside either one. It only hands
them back from lookups.

### Adapters

An adapter is a struct that implements an interface made of `static abstract` members. Adapters are written once per
network type. These two are taken from the prototype:

```csharp
public interface IIPPrefixAdapter<TPrefix>
{
    static abstract bool IsIPv4(TPrefix prefix);
    static abstract uint GetIPv4Bits(TPrefix prefix);    // left-aligned
    static abstract UInt128 GetIPv6Bits(TPrefix prefix); // left-aligned
    static abstract int GetLength(TPrefix prefix);
}

public struct IPNetworkAdapter : IIPPrefixAdapter<IPNetwork>
{
    public static bool IsIPv4(IPNetwork prefix) => prefix.BaseAddress.AddressFamily == AddressFamily.InterNetwork;
    public static uint GetIPv4Bits(IPNetwork prefix) => IPBits.V4(prefix.BaseAddress);
    public static UInt128 GetIPv6Bits(IPNetwork prefix) => IPBits.V6(prefix.BaseAddress);
    public static int GetLength(IPNetwork prefix) => prefix.PrefixLength;
}

public struct NetworkPrefixAdapter : IIPPrefixAdapter<NetworkPrefix>
{
    public static bool IsIPv4(NetworkPrefix prefix) => prefix.AddressLength == 32;
    public static uint GetIPv4Bits(NetworkPrefix prefix) => (uint)prefix.NetworkBits;
    public static UInt128 GetIPv6Bits(NetworkPrefix prefix) => prefix.NetworkBits;
    public static int GetLength(NetworkPrefix prefix) => prefix.Length;
}
```

`IPBits.V4` and `IPBits.V6` copy the address bytes into a stack buffer with `IPAddress.TryWriteBytes` and read them as
a big-endian integer. They do not allocate.

In .NET 10, `IPNetwork` clears host bits on construction and compares by value, so the adapter can trust its base
address. `IPNetwork` is a 16-byte struct, so the core stores it inline in the node.

Network types that are not IP, such as MAC address prefixes, would use a simpler single-family adapter, for example
`IPrefixAdapter<TPrefix, TBits>` with `GetBits` and `GetLength`, on top of one core.

### IP layer

`IPPrefixLookup<TPrefix, TAdapter, TValue>` holds a `BitTrie<uint, ...>` for IPv4 and a `BitTrie<UInt128, ...>` for
IPv6. It routes each call by address family and offers lookups by `IPAddress`. C# has no associated types, so users
would otherwise have to spell out the bit type. Concrete classes hide the generic parameters:

```csharp
public sealed class IPNetworkLookup<TValue> : IPPrefixLookup<IPNetwork, IPNetworkAdapter, TValue> { }

var routes = new IPNetworkLookup<string>();
routes.TryAdd(IPNetwork.Parse("10.0.0.0/8"), "lan");
routes.TryGetMatch(IPAddress.Parse("10.1.2.3"), out var network, out var nextHop);
```

## Why the abstraction does not cost performance

- **The JIT compiles a separate copy of generic code for each value-type argument.** `uint`, `UInt128` and the
  adapter structs are value types. In each copy, `TBits` operators become plain machine instructions and calls such as
  `TAdapter.GetLength(prefix)` become direct calls that can be inlined.
- **Reference-type arguments share one copy of the code.** That applies when the prefix or value is a class, such as
  `NetworkPrefix` or `string`. Sharing is harmless here because the lookup loop never touches the prefix or value. It
  only copies them out of the final node.
- **Things to avoid.** An interface on the key type, virtual methods, `Func<>` delegates or boxing would each put an
  indirect call or an allocation inside the loop. Adapters must be structs, not classes. Static abstract calls on a
  class adapter inside shared code need a runtime lookup.

## Prototype and measurements

[`Prototypes/GenericTrie`](../Prototypes/GenericTrie) contains a generic core with insert and longest-prefix match, the
IP layer, both adapters and a benchmark. It is not part of the solution or the package. To reproduce:

```sh
dotnet run -c Release --project Prototypes/GenericTrie -- verify   # correctness and memory checks
dotnet run -c Release --project Prototypes/GenericTrie             # benchmarks, about 7.5 minutes
```

The data is the same as `PerformanceTest`: 96,944 distinct random IPv4 prefixes and 1,000,000 random addresses.

### Variants

| Variant | Bits for IPv4 | Prefix type | Address conversion | Purpose |
| --- | --- | --- | --- | --- |
| Current | `UInt128` | `NetworkPrefix` | Current `ToUInt128`, which allocates | Baseline |
| Control | `UInt128` | `NetworkPrefix` | A copy of the current `ToUInt128` | Isolates the cost of the abstraction |
| Generic + NetworkPrefix | `uint` | `NetworkPrefix` | `TryWriteBytes`, no allocation | The proposed design |
| Generic + IPNetwork | `uint` | `System.Net.IPNetwork` | `TryWriteBytes`, no allocation | The proposed design with the built-in type |

All prototype variants keep the current node layout, including the separate two-slot child array. That keeps the
comparison about the abstraction rather than about node layout.

### Correctness

| Check | Result |
| --- | --- |
| Value nodes and routing nodes, all variants | 96,944 and 75,986, identical to the current library |
| Matched prefix for 1,000,000 addresses, all variants | Identical to the current library, no mismatches |
| `Count` | 96,944. The current library reports 91,471 because of its known undercount. |
| IPv6 through `IPNetwork` | Longest matches correct for `/0`, `/32` and `/48` test prefixes |

### Speed

BenchmarkDotNet 0.15.8, .NET 10.0.13, one session, 30 iterations per benchmark, on a 4-core Intel Xeon 2.80 GHz cloud
container. Ratios are against the current library in the same session, which is the reliable comparison in this
container.

| Benchmark | Current | Control | Generic + NetworkPrefix | Generic + IPNetwork |
| --- | --- | --- | --- | --- |
| 1M lookups, mean | 1,376 ms | 1,148 ms | 685 ms | 626 ms |
| 1M lookups, ratio | 1.00 | 0.84 ± 0.08 | 0.50 ± 0.08 | 0.46 ± 0.10 |
| 1M lookups, allocated | 61 MB | 61 MB | 0 | 0 |
| Build 97K prefixes, mean | 375 ms | 93 ms | 88 ms | 86 ms |
| Build 97K prefixes, ratio | 1.00 | 0.25 ± 0.03 | 0.24 ± 0.02 | 0.23 ± 0.02 |
| Build 97K prefixes, allocated | 99 MB | 24 MB | 18 MB | 19 MB |

### Memory retained per stored prefix

| Current | Control | Generic + NetworkPrefix | Generic + IPNetwork |
| --- | --- | --- | --- |
| 400 bytes | 243 bytes | 186 bytes | 200 bytes |

These figures exclude the caller's prefix objects, which exist before the build.

### Where the gains come from

The control variant is the fair test of the abstraction, and it is not slower. It is faster for reasons that come with
the new design rather than with generics. These explanations follow from the code; they were not measured one by one.

- **The value is stored in the node.** The current library stores a `NetworkPrefixKeyValuePair` record and casts it on
  every lookup. Reading the value then follows one more pointer to a randomly placed record, which is a likely cache
  miss.
- **Inserts create far less garbage.** There is no record per prefix and no `NetworkPrefix` from
  `GetContainingNetwork`, and the prototype walks the trie without building a `List`. On .NET 10, less garbage means
  fewer Gen1 collections; see the runtime comparison in [Architecture](Architecture.md#net-7-versus-net-10).

The generic variants add two more gains:

- **32-bit keys for IPv4.** The cached bits, mask and split bit shrink from 48 bytes to 12 bytes per node. More of the
  trie fits in cache, and each comparison is one instruction instead of two. This is optimization candidate 5 in the
  architecture document, and here it comes from choosing a type parameter.
- **No allocation per lookup.** This is optimization candidate 1.

## Other consequences of the design

1. **Stored items.** Today `TValue` must derive from `NetworkPrefixKey`, so every item carries its own prefix. The core
   instead stores the prefix and the value in separate node fields. That saves one object per prefix. A compatibility
   wrapper can still return `NetworkPrefixKeyValuePair` records by storing the record as the value.
2. **Routing nodes and roots need no prefix object.** They are just bits and a length.
3. **The known `Count` bugs disappear** in the rewrite. The prototype's count is exact.
4. **Concurrency needs a different writer rule.** Today stored items are references, which are written atomically. A
   struct prefix such as `IPNetwork`, or a struct value, is not. If a writer updates such a field in place while a
   reader copies it, the reader can see a torn value. So when a routing node becomes a value node, or the reverse, the
   writer must build a replacement node and publish it with one reference write. The prototype still mutates in place,
   as the current library does. The production version must not. This rule also removes today's
   `InvalidCastException` race.
5. **Diagnostics need a formatter.** `ToTextTree` and `ToDotTree` print routing nodes, which have no prefix object. The
   IP layer can format bits and length through `IPAddress`. The existing tests compare exact text, so the formatter must
   reproduce `NetworkPrefix.ToString`, including IPv6 and IPv4-embedded forms. That has not been verified yet.
6. **Some behavior becomes stricter.** Bug fixes such as `TryRemove` returning `false` for a routing node are
   observable changes. They belong in a major version.

## Decisions needed

- **Package layout.** The options are:
  - One package with the core, IP layer and both adapters. It keeps the NetworkPrefix dependency.
  - A dependency-free package with the core, the IP layer and the `IPNetwork` adapter, plus a second package with the
    `NetworkPrefix` adapter and the v2 compatibility API.

  The second option is the recommendation, because it fully meets the goal of not depending on NetworkPrefix.
- **Compatibility.** The options are to keep the v2 types `NetworkPrefixLookup`, `NetworkPrefixLookup<TValue>` and
  the `Concurrence*` classes as thin wrappers, or to make a clean break. Wrappers are the recommendation. They let the
  existing 19 tests run unchanged against the new core, which is the strongest correctness check available.
- **Result shape.** Lookups can return the prefix and value through `out` parameters, as in the prototype, or through
  a small readonly struct. Both avoid allocation.
- **Keys wider than 128 bits.** `IBinaryInteger<T>` covers `uint`, `ulong` and `UInt128` for free. A custom wider key
  type would have to implement that large interface. If wider keys matter, for example a VRF identifier plus an IPv6
  address, the core could instead require a small purpose-built interface with only the operations it uses: and, xor,
  shifts, leading-zero count and equality. The recommendation is `IBinaryInteger<T>` now, revisited only if a real use
  case appears.
- **Target frameworks.** Static abstract interface members need .NET 7 or later. `IPNetwork` needs .NET 8 or later.
  The projects already target .NET 10. Multi-targeting `net8.0` as well would keep .NET 8 and .NET 9 users.

## Work plan

1. **Core.** Bring `BitTrie` to feature parity: remove, longest match by prefix, all matches, clear and count. Use node
   replacement for in-place changes, and write unit tests directly against the core.
2. **IP layer.** Add the dual-stack lookup, `IPAddress` conversions, both adapters and a formatter. Port `ToTextTree`
   and `ToDotTree` onto the formatter.
3. **Compatibility layer.** Re-implement the v2 public types on top of the IP layer. Run the existing tests unchanged.
4. **Locking variants.** Add locking wrappers that cover every write method, which also fixes the gaps in today's
   `Concurrence*` classes.
5. **Benchmarks.** Add the generic variants, IPv6 lookups and prefix queries to `PerformanceTest`. Compare against the
   current library in one session.
6. **Packaging and documentation.** Apply the package decision, then update the README, the architecture document and
   CLAUDE.md.

The core should end up about the same size as today's `NetworkPrefixLookupBase`, which it replaces. The adapters are
a few lines each.

## Risks

- **Verbose generic signatures.** These are mitigated with concrete classes such as `IPNetworkLookup<TValue>`.
- **Formatting differences** in IPv6 diagnostics would break the text-tree tests until the formatter matches.
- **Concurrency bugs** if the node-replacement rule is not followed for struct prefixes and values. That deserves a
  dedicated stress test like `Add_IPv4_12`, using `IPNetwork` keys.
- **Measurement noise.** This container varies by about 10% between sessions. Every comparison should run in a single
  BenchmarkDotNet session.
