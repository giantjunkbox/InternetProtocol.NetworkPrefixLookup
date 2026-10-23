# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

`Bitvantage.InternetProtocol.NetworkPrefixLookup` is a .NET library, published on NuGet, for fast longest-prefix-match
lookups of IPv4 and IPv6 network prefixes and of any other fixed-width bit-string key. Version 3, on this branch, is a
redesign: a generic core that works on bits only, an IP layer on top, and adapter packages for other libraries'
network types.

Read `Documentation/Architecture.md` before changing anything under `Bitvantage.InternetProtocol.NetworkPrefixLookup/`.
It covers the node layout, the canonical-shape and copy-on-write rules, the frozen tables, the IP layer,
measured numbers and limitations. `Documentation/Architecture-v2.md` describes the version 2 binary trie that version 3
replaced.

Decisions made by the user:

- Target .NET 10 only.
- Backward compatibility with the v2 public API is not required.
- The core ships as a package with no dependency on `Bitvantage.InternetProtocol.NetworkPrefix`.
- The `NetworkPrefix` adapter ships as a separate package.
- The core is constrained on a purpose-built key interface, `IBitKey<TSelf>`. Key widths are fixed at compile time;
  widths chosen at run time are not supported.
- No custom key types: `IBitKey<TSelf>`'s members are internal, so only `Bits32`, `Bits128` and the test project's keys,
  through `InternalsVisibleTo`, implement it. The tries index with `GetBits` without bounds checks.
- Context such as a VRF is handled with one lookup instance per context, not with wider keys. Only `Bits32` and
  `Bits128` ship, and key widths are limited to 128 bits.
- Lookup results use whichever shape is fastest. All measured shapes tied, so lookups return the prefix and value
  through `out` parameters, which keep the node layout private.
- The read-write core follows the BART-style prototype (`Documentation/Bart-Comparison.md`): 8-bit multibit nodes,
  copy-on-write, lock-free readers. Its known costs, slower lookups on small tables that fit in cache and garbage under
  heavy churn, are accepted.
- One read-write design in which every write does bounded work, with no pauses for growth or compaction. There is no
  separate real-time mode, because two nearly identical code paths are not worth keeping. A separate form is justified
  only when it is clearly faster, as the frozen form is.
- Lookups see the table as it was at one moment: a lookup that overlaps two writes retries, as described under "What
  readers see" in `Documentation/Architecture.md`.
- Addresses are looked up as given by default. `IPLookupOptions.MapIPv4MappedAddresses` maps IPv4-mapped addresses to
  IPv4, strictly: a mapped address gets exactly the answer of the IPv4 address it holds. MaxMind lookups turn it on by
  default, and the MaxMind loader loads MaxMind's alias networks only on request.
- Public API compatibility is not a concern while version 3 is unreleased.
- The package names as they are, and version 3.0.0 for every package.
- No code generators or scripts in other languages: the repository stays C#.
- Ranges with arbitrary ends, such as `192.168.0.10-192.168.0.20`, are left to a separate library: there is no range
  lookup and no `IPAddressRange` package.
- A frozen table chooses its layout from the data unless the caller names one: leaf-pushed, or compressed when the
  leaf-pushed child tables would hold more than 256 entries per prefix and more than 16 MB in all.
- With `MapIPv4MappedAddresses` on, adding an IPv6 prefix inside `::ffff:0:0/96` throws, because no address lookup
  could find it.
- The queries about networks are `TryGetMatch`, `GetMatches`, `GetSubnets` and `Overlaps`, with no overlap test between
  two whole tables. With mapping on they follow the address lookups: a network inside `::ffff:0:0/96` is answered as
  the IPv4 network it holds, and a network containing that whole range also contains every IPv4 prefix. Writes and
  the exact lookups take a prefix as given.

The public interface is described in `Documentation/Interface.md`, whose last sections record where the shipped
interface differs from the original proposal. `Documentation/Adapter-Survey.md` explains which libraries get adapter packages.
`Documentation/Abstraction-Study.md`, `Documentation/Optimization-Log.md`, `Documentation/Bart-Comparison.md` and
`Documentation/Linux-Survey.md` record how the design was chosen and measured.

## Layout

- `Bitvantage.InternetProtocol.NetworkPrefixLookup/`: the core package.
  - `Keys/`: `IBitKey<TSelf>`, `Bits32` and `Bits128`.
  - `Core/`: `BitTrie` (the read-write trie), `FrozenBitTrie`, `IReadOnlyBitTrie`, `BitSet256` with the ART tables,
    `PrefixEntry`, `NoValue`.
  - `IP/`: the adapter interface, `IPAddressBits`, `IPPrefixLookup` and its frozen form, the internal `IPLookupReader`
    they share, and `IPNetworkLookup` for `System.Net.IPNetwork`.
- `Bitvantage.InternetProtocol.NetworkPrefixLookup.NetworkPrefix/`, `.IPNetwork2/`, `.MaxMind/`: the adapter packages,
  each an adapter struct and a read-write and frozen class pair. The MaxMind package also has `MaxMindDatabase.Load`.
- The read-only members of the read-write and frozen IP lookups are written once, in the internal struct
  `IPLookupReader`, and each public class forwards to it in one line. A marker type argument, `ReadWriteForm` or
  `FrozenForm`, lets the JIT call each form's sealed trie type directly; see "One copy of the read-only members" in
  `Documentation/Architecture.md`. Make every lookup's call into a trie through those typed calls, not through
  `IReadOnlyBitTrie`, which would not inline. Keep the reader, the markers and any new shared code internal.
- `Test/` contains NUnit tests in `Core/`, `IP/` and `Adapters/`, and two MaxMind test databases in `Data/MaxMind/`
  (Apache-2.0 or MIT, notice included). `PerformanceTest/` contains BenchmarkDotNet benchmarks and a memory report.
- `Prototypes/GenericTrie/` holds the prototypes the design was measured with. It is not in the solution and references
  the v2 library from NuGet, so it builds independently of the library. `BartTrie.cs` is the prototype of `BitTrie`;
  `PooledTrie` in `FastTrie.cs` holds the prototype frozen tables; `SnapshotTrie.cs` is the earlier binary-trie core.
  `Prototypes/GenericTrie.Tests/` holds 66 test cases for the prototypes.
- `PerformanceTest.V2/` runs `ComparisonBenchmarks` against the original library, before the redesign, at commit
  `b512201`. It is not in the solution; `get-original.sh` extracts the original source into its ignored `Original/`
  folder, and it targets .NET 7, which the original shipped on, as well as .NET 10.

## Build and test

All projects target .NET 10. Cloud containers do not ship the SDK; install it into the home directory:

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

```sh
dotnet build -c Release                                   # whole solution
dotnet test Test -c Release                               # all unit tests, about 2 minutes
dotnet test Test -c Release --filter "FullyQualifiedName~Test.IP"   # a subset
dotnet run -c Release --project PerformanceTest           # all benchmarks
dotnet run -c Release --project PerformanceTest -- --filter '*RandomTable*'   # a subset
dotnet run -c Release --project PerformanceTest -- --memory                   # memory per form, bytes per lookup
```

`PerformanceTest` passes its arguments to BenchmarkDotNet, so `--filter` and `--artifacts` work.

To compare with the original library, run the same benchmarks in its own process, from its folder so that
BenchmarkDotNet finds the right project:

```sh
PerformanceTest.V2/get-original.sh
(cd PerformanceTest.V2 && dotnet run -c Release -f net7.0)          # or -f net10.0; --memory for memory held
dotnet run -c Release --project PerformanceTest -- --filter '*ComparisonBenchmarks*'
dotnet run -c Release --project PerformanceTest -- --memory-comparison
```

The real-table benchmarks and the prototype's real-data tests use the CAIDA RouteViews tables. Download them once and
point `ROUTEVIEWS_DIR` at them; without it those benchmarks are left out and the prototype's six real-table tests are
skipped:

```sh
Prototypes/GenericTrie/download-routeviews.sh /tmp/routeviews
ROUTEVIEWS_DIR=/tmp/routeviews dotnet run -c Release --project PerformanceTest
ROUTEVIEWS_DIR=/tmp/routeviews dotnet test Prototypes/GenericTrie.Tests -c Release   # about 3 minutes
```

**Test baseline:** all 61 tests pass. The one test that asserts exact `ToTextTree` output builds its expected
text with `Environment.NewLine`, so it passes with either line ending. Keep `.gitattributes` at `text=auto`.

The whole solution builds with no warnings. Do not add any.

## Conventions

- Every `.cs` file starts with the AGPL license header in `Bitvantage.InternetProtocol.NetworkPrefixLookup.sln.licenseheader`.
- C# source files are UTF-8 with a byte order mark and use 4-space indentation. Project files use tabs, except
  `Test/Test.csproj`, which uses spaces.
- The library projects set `GenerateDocumentationFile`, so every public member needs an XML doc comment. A build
  must produce no CS1591, CS1570 or CS1574 warnings.
- Comment tags in the code:
  - `PERF:` marks a known cost on a hot path.
  - `KNOWN ISSUE:` marks a confirmed bug that was intentionally left unchanged.
  - `TODO:` marks the author's open ideas.
- Terminology for the trie: a node's *prefixes* end inside its 8-bit stride and are stored as ART base indexes;
  *fringes* end exactly at the end of the stride; *leaves* are lone longer prefixes stored whole; *child nodes* exist
  where two or more longer prefixes share an octet. The core stores a *prefix object* and a *value* with each prefix.

## Rules for changing the trie

- Keep the trie canonical: its shape must depend only on the set of stored prefixes, not on insertion order. A child
  node exists at an octet exactly when two or more prefixes lie below it; one prefix below an octet is a leaf. The
  tests compare `ToTextTree` output across insertion orders and removals, and one test asserts the exact format.
- Readers take no locks. A writer must never change a node that readers can reach, except to store one child reference
  with `Volatile.Write`. Copy the node, finish the copy, then link it in. A node is private only when its `Stamp` is
  the current one, and an array may change in place only when its `Shared` bit is clear.
- During a batch nothing readers can see may change: copy every node on the path to the private root, and adjust
  counts only on private nodes. A batch commit and `Clear` publish the root and the shortcut blocks together.
- Shortcut blocks are created and dropped with the depth-1 nodes they serve; no write may build more than one.
- Every write must do bounded work: no step may grow with the table's size.
- The constructors from a sequence build the trie in one pass (`BitTrie.Load` and `Build`). Any change to the node
  layout or the canonical shape must change that build too; the tests compare its result with a trie built a prefix at
  a time.
- Backward compatibility with the v2 API is not required, but keep public API changes deliberate and documented in
  `Documentation/Interface.md`.
- Target .NET 10 only. Do not add other target frameworks or change package versions unless asked.

## Performance work

- Measure before and after on the same machine. Use BenchmarkDotNet for timings and
  `GC.GetAllocatedBytesForCurrentThread` for allocations.
- The current baseline is in the Performance section of `Documentation/Architecture.md`. Lookups allocate nothing.
- `Documentation/Optimization-Log.md` and the Remedies tried section of `Documentation/Bart-Comparison.md` list what
  made the prototypes faster and what did not help. Check them before trying an idea again.
- Random benchmark data fits in cache and flatters some designs; confirm results on the real RouteViews tables.
- Cloud containers drift by about 10% between benchmark sessions. Compare variants in one BenchmarkDotNet session, as
  separate jobs, rather than across separate runs. Version 2 cannot share a process with version 3, because the two
  assemblies have the same name, so comparisons with it span two runs.
- On .NET 10 write-heavy benchmarks are dominated by garbage collection. Watch the Gen0 and Gen1 columns, and prefer
  allocation reductions over GC settings, which belong to the application.
- Update the measured baseline and the limitations in `Documentation/Architecture.md` when a change lands.
