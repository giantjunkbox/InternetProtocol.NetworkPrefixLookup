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
///     Read-write lookups with and without the check that retries a lookup that overlapped two writes, in one session.
///     Each case runs in its own process, which sets BITTRIE_NO_RETRY before it first touches a trie, and the two
///     variants of each benchmark run back to back. Set ROUTEVIEWS_DIR.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 20)]
public class ConsistencyBenchmarks
{
    // in this order, A B A B, so that drift during the session shows up as a difference between the two runs of a variant
    [Params("1-NoRetry", "2-Retry", "3-NoRetry", "4-Retry")]
    public string Variant = "";

    private IPNetworkLookup<int> _v4 = null!;
    private IPNetworkLookup<int> _v6 = null!;
    private IPNetworkLookup<int> _small = null!;
    private IPNetwork[] _churn = null!;
    private int[] _churnAsns = null!;
    private readonly PrefixEntry<IPNetwork, int>[] _matches = new PrefixEntry<IPNetwork, int>[33];

    [GlobalSetup]
    public void Setup()
    {
        Environment.SetEnvironmentVariable("BITTRIE_NO_RETRY", Variant.EndsWith("NoRetry") ? "1" : "0");
        _v4 = RouteViews.Build(RouteViews.IPv4);
        _v6 = RouteViews.Build(RouteViews.IPv6);
        var table = RouteViews.IPv4;
        _small = new IPNetworkLookup<int>(table.Networks.Where((_, i) => i % 1054 == 0).Select(n => KeyValuePair.Create(n, 0)));
        _churn = table.Networks.Where((_, i) => i % 1000 == 0).ToArray();
        _churnAsns = table.Asns.Where((_, i) => i % 1000 == 0).ToArray();
    }

    [Benchmark]
    public long RealIPv4_Integers()
    {
        var lookup = _v4;
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += lookup.TryGetMatchIPv4(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark]
    public long RealIPv4_Addresses()
    {
        var lookup = _v4;
        long sum = 0;
        foreach (var address in RouteViews.IPv4.Addresses)
            sum += lookup.TryGetMatchValue(address, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark]
    public long RealIPv6_Integers()
    {
        var lookup = _v6;
        long sum = 0;
        foreach (var key in RouteViews.IPv6.Keys)
            sum += lookup.TryGetMatchValueIPv6(key, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark]
    public long SmallIPv4_Integers()
    {
        var lookup = _small;
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += lookup.TryGetMatchValueIPv4(key, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark]
    public int RealIPv4_Matches()
    {
        var lookup = _v4;
        var matches = _matches;
        var sum = 0;
        var keys = RouteViews.IPv4.Addresses;
        for (var i = 0; i < keys.Length; i += 10)
            sum += lookup.GetMatches(keys[i], matches);
        return sum;
    }

    /// <summary>1,055 prefixes of the real IPv4 table removed and added back, one write at a time.</summary>
    [Benchmark]
    public void RealIPv4_SingleWrites()
    {
        for (var i = 0; i < _churn.Length; i++) _v4.Remove(_churn[i]);
        for (var i = 0; i < _churn.Length; i++) _v4.Add(_churn[i], _churnAsns[i]);
    }
}
