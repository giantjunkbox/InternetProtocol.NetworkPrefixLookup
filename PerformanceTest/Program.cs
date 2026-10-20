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

using System.Diagnostics;
using System.Net;
using BenchmarkDotNet.Running;
using Bitvantage.InternetProtocol;

namespace PerformanceTest;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--memory")
        {
            Memory();
            return;
        }

        if (args.Length > 0 && args[0] == "--memory-comparison")
        {
            ComparisonMemory();
            return;
        }

        // pass the command line through so BenchmarkDotNet options such as --filter work; with no filter, run
        // everything, leaving out the real tables when ROUTEVIEWS_DIR is not set
        var types = RouteViews.Available
            ? new[] { typeof(RandomTableBenchmarks), typeof(MaxMindBenchmarks), typeof(RealTableBenchmarks), typeof(RealBuildBenchmarks), typeof(LayerBenchmarks), typeof(WriteBenchmarks), typeof(FrozenRootBenchmarks), typeof(ComparisonBenchmarks), typeof(ConsistencyBenchmarks) }
            : new[] { typeof(RandomTableBenchmarks), typeof(MaxMindBenchmarks), typeof(FrozenRootBenchmarks), typeof(ComparisonBenchmarks) };
        if (args.Length == 0)
            args = new[] { "--filter", "*" };
        BenchmarkSwitcher.FromTypes(types).Run(args);
    }

    /// <summary>The tables of <see cref="ComparisonData" />, reported like PerformanceTest.V2's --memory.</summary>
    private static void ComparisonMemory()
    {
        foreach (var name in ComparisonData.Names)
        {
            var data = ComparisonData.Get(name);
            var before = ComparisonData.HeapBytes();
            var sw = Stopwatch.StartNew();
            var lookup = new NetworkPrefixLookup<int>();
            for (var i = 0; i < data.Prefixes.Length; i++)
                lookup.TryAdd(data.Prefixes[i], data.Values[i]);
            var seconds = sw.Elapsed.TotalSeconds;
            var held = ComparisonData.HeapBytes() - before;
            var frozen = lookup.Freeze();
            var frozenHeld = ComparisonData.HeapBytes() - before - held;
            var compressed = lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }, new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });
            var compressedHeld = ComparisonData.HeapBytes() - before - held - frozenHeld;

            var allocated = GC.GetAllocatedBytesForCurrentThread();
            foreach (var address in data.Addresses.Take(100_000))
                lookup.TryGetMatch(address, out _, out _);
            var perLookup = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 100_000.0;
            Console.WriteLine($"{name,-10} v3: {held / 1048576.0,8:F1} MB held, built in {seconds:F2} s, {perLookup:F0} bytes allocated per lookup; " +
                              $"frozen {frozenHeld / 1048576.0:F1} MB, compressed {compressedHeld / 1048576.0:F1} MB");
            GC.KeepAlive(lookup);
            GC.KeepAlive(frozen);
            GC.KeepAlive(compressed);
        }
    }

    /// <summary>Memory held by each form on the real tables, and bytes allocated per lookup.</summary>
    private static void Memory()
    {
        void Report(string name, Func<object> build)
        {
            var before = ComparisonData.HeapBytes();
            var sw = Stopwatch.StartNew();
            var built = build();
            var seconds = sw.Elapsed.TotalSeconds;
            var after = ComparisonData.HeapBytes();
            Console.WriteLine($"{name,-44} {(after - before) / 1048576.0,8:F1} MB {seconds,8:F2} s");
            GC.KeepAlive(built);
        }

        var random = RandomTableBenchmarks.CreateNetworks();
        var addresses = RandomTableBenchmarks.CreateAddresses();
        var randomLookup = new NetworkPrefixLookup<string>(random.Select(n => KeyValuePair.Create(n, string.Empty)));
        var frozenRandom = randomLookup.Freeze();
        Console.WriteLine($"random table, {randomLookup.Count:N0} prefixes: read-write {randomLookup.GetStatistics().IPv4.Bytes / 1048576.0:F1} MB, " +
                          $"frozen leaf-pushed {frozenRandom.Bytes / 1048576.0:F1} MB, frozen compressed {randomLookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }).Bytes / 1048576.0:F1} MB");
        foreach (var (name, lookup) in new (string, IReadOnlyIPPrefixLookup<NetworkPrefix, string>)[] { ("read-write", randomLookup), ("frozen", frozenRandom) })
        {
            lookup.TryGetMatch(addresses[0], out _, out _);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            foreach (var address in addresses)
                lookup.TryGetMatch(address, out _, out _);
            Console.WriteLine($"bytes allocated per TryGetMatch(IPAddress), {name}: {(GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)addresses.Count:F1}");
        }

        if (!RouteViews.Available)
        {
            Console.WriteLine("Set ROUTEVIEWS_DIR for the real tables.");
            return;
        }

        var v4 = RouteViews.IPv4;
        var v6 = RouteViews.IPv6;
        IPNetworkLookup<int>? lookupV4 = null, lookupV6 = null;
        Report("IPv4 read-write, built as one batch", () => lookupV4 = RouteViews.Build(v4));
        Report("IPv4 frozen, leaf-pushed 16/8", () => lookupV4!.Freeze());
        Report("IPv4 frozen, compressed 18", () => lookupV4!.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }));
        Report("IPv6 read-write, built as one batch", () => lookupV6 = RouteViews.Build(v6));
        Report("IPv6 frozen, leaf-pushed 16/8", () => lookupV6!.Freeze(ipv6: new FrozenBitTrieOptions()));
        Report("IPv6 frozen, compressed 18", () => lookupV6!.Freeze(ipv6: new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }));
        Console.WriteLine($"IPv4 statistics: {lookupV4!.GetStatistics().IPv4}");
        Console.WriteLine($"IPv6 statistics: {lookupV6!.GetStatistics().IPv6}");
        Console.WriteLine($"IPv4 frozen bytes: leaf-pushed {lookupV4.Freeze().Bytes / 1048576.0:F1} MB, compressed {lookupV4.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }).Bytes / 1048576.0:F1} MB");
        Console.WriteLine($"IPv6 frozen bytes: leaf-pushed {lookupV6.Freeze().Bytes / 1048576.0:F1} MB, compressed {lookupV6.Freeze(ipv6: new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }).Bytes / 1048576.0:F1} MB");
    }
}
