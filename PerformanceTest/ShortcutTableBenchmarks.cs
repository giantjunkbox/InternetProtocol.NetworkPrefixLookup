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
using BenchmarkDotNet.Jobs;
using Bitvantage.InternetProtocol;

namespace PerformanceTest;

/// <summary>
///     The flat 65,536-slot shortcut table against one block per first octet, in one session as two jobs that differ only
///     in BITTRIE_FLAT_TABLE. Set ROUTEVIEWS_DIR.
/// </summary>
[Config(typeof(ShortcutTableConfig))]
[MemoryDiagnoser]
public class ShortcutTableBenchmarks
{
    private IPNetworkLookup<int> _v4 = null!;
    private IPNetworkLookup<int> _v6 = null!;
    private NetworkPrefixLookup<string> _random = null!;
    private uint[] _randomKeys = null!;
    private IPNetwork[] _churn = null!;
    private int[] _churnAsns = null!;

    [GlobalSetup]
    public void Setup()
    {
        _v4 = RouteViews.Build(RouteViews.IPv4);
        _v6 = RouteViews.Build(RouteViews.IPv6);
        _random = new NetworkPrefixLookup<string>(RandomTableBenchmarks.CreateNetworks().Select(n => KeyValuePair.Create(n, string.Empty)));
        _randomKeys = RandomTableBenchmarks.CreateAddresses().Select(IPAddressBits.ToIPv4Bits).ToArray();
        // every 1,000th prefix of the real IPv4 table, removed and added back by the write benchmarks
        var table = RouteViews.IPv4;
        _churn = table.Networks.Where((_, i) => i % 1000 == 0).ToArray();
        _churnAsns = table.Asns.Where((_, i) => i % 1000 == 0).ToArray();
    }

    [Benchmark]
    public long RealIPv4_Lookups()
    {
        var lookup = _v4;
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += lookup.TryGetMatchValueIPv4(key, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark]
    public long RealIPv6_Lookups()
    {
        var lookup = _v6;
        long sum = 0;
        foreach (var key in RouteViews.IPv6.Keys)
            sum += lookup.TryGetMatchValueIPv6(key, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark]
    public int RandomTable_Lookups()
    {
        var lookup = _random;
        var found = 0;
        foreach (var key in _randomKeys)
            if (lookup.TryGetMatchValueIPv4(key, out _)) found++;
        return found;
    }

    /// <summary>One batch on the real IPv4 table that removes 10 prefixes and adds them back.</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public void RealIPv4_SmallBatches()
    {
        for (var round = 0; round < 100; round++)
        {
            using var batch = _v4.BeginBatch();
            for (var i = 0; i < 10; i++) _v4.Remove(_churn[i]);
            for (var i = 0; i < 10; i++) _v4.Add(_churn[i], _churnAsns[i]);
            batch.Commit();
        }
    }

    /// <summary>1,055 prefixes of the real IPv4 table removed and added back, one write at a time.</summary>
    [Benchmark]
    public void RealIPv4_SingleWrites()
    {
        for (var i = 0; i < _churn.Length; i++) _v4.Remove(_churn[i]);
        for (var i = 0; i < _churn.Length; i++) _v4.Add(_churn[i], _churnAsns[i]);
    }
}

public class ShortcutTableConfig : ManualConfig
{
    public ShortcutTableConfig()
    {
        AddJob(Job.Default.WithEnvironmentVariables(new EnvironmentVariable("BITTRIE_FLAT_TABLE", "1")).WithId("Flat").AsBaseline());
        AddJob(Job.Default.WithEnvironmentVariables(new EnvironmentVariable("BITTRIE_FLAT_TABLE", "0")).WithId("Blocks"));
    }
}

/// <summary>Lookups in small frozen tables: a full 16-bit root against the root sized from the number of prefixes.</summary>
public class FrozenRootBenchmarks
{
    [Params(256, 1000, 4000)]
    public int Prefixes;

    private FrozenNetworkPrefixLookup<string> _fullRoot = null!;
    private FrozenNetworkPrefixLookup<string> _sizedRoot = null!;
    private uint[] _keys = null!;

    [GlobalSetup]
    public void Setup()
    {
        var lookup = new NetworkPrefixLookup<string>(RandomTableBenchmarks.CreateNetworks().Take(Prefixes).Select(n => KeyValuePair.Create(n, string.Empty)));
        _fullRoot = lookup.Freeze(new FrozenBitTrieOptions { RootBits = 16 });
        _sizedRoot = lookup.Freeze();
        _keys = RandomTableBenchmarks.CreateAddresses().Select(IPAddressBits.ToIPv4Bits).ToArray();
        Console.WriteLine($"// {Prefixes} prefixes: full root {_fullRoot.Bytes:N0} bytes, sized root {_sizedRoot.Bytes:N0} bytes");
    }

    [Benchmark(Baseline = true)]
    public int FullRoot()
    {
        var found = 0;
        foreach (var key in _keys)
            if (_fullRoot.TryGetMatchValueIPv4(key, out _)) found++;
        return found;
    }

    [Benchmark]
    public int SizedRoot()
    {
        var found = 0;
        foreach (var key in _keys)
            if (_sizedRoot.TryGetMatchValueIPv4(key, out _)) found++;
        return found;
    }
}
