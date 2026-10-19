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
- **Arbitrary key widths are cheap if the key width is fixed when the code is compiled.** Replacing the
  `IBinaryInteger<T>` constraint with a small purpose-built key interface did not slow 32-bit keys. A 256-bit key
  using vector instructions was as fast as a 128-bit key. Only keys whose width is chosen at run time were expensive.
  See [Arbitrary key widths](#arbitrary-key-widths).
- **Decided:** .NET 10 only, no backward compatibility, a dependency-free core package, a separate package for the
  `NetworkPrefix` adapter, the `IBitKey<TSelf>` key interface with cached masks, and `out` parameters for lookup
  results. The remaining question is under [Open questions](#open-questions).

## Decisions

| Topic | Decision |
| --- | --- |
| Target framework | .NET 10 only. |
| Backward compatibility | Not required. The v2 public types are replaced, and the existing tests are ported to the new API. |
| Packaging | The core ships as a package with no dependency on `Bitvantage.InternetProtocol.NetworkPrefix`. |
| `NetworkPrefix` support | A separate package containing the `NetworkPrefix` adapter. |
| Key interface | `IBitKey<TSelf>`, with each node caching its mask and split bit. Widths are fixed at compile time; widths chosen at run time are not supported. See [Arbitrary key widths](#arbitrary-key-widths). |
| Lookup results | Whichever shape is fastest. All measured shapes tied, so lookups use `out` parameters, which keep the node layout private. See [Lookup result shape](#lookup-result-shape). |

## What the library uses from NetworkPrefix today

| Category | Members used | Where | What replaces it |
| --- | --- | --- | --- |
| Bits | `NetworkBits`, `NetworkMaskBits`, `Length`, `AddressLength` | Copied into every `Node` | An adapter supplies bits and length. The core computes masks from the length. |
| Address family | `Version`, `IPAddress.AddressFamily` | Choosing `RootV4` or `RootV6` | The IP layer picks one of two cores. The core has no notion of family. |
| Prefix arithmetic | `==`, `Contains`, `GetContainingNetwork` | Insert cases 1, 3 and 4 | Mask comparison and one leading-zero count. No objects are created. |
| Placeholder prefixes | `new NetworkPrefix(...)` for roots and routing nodes, `new NetworkPrefixKey(...)` | Root creation, insert case 4, remove | Routing nodes hold only bits. They need no prefix object at all. |
| Display | `ToString`, `Address`, `Length`, `IPVersion` | `ToTextTree`, `ToDotTree`, debugger display | A formatter for bits and length, supplied by the IP layer. |
| Public API | `NetworkPrefix` parameters, `NetworkPrefixKey` and `NetworkPrefixKeyValuePair<T>` records, the `IPVersion` enum | Every public method | Generic prefix and value parameters. |

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
public sealed class BitTrie<TKey, TPrefix, TValue>
    where TKey : struct, IBitKey<TKey>   // Bits32, Bits64, Bits128, Bits256, or your own fixed-width key
{
    public bool TryAdd(in TKey bits, int length, TPrefix prefix, TValue value);
    public bool TryRemove(in TKey bits, int length);
    public bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value);             // longest match for a full key
    public bool TryGetMatch(in TKey bits, int length, out TPrefix prefix, out TValue value); // longest match for a prefix
    public int GetMatches(in TKey key, ICollection<KeyValuePair<TPrefix, TValue>> results);
    public long Count { get; }
    public void Clear();
}
```

The first prototype constrained the key on `IBinaryInteger<T>`, which covers `uint`, `ulong` and `UInt128`. The
sketch above uses the purpose-built `IBitKey<TSelf>` interface instead, because it allows any width at no cost; see
[Arbitrary key widths](#arbitrary-key-widths).

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
P="-c Release --project Prototypes/GenericTrie --"
dotnet run $P verify                                    # abstraction: correctness and memory
dotnet run $P --filter 'Comparison.*'                   # abstraction benchmarks, about 7.5 minutes
dotnet run $P verify-width                              # key widths: correctness and memory
dotnet run $P --filter 'WidthComparison.*'              # key widths with computed masks, about 10 minutes
dotnet run $P --filter 'CachedWidthComparison.*'        # key widths with cached masks, about 9 minutes
dotnet run $P --filter 'Width192Comparison.*'           # 192-bit keys against 128-bit and 256-bit, about 6 minutes
dotnet run $P verify-shapes                             # result shapes: correctness
dotnet run $P --filter 'ResultShapeComparison.*'        # result shapes on the large and small tables, about 6 minutes
dotnet run $P --filter 'LargeValueShapeComparison.*'    # result shapes with string and 64-byte values, about 5 minutes
dotnet run $P isolate cached32                          # one variant in its own process, quick timing
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

## Arbitrary key widths

### What it takes

1. **Replace the `IBinaryInteger<T>` constraint with a small key interface.** The prototype's `IBitKey<TSelf>` in
   [`WideKeys.cs`](../Prototypes/GenericTrie/WideKeys.cs) has only `static abstract` members:

   | Member | Used for |
   | --- | --- |
   | `Truncate(key, length)` | Clearing host bits on insert and building routing nodes |
   | `CommonPrefixLength(a, b, max)` | The routing node for two sibling prefixes |
   | `MaskFor(length)` and `SplitFor(length)` | Values cached in each node when it is created |
   | `IsSet(key, split)` | Choosing a child slot |
   | `MatchesUnderMask(key, bits, mask)` | Testing whether a node contains a key |

2. **Ship a few key types.** `Bits32`, `Bits64` and `Bits128` are one-field wrappers around `uint`, `ulong` and
   `UInt128`. `Bits192` holds three 64-bit words inline, enough for an IPv6 address plus up to 64 bits of context.
   `Bits256` holds four words and compares them with one 256-bit vector instruction. Anyone can implement the interface
   for another fixed width.
3. **Have adapters produce key types instead of raw integers.** The IP layer wraps `uint` and `UInt128` in `Bits32` and
   `Bits128`.
4. **Keep the cached mask and split bit in each node.** That means three copies of the key per node, so node size grows
   with key width.

A key whose width is chosen at run time, held in an array, can implement the same interface. It is the expensive
option, as the measurements show.

### Measurements

The same IPv4 data was encoded in keys of each width, so every trie has the same shape and only the key representation
differs. The keys were converted before timing, so these numbers measure only the trie walk. They are not comparable
with the earlier tables, which include `IPAddress` conversion.

For 256-bit and variable-width keys there are two layouts. In the best case, the IPv4 bits sit in the first 64-bit
word. In the worst case, a fixed 192-bit context comes first, so every comparison has to cover all four words.

Two benchmark sessions were run, one with masks computed at each node and one with masks cached in each node.
Ratios are against the 32-bit `IBinaryInteger` reference in the same session. All variants returned identical
results for 1,000,000 lookups.

| Key type | Masks | Lookup time ratio | Retained bytes per prefix |
| --- | --- | --- | --- |
| 32-bit, `IBinaryInteger` reference | cached | 1.00 | 171 |
| 32-bit, `IBitKey` | cached | 1.09 ± 0.20 | 171 |
| 32-bit, `IBitKey` | computed | 1.38 ± 0.22 to 1.53 ± 0.25 | 157 |
| 128-bit | cached | 1.85 ± 0.27 | 243 |
| 128-bit | computed | 1.93 ± 0.29 | 186 |
| 256-bit vector, best case | cached | 1.60 ± 0.27 | 328 |
| 256-bit vector, worst case | cached | 1.82 ± 0.28 | 328 |
| 256-bit, best case | computed | 2.07 ± 0.33 | 214 |
| 256-bit, worst case | computed | 1.98 ± 0.31 | 214 |
| Variable width, array-backed, worst case | computed | 2.24 ± 0.33 | 271 |
| Variable width, array-backed, worst case | cached | 3.54 ± 0.54 | 499 |

A third session added the 192-bit key. Three 64-bit words do not fill a 256-bit vector, so `Bits192` uses
branch-free scalar operations on all three words. Its worst case puts a 128-bit context in front of the IPv4 bits.

| Key type | Masks | Lookup time ratio | Retained bytes per prefix |
| --- | --- | --- | --- |
| 32-bit, `IBinaryInteger` reference | cached | 1.00 | 171 |
| 128-bit | cached | 2.03 ± 0.21 | 243 |
| 192-bit scalar, best case | cached | 2.08 ± 0.34 | 285 |
| 192-bit scalar, worst case | cached | 2.15 ± 0.25 | 285 |
| 256-bit vector, best case | cached | 1.83 ± 0.23 | 328 |
| 256-bit vector, worst case | cached | 1.94 ± 0.26 | 328 |

Building the trie from 97,000 prefixes, with computed masks:

| 32-bit reference | 32-bit `IBitKey` | 128-bit | 256-bit | Variable width |
| --- | --- | --- | --- | --- |
| 57 ms | 64 ms | 86 ms | 87 to 91 ms | 109 ms |

### Findings

- **The interface itself costs nothing.** With masks cached in the node, 32-bit keys through `IBitKey` ran at
  1.09 ± 0.20 of the `IBinaryInteger` reference, which is within the noise.
- **Caching the mask matters more than expected.** Computing the mask at each node made 32-bit lookups 38 to 53%
  slower. The machine code showed both loops fully inlined, with about five extra instructions per node. The whole
  trie fits in this machine's 33 MiB L3 cache, so those instructions sit on the critical path.
- **Each key type pays only for itself.** The JIT compiles separate code for each key type. An IPv4 trie stays a fast
  32-bit trie no matter how wide the keys in other tries are.
- **128-bit keys cost about 1.85 times as much as 32-bit keys** per lookup in this test. Real IPv6 tables have a
  different shape, so this is a per-node cost, not a prediction for IPv6.
- **256-bit keys with vector instructions are about as fast as 128-bit keys.** One vector instruction covers all four
  words, while each `UInt128` operation takes two scalar instructions. A 128-bit key built on 128-bit vectors might
  therefore beat `UInt128` for IPv6. That has not been tested.
- **192-bit keys run at about the same speed as 128-bit and 256-bit keys, and use 13% less memory than 256-bit
  keys.** The 256-bit vector type was slightly faster in this session, but the difference is within the noise.
- **Run-time widths are the expensive option.** An array-backed key adds a pointer load per node, and caching masks
  adds two more arrays per node. That made lookups 2.2 to 3.5 times slower and used up to 2.9 times the memory.

### Recommendation

- Constrain the core on `IBitKey<TSelf>` rather than `IBinaryInteger<T>`. It is equally fast and allows any fixed
  width.
- Ship `Bits32`, `Bits64`, `Bits128` and `Bits192`. `Bits192` covers IPv6 plus up to 64 bits of context at the same
  speed as `Bits128`. Add `Bits256` only if more context bits are needed. Try a vector-based `Bits128` and keep
  whichever benchmarks faster.
- Cache the mask and split bit in each node.
- Do not support widths chosen at run time. Shorter keys already fit in a wider key type, because bits are
  left-aligned and each prefix carries its own length. If keys from different families share one trie, put a few tag
  bits in front so they cannot collide, the way IPv4-mapped IPv6 addresses do.

## Lookup result shape

The decision was to use whichever shape is fastest. Four shapes were measured, all sharing one trie walk and one node
layout in [`ResultShapes.cs`](../Prototypes/GenericTrie/ResultShapes.cs):

| Shape | Signature |
| --- | --- |
| A. Out parameters | `bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value)` |
| B. One out struct | `bool TryGetMatch(in TKey key, out PrefixMatch<TPrefix, TValue> match)` |
| C. Returned struct | `PrefixMatch<TPrefix, TValue> GetMatch(in TKey key)`, with a `Found` flag |
| D. Returned entry | `PrefixEntry<TPrefix, TValue>? FindEntry(in TKey key)`, the node itself through a read-only base type |

Prefixes were `IPNetwork` values, so results contain object references, and every benchmark read both the prefix and
the value of every result. The small table has 1,000 prefixes plus `0.0.0.0/0`. Its lookups are short enough that the
cost of returning the result would show if it mattered. Ratios are against shape A in the same session.

| Scenario | A | B | C | D |
| --- | --- | --- | --- | --- |
| Large table, string values | 1.00 | 0.92 ± 0.23 | 0.88 ± 0.20 | 1.06 ± 0.23 |
| Small table, string values, first session | 1.00 | 0.99 ± 0.06 | 1.01 ± 0.07 | 1.03 ± 0.06 |
| Small table, string values, second session | 1.00 | 0.97 ± 0.06 | 0.99 ± 0.08 | 0.97 ± 0.07 |
| Small table, 64-byte struct values | 1.00 | 1.02 ± 0.06 | 1.00 ± 0.06 | 1.04 ± 0.07 |

All four shapes are equally fast, within the noise, even when every result copies a 64-byte value. Copying a result
costs nothing measurable next to the walk.

**Decision: shape A, `out` parameters.** With speed tied, the deciding factor is future speed. Shape D would make the
internal node part of the public API, which would rule out later layout changes such as storing nodes in arrays instead
of separate objects. Shape A keeps the layout private and adds no result type. It is also the familiar `TryGetValue`
pattern.

## Other consequences of the design

1. **Stored items.** Today `TValue` must derive from `NetworkPrefixKey`, so every item carries its own prefix. The core
   instead stores the prefix and the value in separate node fields. That saves one object per prefix.
2. **Routing nodes and roots need no prefix object.** They are just bits and a length.
3. **The known `Count` bugs disappear** in the rewrite. The prototype's count is exact.
4. **Concurrency needs a different writer rule.** Today stored items are references, which are written atomically. A
   struct prefix such as `IPNetwork`, or a struct value, is not. If a writer updates such a field in place while a
   reader copies it, the reader can see a torn value. So when a routing node becomes a value node, or the reverse, the
   writer must build a replacement node and publish it with one reference write. The prototype still mutates in place,
   as the current library does. The production version must not. This rule also removes today's
   `InvalidCastException` race.
5. **Diagnostics need a formatter.** `ToTextTree` and `ToDotTree` print routing nodes, which have no prefix object. The
   IP layer can format bits and length through `IPAddress`. The ported tests can keep asserting the tree shape through
   this text, so the output format should be settled before the tests are ported.
6. **Some behavior becomes stricter.** Bug fixes such as `TryRemove` returning `false` for a routing node are
   observable changes. The redesign is a new major version without backward compatibility, so that is acceptable.

## Open questions

- **Package contents.** The recommendation is one dependency-free package containing the core, the IP layer and the
  `IPNetwork` adapter, because `IPNetwork` is part of .NET.

## Work plan

1. **Core.** Build `BitTrie` on `IBitKey<TSelf>` with cached masks, starting from `SnapshotTrie` in the prototype: the
   node pool, branch-free walk, multi-level index, snapshots, removal and compaction described in
   [Optimization-Log.md](Optimization-Log.md). Bring it to feature parity: remove,
   longest match by prefix, all matches, clear and count. Follow the concurrency rules in that log for array growth,
   removal and publishing the index. Write unit tests directly against the core, including at least one non-IP key
   width.
2. **IP layer.** Add the dual-stack lookup, `IPAddress` conversions, both adapters and a formatter. Port `ToTextTree`
   and `ToDotTree` onto the formatter.
3. **Port the tests.** Rewrite the existing 19 tests against the new API, keeping their tree-shape assertions through
   the formatter.
4. **Locking variants.** Add locking wrappers that cover every write method, which also fixes the gaps in today's
   `Concurrence*` classes.
5. **Frozen lookups.** Add a method that builds a read-only lookup from a loaded trie, with leaf-pushed tables and a
   value-only lookup, as described under Frozen tables in [Optimization-Log.md](Optimization-Log.md).
6. **Benchmarks.** Add IPv6 lookups and prefix queries to `PerformanceTest`. Compare against the current library in one
   session.
7. **Packaging and documentation.** Set up the dependency-free core package, then update the README, the architecture
   document and CLAUDE.md.

The core should end up about the same size as today's `NetworkPrefixLookupBase`, which it replaces. The adapters are
a few lines each.

## Risks

- **Verbose generic signatures.** These are mitigated with concrete classes such as `IPNetworkLookup<TValue>`.
- **Ported test expectations.** If the new formatter writes prefixes differently, the expected trees in the ported tests
  must be regenerated. Each regenerated string should differ only in formatting, never in shape.
- **Concurrency bugs** if the node-replacement rule is not followed for struct prefixes and values. That deserves a
  dedicated stress test like `Add_IPv4_12`, using `IPNetwork` keys.
- **Measurement noise.** This container varies by about 10% between sessions. Every comparison should run in a single
  BenchmarkDotNet session.
