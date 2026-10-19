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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Bart.NET;

namespace GenericTrie;

/// <summary>
///     Compares Bart.NET 1.2.1, a port of an early Go BART, and <see cref="BartStyleTrie{TKey}" />, the current Go BART
///     design written in C#, with this design on the real RouteViews tables: correctness, memory and build time here,
///     lookup and build speed in <see cref="RealBartBenchmarks" />.
/// </summary>
public static class BartComparison
{
    public static Table<int> BuildBart<TKey>(RealData.Table<TKey> table) where TKey : struct, IBitKey<TKey>
    {
        var bart = new Table<int>();
        for (var i = 0; i < table.Networks.Length; i++) bart.Insert(table.Networks[i], i);
        return bart;
    }

    public static BartStyleTrie<TKey> BuildBartStyle<TKey>(RealData.Table<TKey> table, int width) where TKey : struct, IBitKey<TKey>
    {
        var trie = new BartStyleTrie<TKey>(width);
        for (var i = 0; i < table.Bits.Length; i++) trie.Insert(in table.Bits[i], table.Lengths[i], i);
        return trie;
    }

    public static void Run()
    {
        GC.KeepAlive(RealData.V4);
        GC.KeepAlive(RealData.V6);
        Compare("Real IPv4", RealData.V4, 32, a => new Bits32(Optimization.FromAddressProperty(a)));
        Compare("Real IPv6", RealData.V6, 128, a => new Bits128(IPBits.V6(a)));
    }

    private static void Compare<TKey>(string name, RealData.Table<TKey> table, int width, Func<IPAddress, TKey> toKey) where TKey : struct, IBitKey<TKey>
    {
        // the reference: SnapshotTrie, checked against the current library elsewhere, with the prefix index as the value
        var reference = new SnapshotTrie<TKey, int, int>(width);
        reference.BeginBulkLoad();
        for (var i = 0; i < table.Bits.Length; i++) reference.TryAdd(in table.Bits[i], table.Lengths[i], i, i);
        reference.EndBulkLoad();

        var bart = BuildBart(table);
        var mismatches = 0;
        for (var k = 0; k < table.Addresses.Length; k++)
        {
            var expected = reference.TryGetMatch(toKey(table.Addresses[k]), out _, out var v) ? v : -1;
            var actual = bart.TryGetValue(table.Addresses[k], out var b) ? b : -1;
            if (expected != actual) mismatches++;
        }
        Console.WriteLine($"{name}: Bart.NET mismatches against the reference: {mismatches:N0} of {table.Addresses.Length:N0}");

        var style = BuildBartStyle(table, width);
        mismatches = 0;
        for (var k = 0; k < table.Keys.Length; k++)
        {
            var expected = reference.TryGetMatch(in table.Keys[k], out _, out var v) ? v : -1;
            if (style.Lookup(in table.Keys[k]) != expected) mismatches++;
        }
        Console.WriteLine($"  BART-style trie in C#: {mismatches:N0} mismatches, {style.NodeCount:N0} nodes");

        var styleBytes = Retained(() => BuildBartStyle(table, width));
        var bartBytes = Retained(() => BuildBart(table));
        var snapshotBytes = Retained(() => RealDataChecks.BulkSnapshot(table, width, 8));
        var frozenBytes = Retained(() => RealDataChecks.BuildLeafPushed(table, 16, 8, width));
        Console.WriteLine($"  memory retained: BART-style in C# {Mb(styleBytes)}, Bart.NET {Mb(bartBytes)}, SnapshotTrie threshold 8 {Mb(snapshotBytes)}, " +
                          $"PooledTrie with leaf-pushed 16/8 tables, nodes kept {Mb(frozenBytes)}");

        var styleMs = Best(() => BuildBartStyle(table, width));
        var bartMs = Best(() => BuildBart(table));
        var snapshotMs = Best(() => RealDataChecks.BulkSnapshot(table, width, 8));
        Console.WriteLine($"  build, best of 3: BART-style in C# {styleMs:N0} ms, Bart.NET {bartMs:N0} ms, SnapshotTrie bulk load {snapshotMs:N0} ms");
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:F1} MB";

    private static long Retained(Func<object> build)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);
        var result = build();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var after = GC.GetTotalMemory(true);
        GC.KeepAlive(result);
        return after - before;
    }

    private static double Best(Func<object> build)
    {
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            var sw = Stopwatch.StartNew();
            GC.KeepAlive(build());
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }
}

/// <summary>
///     One million lookups from IPAddress objects, and building each table, against Bart.NET 1.2.1 on the real RouteViews
///     tables. Bart.NET is the baseline in each category. Bart.NET returns only the value; the other designs also return
///     the matched prefix. Each table is built only in the process that measures it.
/// </summary>
[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class RealBartBenchmarks
{
    private static readonly Lazy<Table<int>> V4Bart = new(() => BartComparison.BuildBart(RealData.V4));
    private static readonly Lazy<Bitvantage.InternetProtocol.NetworkPrefixLookup<int>> V4Current = new(() => RealDataChecks.BuildCurrent(RealData.V4));
    private static readonly Lazy<SnapshotTrie<Bits32, IPNetwork, int>> V4Snapshot = new(() => RealDataChecks.BulkSnapshot(RealData.V4, 32, 8));
    private static readonly Lazy<PooledTrie<Bits32, IPNetwork, int>> V4Frozen = new(() => RealDataChecks.BuildLeafPushed(RealData.V4, 16, 8, 32));
    private static readonly Lazy<BartStyleTrie<Bits32>> V4Style = new(() => BartComparison.BuildBartStyle(RealData.V4, 32));
    private static readonly Lazy<Table<int>> V6Bart = new(() => BartComparison.BuildBart(RealData.V6));
    private static readonly Lazy<BartStyleTrie<Bits128>> V6Style = new(() => BartComparison.BuildBartStyle(RealData.V6, 128));

    // a dense copy of the values, as a frozen lookup would keep for value-only lookups
    private static (PooledTrie<TKey, IPNetwork, int>, int[]) WithValues<TKey>(PooledTrie<TKey, IPNetwork, int> trie) where TKey : struct, IBitKey<TKey> =>
        (trie, Enumerable.Range(0, trie.ValueCount).Select(trie.GetValue).ToArray());
    private static readonly Lazy<Bitvantage.InternetProtocol.NetworkPrefixLookup<int>> V6Current = new(() => RealDataChecks.BuildCurrent(RealData.V6));
    private static readonly Lazy<SnapshotTrie<Bits128, IPNetwork, int>> V6Snapshot = new(() => RealDataChecks.BulkSnapshot(RealData.V6, 128, 8));
    private static readonly Lazy<PooledTrie<Bits128, IPNetwork, int>> V6Frozen = new(() => RealDataChecks.BuildLeafPushed(RealData.V6, 16, 8, 128));
    private static readonly Lazy<(PooledTrie<Bits32, IPNetwork, int> Trie, int[] Values)> V4FrozenValues = new(() => WithValues(V4Frozen.Value));
    private static readonly Lazy<(PooledTrie<Bits128, IPNetwork, int> Trie, int[] Values)> V6FrozenValues = new(() => WithValues(V6Frozen.Value));

    [Benchmark(Baseline = true), IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_BartNet() { var t = V4Bart.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) sum += t.TryGetValue(a, out var v) ? v : -1; return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_CurrentLibrary() { var t = V4Current.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) sum += t.TryGetMatch(a, out var r) ? r.Value : -1; return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_ReadWrite_SnapshotTrie() { var t = V4Snapshot.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) { var k = new Bits32(Optimization.FromAddressProperty(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_Frozen_LeafPushed() { var t = V4Frozen.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) { var k = new Bits32(Optimization.FromAddressProperty(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_BartStyle_CSharp() { var t = V4Style.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) sum += t.Lookup(new Bits32(Optimization.FromAddressProperty(a))); return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_Frozen_LeafPushed_ValueOnly() { var (t, values) = V4FrozenValues.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) { var i = t.GetMatchIndex(new Bits32(Optimization.FromAddressProperty(a))); sum += i >= 0 ? values[i] : -1; } return sum; }

    [Benchmark(Baseline = true), IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_BartNet() { var t = V6Bart.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) sum += t.TryGetValue(a, out var v) ? v : -1; return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_CurrentLibrary() { var t = V6Current.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) sum += t.TryGetMatch(a, out var r) ? r.Value : -1; return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_ReadWrite_SnapshotTrie() { var t = V6Snapshot.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) { var k = new Bits128(IPBits.V6(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_Frozen_LeafPushed() { var t = V6Frozen.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) { var k = new Bits128(IPBits.V6(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_BartStyle_CSharp() { var t = V6Style.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) sum += t.Lookup(new Bits128(IPBits.V6(a))); return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_Frozen_LeafPushed_ValueOnly() { var (t, values) = V6FrozenValues.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) { var i = t.GetMatchIndex(new Bits128(IPBits.V6(a))); sum += i >= 0 ? values[i] : -1; } return sum; }

    [Benchmark(Baseline = true), IterationCount(10), BenchmarkCategory("IPv4 build")]
    public object IPv4_Build_BartNet() => BartComparison.BuildBart(RealData.V4);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv4 build")]
    public object IPv4_Build_SnapshotTrie_BulkLoad() => RealDataChecks.BulkSnapshot(RealData.V4, 32, 8);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv4 build")]
    public object IPv4_Build_BartStyle_CSharp() => BartComparison.BuildBartStyle(RealData.V4, 32);

    [Benchmark(Baseline = true), IterationCount(10), BenchmarkCategory("IPv6 build")]
    public object IPv6_Build_BartNet() => BartComparison.BuildBart(RealData.V6);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv6 build")]
    public object IPv6_Build_SnapshotTrie_BulkLoad() => RealDataChecks.BulkSnapshot(RealData.V6, 128, 8);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv6 build")]
    public object IPv6_Build_BartStyle_CSharp() => BartComparison.BuildBartStyle(RealData.V6, 128);
}
