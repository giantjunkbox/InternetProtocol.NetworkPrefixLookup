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
///     The queries about networks on the real RouteViews tables, read-write against frozen: 100,000 networks per family,
///     each an announced prefix made up to 8 bits shorter or longer, so that some hold announced prefixes and some lie
///     inside them. Set ROUTEVIEWS_DIR; see <see cref="RouteViews" />.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class RealNetworkQueryBenchmarks
{
    private sealed record Data(IPNetworkLookup<int> ReadWrite, FrozenIPNetworkLookup<int> Frozen, IPNetwork[] Queries);

    private static readonly Lazy<Data> V4 = new(() => Create(RouteViews.IPv4, 32));
    private static readonly Lazy<Data> V6 = new(() => Create(RouteViews.IPv6, 128));

    private static Data Create(RouteViews.Table table, int width)
    {
        var lookup = RouteViews.Build(table);
        var random = new Random(7);
        var queries = new IPNetwork[100_000];
        for (var i = 0; i < queries.Length; i++)
        {
            var announced = table.Networks[random.Next(table.Networks.Length)];
            var length = Math.Clamp(announced.PrefixLength + random.Next(-8, 9), 0, width);

            // the announced bits, then random bits, cut to the new length
            var bits = width == 32 ? IPAddressBits.ToIPv4Bits(announced.BaseAddress) : IPAddressBits.ToIPv6Bits(announced.BaseAddress);
            var noise = ((UInt128)(ulong)random.NextInt64() << 64) | (ulong)random.NextInt64();
            var all = width == 32 ? (UInt128)uint.MaxValue : UInt128.MaxValue;
            UInt128 Mask(int l) => l == 0 ? UInt128.Zero : all & ~(all >> l);
            bits = ((bits | (noise & ~Mask(announced.PrefixLength))) & Mask(length)) & all;
            queries[i] = new IPNetwork(width == 32 ? IPAddressBits.FromIPv4Bits((uint)bits) : IPAddressBits.FromIPv6Bits(bits), length);
        }

        return new Data(lookup, lookup.Freeze(), queries);
    }

    [GlobalSetup]
    public void Setup() => _ = (Family == 4 ? V4 : V6).Value;

    [ParamsAllValues]
    public bool Frozen { get; set; }

    [Params(4, 6)]
    public int Family { get; set; }

    private IReadOnlyIPPrefixLookup<IPNetwork, int> Lookup => (Family == 4 ? V4 : V6).Value is var data && Frozen ? data.Frozen : data.ReadWrite;

    private IPNetwork[] Queries => (Family == 4 ? V4 : V6).Value.Queries;

    [Benchmark(Baseline = true)]
    public long TryGetMatch()
    {
        var lookup = Lookup;
        long sum = 0;
        foreach (var query in Queries)
            sum += lookup.TryGetMatch(query, out _, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark]
    public long Overlaps()
    {
        var lookup = Lookup;
        long sum = 0;
        foreach (var query in Queries)
            sum += lookup.Overlaps(query) ? 1 : 0;
        return sum;
    }

    [Benchmark]
    public long GetMatches()
    {
        var lookup = Lookup;
        long sum = 0;
        foreach (var query in Queries)
            sum += lookup.GetMatches(query).Count;
        return sum;
    }

    [Benchmark]
    public long GetSubnetsAny()
    {
        var lookup = Lookup;
        long sum = 0;
        foreach (var query in Queries)
            sum += lookup.GetSubnets(query).Any() ? 1 : 0;
        return sum;
    }

    [Benchmark]
    public long GetSubnetsAll()
    {
        var lookup = Lookup;
        long sum = 0;
        foreach (var query in Queries)
            foreach (var entry in lookup.GetSubnets(query))
                sum += entry.Value;
        return sum;
    }
}
