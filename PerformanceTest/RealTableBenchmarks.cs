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

namespace PerformanceTest;

/// <summary>
///     One million lookups on the real RouteViews tables, read-write against both frozen layouts, from precomputed keys.
///     Set ROUTEVIEWS_DIR; see <see cref="RouteViews" />.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class RealTableBenchmarks
{
    private static readonly Lazy<(IPNetworkLookup<int> ReadWrite, FrozenIPNetworkLookup<int> LeafPushed, FrozenIPNetworkLookup<int> Compressed)> V4 = new(() => Forms(RouteViews.IPv4));
    private static readonly Lazy<(IPNetworkLookup<int> ReadWrite, FrozenIPNetworkLookup<int> LeafPushed, FrozenIPNetworkLookup<int> Compressed)> V6 = new(() => Forms(RouteViews.IPv6));

    private static (IPNetworkLookup<int>, FrozenIPNetworkLookup<int>, FrozenIPNetworkLookup<int>) Forms(RouteViews.Table table)
    {
        var lookup = RouteViews.Build(table);
        var compressed = new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed };
        return (lookup, lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.LeafPushed }), lookup.Freeze(compressed, compressed));
    }

    [GlobalSetup]
    public void Setup()
    {
        _ = V4.Value;
        _ = V6.Value;
    }

    // the concrete types, so that the calls are not made through the interface
    [Benchmark(Baseline = true), BenchmarkCategory("IPv4")]
    public long IPv4_ReadWrite()
    {
        var lookup = V4.Value.ReadWrite;
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += lookup.TryGetMatchIPv4(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark, BenchmarkCategory("IPv4")]
    public long IPv4_LeafPushed()
    {
        var lookup = V4.Value.LeafPushed;
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += lookup.TryGetMatchIPv4(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark, BenchmarkCategory("IPv4")]
    public long IPv4_Compressed()
    {
        var lookup = V4.Value.Compressed;
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += lookup.TryGetMatchIPv4(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark, BenchmarkCategory("IPv4")]
    public long IPv4_LeafPushed_ValueOnly()
    {
        var lookup = V4.Value.LeafPushed;
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += lookup.TryGetMatchValueIPv4(key, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark, BenchmarkCategory("IPv4")]
    public long IPv4_ReadWrite_IPAddress()
    {
        var lookup = V4.Value.ReadWrite;
        long sum = 0;
        foreach (var address in RouteViews.IPv4.Addresses)
            sum += lookup.TryGetMatch(address, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("IPv6")]
    public long IPv6_ReadWrite()
    {
        var lookup = V6.Value.ReadWrite;
        long sum = 0;
        foreach (var key in RouteViews.IPv6.Keys)
            sum += lookup.TryGetMatchIPv6(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark, BenchmarkCategory("IPv6")]
    public long IPv6_LeafPushed()
    {
        var lookup = V6.Value.LeafPushed;
        long sum = 0;
        foreach (var key in RouteViews.IPv6.Keys)
            sum += lookup.TryGetMatchIPv6(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark, BenchmarkCategory("IPv6")]
    public long IPv6_Compressed()
    {
        var lookup = V6.Value.Compressed;
        long sum = 0;
        foreach (var key in RouteViews.IPv6.Keys)
            sum += lookup.TryGetMatchIPv6(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark, BenchmarkCategory("IPv6")]
    public long IPv6_LeafPushed_ValueOnly()
    {
        var lookup = V6.Value.LeafPushed;
        long sum = 0;
        foreach (var key in RouteViews.IPv6.Keys)
            sum += lookup.TryGetMatchValueIPv6(key, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark, BenchmarkCategory("IPv6")]
    public long IPv6_ReadWrite_IPAddress()
    {
        var lookup = V6.Value.ReadWrite;
        long sum = 0;
        foreach (var address in RouteViews.IPv6.Addresses)
            sum += lookup.TryGetMatch(address, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }
}

/// <summary>
///     Building the real tables: one write at a time, as one batch, and freezing; and enumerating them, which is the part
///     of freezing during which writers wait.
/// </summary>
[MemoryDiagnoser]
public class RealBuildBenchmarks
{
    private static readonly Lazy<IPNetworkLookup<int>> V4 = new(() => RouteViews.Build(RouteViews.IPv4));
    private static readonly Lazy<IPNetworkLookup<int>> V6 = new(() => RouteViews.Build(RouteViews.IPv6));
    private static readonly Lazy<KeyValuePair<IPNetwork, int>[]> V4Items = new(() => Items(RouteViews.IPv4, shuffle: false));
    private static readonly Lazy<KeyValuePair<IPNetwork, int>[]> V4Shuffled = new(() => Items(RouteViews.IPv4, shuffle: true));
    private static readonly Lazy<KeyValuePair<IPNetwork, int>[]> V6Items = new(() => Items(RouteViews.IPv6, shuffle: false));
    private static readonly Lazy<KeyValuePair<IPNetwork, int>[]> V6Shuffled = new(() => Items(RouteViews.IPv6, shuffle: true));

    [GlobalSetup]
    public void Setup()
    {
        _ = V4.Value;
        _ = V6.Value;
        _ = V4Items.Value;
        _ = V4Shuffled.Value;
        _ = V6Items.Value;
        _ = V6Shuffled.Value;
    }

    // the table in file order, which is address order, or shuffled; the first of each network, as the batch keeps it
    private static KeyValuePair<IPNetwork, int>[] Items(RouteViews.Table table, bool shuffle)
    {
        var items = table.Networks.Select((n, i) => KeyValuePair.Create(n, table.Asns[i])).DistinctBy(p => p.Key).ToArray();
        if (shuffle) new Random(5).Shuffle(items);
        return items;
    }

    private static IPNetworkLookup<int> Batch(KeyValuePair<IPNetwork, int>[] items)
    {
        var lookup = new IPNetworkLookup<int>();
        using var batch = lookup.BeginBatch();
        foreach (var (network, asn) in items)
            lookup.TryAdd(network, asn);
        batch.Commit();
        return lookup;
    }

    [Benchmark, IterationCount(10)]
    public object IPv4_OneAtATime()
    {
        var table = RouteViews.IPv4;
        var lookup = new IPNetworkLookup<int>();
        for (var i = 0; i < table.Networks.Length; i++)
            lookup.TryAdd(table.Networks[i], table.Asns[i]);
        return lookup;
    }

    [Benchmark(Baseline = true), IterationCount(10)]
    public object IPv4_Batch() => RouteViews.Build(RouteViews.IPv4);

    [Benchmark, IterationCount(10)]
    public object IPv4_BatchShuffled() => Batch(V4Shuffled.Value);

    [Benchmark, IterationCount(10)]
    public object IPv4_FromSequence() => new IPNetworkLookup<int>(V4Items.Value);

    [Benchmark, IterationCount(10)]
    public object IPv4_FromShuffledSequence() => new IPNetworkLookup<int>(V4Shuffled.Value);

    [Benchmark, IterationCount(10)]
    public object IPv4_FreezeLeafPushed() => V4.Value.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.LeafPushed });

    [Benchmark, IterationCount(10)]
    public object IPv4_FreezeCompressed() => V4.Value.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });

    [Benchmark, IterationCount(10)]
    public object IPv4_Enumerate() => Enumerate(V4.Value);

    [Benchmark, IterationCount(10)]
    public object IPv6_Batch() => RouteViews.Build(RouteViews.IPv6);

    [Benchmark, IterationCount(10)]
    public object IPv6_BatchShuffled() => Batch(V6Shuffled.Value);

    [Benchmark, IterationCount(10)]
    public object IPv6_FromSequence() => new IPNetworkLookup<int>(V6Items.Value);

    [Benchmark, IterationCount(10)]
    public object IPv6_FromShuffledSequence() => new IPNetworkLookup<int>(V6Shuffled.Value);

    [Benchmark, IterationCount(10)]
    public object IPv6_FreezeLeafPushed() => V6.Value.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.LeafPushed });

    [Benchmark, IterationCount(10)]
    public object IPv6_FreezeCompressed() => V6.Value.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });

    [Benchmark, IterationCount(10)]
    public object IPv6_Enumerate() => Enumerate(V6.Value);

    private static long Enumerate(IPNetworkLookup<int> lookup)
    {
        long sum = 0;
        foreach (var entry in lookup)
            sum += entry.Value;
        return sum;
    }
}
