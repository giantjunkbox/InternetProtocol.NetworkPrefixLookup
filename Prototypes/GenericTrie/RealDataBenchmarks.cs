/*
   Bitvantage.InternetProtocol.NetworkPrefixLookup
   Copyright (C) 2024 Michael Crino

   This program is free software: you can redistribute it and/or modify
   it under the terms of the GNU Affero General Public License as published by
   the Free Software Foundation, either version 3 of the License, or
   (at your option) any later version.

   This program is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
   GNU Affero General Public License for more details.

   You should have received a copy of the GNU Affero General Public License
   along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Bitvantage.InternetProtocol;

namespace GenericTrie;

/// <summary>
///     One million lookups from IPAddress objects against the real RouteViews IPv4 table, 1,054,037 prefixes. Requires
///     ROUTEVIEWS_DIR.
/// </summary>
[MemoryDiagnoser]
public class RealIPv4Lookup
{
    private static readonly RealData.Table<Bits32> Table = RealData.V4;
    private static readonly NetworkPrefixLookup<int> Current = RealDataChecks.BuildCurrent(Table);
    private static readonly CachedKeyTrie<Bits32, IPNetwork, int> Cached = RealDataChecks.BuildCached(Table);
    private static readonly SnapshotTrie<Bits32, IPNetwork, int> Snapshot8 = RealDataChecks.BulkSnapshot(Table, 32, 8);
    private static readonly SnapshotTrie<Bits32, IPNetwork, int> Snapshot32 = RealDataChecks.BulkSnapshot(Table, 32, 32);

    [Benchmark(Baseline = true), IterationCount(20)]
    public long CurrentLibrary() { long sum = 0; foreach (var a in Table.Addresses) sum += Current.TryGetMatch(a, out var r) ? r.Prefix.Length + r.Value : -1; return sum; }

    [Benchmark, IterationCount(20)]
    public long DecidedBaseline_CachedKeyTrie() { long sum = 0; foreach (var a in Table.Addresses) { var k = new Bits32(Optimization.FromAddressProperty(a)); sum += Cached.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20)]
    public long Optimized_MultiLevel_Threshold8() { long sum = 0; foreach (var a in Table.Addresses) { var k = new Bits32(Optimization.FromAddressProperty(a)); sum += Snapshot8.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20)]
    public long Optimized_MultiLevel_Threshold32() { long sum = 0; foreach (var a in Table.Addresses) { var k = new Bits32(Optimization.FromAddressProperty(a)); sum += Snapshot32.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }
}

/// <summary>One million lookups from IPAddress objects against the real RouteViews IPv6 table, 239,603 prefixes.</summary>
[MemoryDiagnoser]
public class RealIPv6Lookup
{
    private static readonly RealData.Table<Bits128> Table = RealData.V6;
    private static readonly NetworkPrefixLookup<int> Current = RealDataChecks.BuildCurrent(Table);
    private static readonly CachedKeyTrie<Bits128, IPNetwork, int> Cached = RealDataChecks.BuildCached(Table);
    private static readonly SnapshotTrie<Bits128, IPNetwork, int> Snapshot8 = RealDataChecks.BulkSnapshot(Table, 128, 8);
    private static readonly SnapshotTrie<Bits128, IPNetwork, int> Snapshot32 = RealDataChecks.BulkSnapshot(Table, 128, 32);

    [Benchmark(Baseline = true), IterationCount(20)]
    public long CurrentLibrary() { long sum = 0; foreach (var a in Table.Addresses) sum += Current.TryGetMatch(a, out var r) ? r.Prefix.Length + r.Value : -1; return sum; }

    [Benchmark, IterationCount(20)]
    public long DecidedBaseline_CachedKeyTrie() { long sum = 0; foreach (var a in Table.Addresses) { var k = new Bits128(IPBits.V6(a)); sum += Cached.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20)]
    public long Optimized_MultiLevel_Threshold8() { long sum = 0; foreach (var a in Table.Addresses) { var k = new Bits128(IPBits.V6(a)); sum += Snapshot8.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20)]
    public long Optimized_MultiLevel_Threshold32() { long sum = 0; foreach (var a in Table.Addresses) { var k = new Bits128(IPBits.V6(a)); sum += Snapshot32.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }
}

/// <summary>
///     Frozen-table options against the read-write design, one million lookups from precomputed keys, so address
///     conversion is left out. The IndexOnly rows stop at the value index and skip reading the prefix and value, which
///     shows how the time divides between finding the answer and fetching it. Each table is built only in the process
///     that measures it.
/// </summary>
[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class RealFrozenLookup
{
    private static readonly Lazy<SnapshotTrie<Bits32, IPNetwork, int>> V4Snapshot = new(() => RealDataChecks.BulkSnapshot(RealData.V4, 32, 8));
    private static readonly Lazy<PooledTrie<Bits32, IPNetwork, int>> V4MultiLevel = new(() => RealDataChecks.BuildMultiLevel(RealData.V4, 16, 8, 8, 32));
    private static readonly Lazy<PooledTrie<Bits32, IPNetwork, int>> V4LeafPushed16 = new(() => RealDataChecks.BuildLeafPushed(RealData.V4, 16, 8, 32));
    private static readonly Lazy<PooledTrie<Bits32, IPNetwork, int>> V4LeafPushed24 = new(() => RealDataChecks.BuildLeafPushed(RealData.V4, 24, 8, 32));
    private static readonly Lazy<PooledTrie<Bits32, IPNetwork, int>> V4Compressed18 = new(() => RealDataChecks.BuildCompressed(RealData.V4, 18, 32));
    private static readonly Lazy<SnapshotTrie<Bits128, IPNetwork, int>> V6Snapshot = new(() => RealDataChecks.BulkSnapshot(RealData.V6, 128, 8));
    private static readonly Lazy<PooledTrie<Bits128, IPNetwork, int>> V6MultiLevel = new(() => RealDataChecks.BuildMultiLevel(RealData.V6, 16, 8, 8, 128));
    private static readonly Lazy<PooledTrie<Bits128, IPNetwork, int>> V6LeafPushed16 = new(() => RealDataChecks.BuildLeafPushed(RealData.V6, 16, 8, 128));
    private static readonly Lazy<PooledTrie<Bits128, IPNetwork, int>> V6Compressed18 = new(() => RealDataChecks.BuildCompressed(RealData.V6, 18, 128));
    private static readonly Lazy<PooledTrie<Bits128, IPNetwork, int>> V6Compressed20 = new(() => RealDataChecks.BuildCompressed(RealData.V6, 20, 128));

    private static long Lookups<TKey>(TKey[] keys, SnapshotTrie<TKey, IPNetwork, int> trie) where TKey : struct, IBitKey<TKey>
    { long sum = 0; for (var i = 0; i < keys.Length; i++) sum += trie.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v : -1; return sum; }

    private static long Lookups<TKey>(TKey[] keys, PooledTrie<TKey, IPNetwork, int> trie) where TKey : struct, IBitKey<TKey>
    { long sum = 0; for (var i = 0; i < keys.Length; i++) sum += trie.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v : -1; return sum; }

    private static long IndexOnly<TKey>(TKey[] keys, PooledTrie<TKey, IPNetwork, int> trie) where TKey : struct, IBitKey<TKey>
    { long sum = 0; for (var i = 0; i < keys.Length; i++) sum += trie.GetMatchIndex(in keys[i]); return sum; }

    [Benchmark(Baseline = true), IterationCount(20), BenchmarkCategory("IPv4")]
    public long IPv4_ReadWrite_MultiLevel() => Lookups(RealData.V4.Keys, V4Snapshot.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4")]
    public long IPv4_LeafPushed_16_8() => Lookups(RealData.V4.Keys, V4LeafPushed16.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4")]
    public long IPv4_LeafPushed_24_8() => Lookups(RealData.V4.Keys, V4LeafPushed24.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4")]
    public long IPv4_Compressed_18() => Lookups(RealData.V4.Keys, V4Compressed18.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4")]
    public long IPv4_MultiLevel_IndexOnly() => IndexOnly(RealData.V4.Keys, V4MultiLevel.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4")]
    public long IPv4_LeafPushed_16_8_IndexOnly() => IndexOnly(RealData.V4.Keys, V4LeafPushed16.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4")]
    public long IPv4_LeafPushed_24_8_IndexOnly() => IndexOnly(RealData.V4.Keys, V4LeafPushed24.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4")]
    public long IPv4_Compressed_18_IndexOnly() => IndexOnly(RealData.V4.Keys, V4Compressed18.Value);

    [Benchmark(Baseline = true), IterationCount(20), BenchmarkCategory("IPv6")]
    public long IPv6_ReadWrite_MultiLevel() => Lookups(RealData.V6.Keys, V6Snapshot.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6")]
    public long IPv6_LeafPushed_16_8() => Lookups(RealData.V6.Keys, V6LeafPushed16.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6")]
    public long IPv6_Compressed_18() => Lookups(RealData.V6.Keys, V6Compressed18.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6")]
    public long IPv6_Compressed_20() => Lookups(RealData.V6.Keys, V6Compressed20.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6")]
    public long IPv6_MultiLevel_IndexOnly() => IndexOnly(RealData.V6.Keys, V6MultiLevel.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6")]
    public long IPv6_LeafPushed_16_8_IndexOnly() => IndexOnly(RealData.V6.Keys, V6LeafPushed16.Value);

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6")]
    public long IPv6_Compressed_18_IndexOnly() => IndexOnly(RealData.V6.Keys, V6Compressed18.Value);
}

/// <summary>Building each real table from scratch.</summary>
[MemoryDiagnoser]
public class RealBuild
{
    private static readonly NetworkPrefix[] V4Prefixes = RealData.V4.Networks.Select(n => new NetworkPrefix(n.BaseAddress, n.PrefixLength)).ToArray();
    private static readonly NetworkPrefix[] V6Prefixes = RealData.V6.Networks.Select(n => new NetworkPrefix(n.BaseAddress, n.PrefixLength)).ToArray();

    private static object Current(NetworkPrefix[] prefixes, int[] asns) { var l = new NetworkPrefixLookup<int>(); for (var i = 0; i < prefixes.Length; i++) l.TryAdd(prefixes[i], asns[i]); return l; }

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv4")]
    public object IPv4_CurrentLibrary() => Current(V4Prefixes, RealData.V4.Asns);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv4")]
    public object IPv4_DecidedBaseline_CachedKeyTrie() => RealDataChecks.BuildCached(RealData.V4);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv4")]
    public object IPv4_Optimized_BulkLoad_Threshold8() => RealDataChecks.BulkSnapshot(RealData.V4, 32, 8);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv4")]
    public object IPv4_Optimized_BulkLoad_Threshold32() => RealDataChecks.BulkSnapshot(RealData.V4, 32, 32);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv6")]
    public object IPv6_CurrentLibrary() => Current(V6Prefixes, RealData.V6.Asns);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv6")]
    public object IPv6_DecidedBaseline_CachedKeyTrie() => RealDataChecks.BuildCached(RealData.V6);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv6")]
    public object IPv6_Optimized_BulkLoad_Threshold8() => RealDataChecks.BulkSnapshot(RealData.V6, 128, 8);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv6")]
    public object IPv6_Optimized_BulkLoad_Threshold32() => RealDataChecks.BulkSnapshot(RealData.V6, 128, 32);
}
