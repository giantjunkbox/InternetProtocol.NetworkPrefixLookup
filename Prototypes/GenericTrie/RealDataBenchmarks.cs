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
