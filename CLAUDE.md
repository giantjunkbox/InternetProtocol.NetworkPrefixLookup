# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

`Bitvantage.InternetProtocol.NetworkPrefixLookup` is a .NET library, published on NuGet, for fast longest-prefix-match
lookups of IPv4 and IPv6 network prefixes. It stores prefixes in a path-compressed binary trie, one per address family.
The current goal is to prepare for performance optimization of the lookup path.

Read `Documentation/Architecture.md` before changing anything under `Bitvantage.InternetProtocol.NetworkPrefixLookup/`.
It covers the trie invariants, the lock-free reader rules, measured baseline numbers, optimization candidates, and
known bugs.

## Layout

- `Bitvantage.InternetProtocol.NetworkPrefixLookup/NetworkPrefixLookupBase.cs` holds nearly all logic: the trie nodes,
  lookup, insert, remove and the text and Graphviz renderers.
- `NetworkPrefixLookup.cs`, `NetworkPrefixKey.cs` and `ConcurrenceNetworkPrefixLookup.cs` are thin public wrappers.
- `Test/` contains NUnit tests. `PerformanceTest/` contains BenchmarkDotNet benchmarks.
- The `NetworkPrefix` type comes from the `Bitvantage.InternetProtocol.NetworkPrefix` NuGet package, version `2.0.*`.

## Build and test

All projects target .NET 7. Cloud containers do not ship the SDK; install it into the home directory:

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 7.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

```sh
dotnet build -c Release                                   # whole solution
dotnet test Test -c Release                               # all unit tests
dotnet test Test -c Release --filter "Name~Delete_IPv4"   # a subset
dotnet run -c Release --project PerformanceTest           # benchmarks; slow, runs every benchmark
```

`PerformanceTest` ignores command-line arguments, so BenchmarkDotNet filters do not work without changing `Main`.

**Test baseline on Linux:** 3 of 19 tests pass and 16 fail. Every failure comes from one known bug: `ToTextTree`
trims its trailing line break by removing two characters, which assumes Windows `\r\n`. All 19 pass on Windows, or on
Linux once that trim uses `Environment.NewLine.Length`. Do not edit the expected strings in the tests to work around
it.

The library build has 4 pre-existing nullable warnings (CS8618, CS8625). Do not add new warnings.

## Conventions

- Every `.cs` file starts with the AGPL license header in `Bitvantage.InternetProtocol.NetworkPrefixLookup.sln.licenseheader`.
- C# source files are UTF-8 with a byte order mark and use 4-space indentation. Project files use tabs.
- The library project sets `GenerateDocumentationFile`, so every public member needs an XML doc comment. A build
  must produce no CS1591, CS1570 or CS1574 warnings.
- Comment tags in the code:
  - `PERF:` marks a known cost on a hot path.
  - `KNOWN ISSUE:` marks a confirmed bug that was intentionally left unchanged.
  - `TODO:` marks the author's open ideas.
- Terminology: a *value node* holds an added item (`HasValue == true`); a *routing node* only joins two subtrees. The
  README calls routing nodes "container nodes".

## Rules for changing the trie

- Keep the trie canonical: its shape must depend only on the set of stored prefixes, not on insertion order. Most tests
  assert the exact `ToTextTree` output, so do not change that format.
- Readers take no locks. A writer must fully build any new node before linking it in with one reference write, and must
  never leave a node that readers can reach in a half-updated state.
- Public API changes are breaking changes for NuGet consumers. Raise them with the user first.
- Do not retarget the framework or bump package versions unless asked.

## Performance work

- Measure before and after on the same machine. Use BenchmarkDotNet for timings and
  `GC.GetAllocatedBytesForCurrentThread` for allocations.
- The baseline in `Documentation/Architecture.md` shows about 16 nodes visited per lookup with the benchmark data, and
  64 bytes allocated per `TryGetMatch(IPAddress)` call in `ToUInt128`.
- Search for `PERF:` to find the hot spots that are already identified.
- Update the measured baseline and the optimization list in `Documentation/Architecture.md` when a change lands.
