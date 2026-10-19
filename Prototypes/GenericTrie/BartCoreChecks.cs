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

namespace GenericTrie;

/// <summary>
///     Measurements of <see cref="BartTrie{TKey,TPrefix,TValue}" /> against <see cref="SnapshotTrie{TKey,TPrefix,TValue}" />
///     on the real tables: memory, and worst-case latency with work per write under churn. Lookup and build speed are in
///     <see cref="BartCoreBenchmarks" />.
/// </summary>
public static class BartCoreChecks
{
    public static BartTrie<TKey, IPNetwork, int> BuildBart<TKey>(RealData.Table<TKey> table, int width, bool bulk = true) where TKey : struct, IBitKey<TKey>
    {
        var trie = new BartTrie<TKey, IPNetwork, int>(width);
        if (bulk) trie.BeginBulkLoad();
        for (var i = 0; i < table.Bits.Length; i++) trie.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
        if (bulk) trie.EndBulkLoad();
        return trie;
    }

    public static void Memory()
    {
        GC.KeepAlive(RealData.V4);
        GC.KeepAlive(RealData.V6);
        MemoryOf("Real IPv4", RealData.V4, 32);
        MemoryOf("Real IPv6", RealData.V6, 128);
    }

    private static void MemoryOf<TKey>(string name, RealData.Table<TKey> table, int width) where TKey : struct, IBitKey<TKey>
    {
        var bulk = Retained(() => BuildBart(table, width));
        var live = Retained(() => BuildBart(table, width, bulk: false));
        var snapshot = Retained(() => RealDataChecks.BulkSnapshot(table, width, 8));

        // churn: remove and re-add half the prefixes in random order; the contents end up the same
        var trie = BuildBart(table, width);
        var random = new Random(5);
        var chosen = Enumerable.Range(0, table.Bits.Length).Where(_ => random.Next(2) == 0).OrderBy(_ => random.Next()).ToArray();
        var before = Heap();
        foreach (var i in chosen) trie.TryRemove(in table.Bits[i], table.Lengths[i]);
        foreach (var i in chosen.OrderBy(_ => random.Next())) trie.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
        var growth = Heap() - before;
        GC.KeepAlive(trie);

        Console.WriteLine($"{name}: BartTrie {Mb(bulk)} bulk loaded, {Mb(live)} added one at a time, {Mb(growth)} growth after 50% churn; SnapshotTrie {Mb(snapshot)}");
    }

    private static long Heap()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return GC.GetTotalMemory(true);
    }

    private static long Retained(Func<object> build)
    {
        var before = Heap();
        var result = build();
        var after = Heap();
        GC.KeepAlive(result);
        return after - before;
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:F1} MB";

    /// <summary>
    ///     The tail-latency test of <see cref="RealDataChecks.TailLatency" /> for BartTrie: one writer makes a million
    ///     changes to the real IPv4 table as fast as it can while one reader times every lookup. Also counts the array
    ///     elements and nodes each write copies, which timer noise cannot distort.
    /// </summary>
    public static void TailLatency()
    {
        const int changes = 1_000_000;
        var table = RealData.V4;
        var trie = BuildBart(table, 32);
        var writeNs = new long[changes];
        var work = new long[changes];
        var slowest = new List<(long Ns, string What)>();
        var readHistogram = new long[1400];
        long readMax = 0, reads = 0;
        var stop = 0;

        var heapBefore = Heap();
        var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var pauseBefore = GC.GetTotalPauseDuration();

        var reader = new Thread(() =>
        {
            var keys = table.Keys;
            var k = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                var t0 = Stopwatch.GetTimestamp();
                trie.TryGetMatch(in keys[k], out _, out _);
                var ns = (Stopwatch.GetTimestamp() - t0) * 1_000_000_000L / Stopwatch.Frequency;
                readHistogram[Bucket(ns)]++;
                if (ns > readMax) readMax = ns;
                reads++;
                if (++k == keys.Length) k = 0;
            }
        });
        reader.Start();

        var random = new Random(9);
        var removed = new HashSet<int>(changes);
        trie.TakeWork();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var c = 0; c < changes; c++)
        {
            var i = random.Next(table.Bits.Length);
            var adding = removed.Remove(i);
            var t0 = Stopwatch.GetTimestamp();
            if (adding) trie.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
            else trie.TryRemove(in table.Bits[i], table.Lengths[i]);
            writeNs[c] = (Stopwatch.GetTimestamp() - t0) * 1_000_000_000L / Stopwatch.Frequency;
            work[c] = trie.TakeWork();
            if (!adding) removed.Add(i);
            if (slowest.Count < 6 || writeNs[c] > slowest[^1].Ns)
            {
                slowest.Add((writeNs[c], $"{(adding ? "add" : "remove")} /{table.Lengths[i]}, {work[c]} copied"));
                slowest.Sort((a, b) => b.Ns.CompareTo(a.Ns));
                if (slowest.Count > 6) slowest.RemoveAt(6);
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Volatile.Write(ref stop, 1);
        reader.Join();
        var pause = GC.GetTotalPauseDuration() - pauseBefore;
        var gc = (GC.CollectionCount(0) - gcBefore.Item1, GC.CollectionCount(1) - gcBefore.Item2, GC.CollectionCount(2) - gcBefore.Item3);
        foreach (var i in removed) trie.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
        var heapGrowth = Heap() - heapBefore;

        var total = writeNs.Sum();
        Array.Sort(writeNs);
        Array.Sort(work);
        string W(double q) => Format(writeNs[(int)Math.Min(writeNs.Length - 1, q * writeNs.Length)]);
        long K(double q) => work[(int)Math.Min(work.Length - 1, q * work.Length)];
        string R(double q) => Format(Percentile(readHistogram, reads, q));
        Console.WriteLine($"BartTrie, {changes:N0} changes to real IPv4, {total / 1e9:F1} s in total; heap growth after re-adding the removed prefixes: {Mb(heapGrowth)}");
        Console.WriteLine($"  writes:  p50 {W(0.5)}, p99 {W(0.99)}, p99.9 {W(0.999)}, p99.99 {W(0.9999)}, max {Format(writeNs[^1])}");
        Console.WriteLine($"  copied per write: median {K(0.5)}, p99 {K(0.99)}, p99.99 {K(0.9999)}, max {work[^1]} array elements and nodes");
        Console.WriteLine($"  lookups: p50 {R(0.5)}, p99 {R(0.99)}, p99.9 {R(0.999)}, p99.99 {R(0.9999)}, max {Format(readMax)}, {reads:N0} lookups");
        Console.WriteLine($"  garbage collections: {gc.Item1} gen0, {gc.Item2} gen1, {gc.Item3} gen2, {pause.TotalMilliseconds:F0} ms paused in total; " +
                          $"{allocated / (double)changes:N0} bytes allocated per write");
        Console.WriteLine("  slowest writes: " + string.Join("; ", slowest.Select(w => $"{Format(w.Ns)} {w.What}")));
    }

    /// <summary>
    ///     Quick timings, best of 15, of one million lookups from precomputed keys: SnapshotTrie and BartTrie on the real
    ///     IPv4 and IPv6 tables and on the random benchmark table. For steering optimizations; final numbers come from
    ///     <see cref="BartCoreBenchmarks" />.
    /// </summary>
    public static void Time()
    {
        var v4 = RealData.V4;
        var v6 = RealData.V6;
        var randomKeys = WidthData.Key32;
        var snapshotRandom = new SnapshotTrie<Bits32, int, int>(32);
        snapshotRandom.BeginBulkLoad();
        var bartRandom = new BartTrie<Bits32, int, int>(32);
        bartRandom.BeginBulkLoad();
        for (var i = 0; i < WidthData.Prefix32.Length; i++)
        {
            snapshotRandom.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i);
            bartRandom.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i);
        }
        snapshotRandom.EndBulkLoad();
        bartRandom.EndBulkLoad();

        var snapshot4 = RealDataChecks.BulkSnapshot(v4, 32, 8);
        var bart4 = BuildBart(v4, 32);
        var snapshot6 = RealDataChecks.BulkSnapshot(v6, 128, 8);
        var bart6 = BuildBart(v6, 128);

        Report("random, SnapshotTrie", () => { long s = 0; foreach (var k in randomKeys) s += snapshotRandom.TryGetMatch(in k, out var p, out _) ? p : -1; return s; });
        Report("random, BartTrie", () => { long s = 0; foreach (var k in randomKeys) s += bartRandom.TryGetMatch(in k, out var p, out _) ? p : -1; return s; });
        Report("IPv4, SnapshotTrie", () => { long s = 0; foreach (var k in v4.Keys) s += snapshot4.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; return s; });
        Report("IPv4, BartTrie", () => { long s = 0; foreach (var k in v4.Keys) s += bart4.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; return s; });
        Report("IPv6, SnapshotTrie", () => { long s = 0; foreach (var k in v6.Keys) s += snapshot6.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; return s; });
        Report("IPv6, BartTrie", () => { long s = 0; foreach (var k in v6.Keys) s += bart6.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; return s; });
    }

    private static void Report(string name, Func<long> run)
    {
        for (var i = 0; i < 5; i++) run();
        var best = double.MaxValue;
        for (var i = 0; i < 15; i++)
        {
            var sw = Stopwatch.StartNew();
            run();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"  {name,-22} {best,7:F1} ms per 1M lookups");
    }

    private static int Bucket(long ns) => ns < 10_000 ? (int)(ns / 10) : Math.Min(1399, 1000 + (int)(Math.Log(ns / 10_000.0) / Math.Log(1.05)));

    private static long UpperBound(int bucket) => bucket < 1000 ? (bucket + 1) * 10L : (long)(10_000 * Math.Pow(1.05, bucket - 1000 + 1));

    private static long Percentile(long[] histogram, long total, double q)
    {
        var target = (long)Math.Ceiling(q * total);
        long seen = 0;
        for (var b = 0; b < histogram.Length; b++)
            if ((seen += histogram[b]) >= target)
                return UpperBound(b);
        return UpperBound(histogram.Length - 1);
    }

    private static string Format(long ns) => ns < 10_000 ? $"{ns:N0} ns" : ns < 10_000_000 ? $"{ns / 1000.0:N0} us" : $"{ns / 1e6:N0} ms";
}

/// <summary>
///     BartTrie against SnapshotTrie in one session: one million lookups from IPAddress objects on the real tables, one
///     million lookups from precomputed keys on the random benchmark table, which fits in cache, and building the real
///     tables. SnapshotTrie is the baseline in each category. Each table is built only in the process that measures it.
/// </summary>
[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class BartCoreBenchmarks
{
    private static readonly Lazy<SnapshotTrie<Bits32, IPNetwork, int>> V4Snapshot = new(() => RealDataChecks.BulkSnapshot(RealData.V4, 32, 8));
    private static readonly Lazy<BartTrie<Bits32, IPNetwork, int>> V4Bart = new(() => BartCoreChecks.BuildBart(RealData.V4, 32));
    private static readonly Lazy<PooledTrie<Bits32, IPNetwork, int>> V4Frozen = new(() => RealDataChecks.BuildLeafPushed(RealData.V4, 16, 8, 32));
    private static readonly Lazy<SnapshotTrie<Bits128, IPNetwork, int>> V6Snapshot = new(() => RealDataChecks.BulkSnapshot(RealData.V6, 128, 8));
    private static readonly Lazy<BartTrie<Bits128, IPNetwork, int>> V6Bart = new(() => BartCoreChecks.BuildBart(RealData.V6, 128));
    private static readonly Lazy<PooledTrie<Bits128, IPNetwork, int>> V6Frozen = new(() => RealDataChecks.BuildLeafPushed(RealData.V6, 16, 8, 128));
    private static readonly Lazy<SnapshotTrie<Bits32, int, int>> RandomSnapshot = new(BuildRandomSnapshot);
    private static readonly Lazy<BartTrie<Bits32, int, int>> RandomBart = new(BuildRandomBart);

    private static SnapshotTrie<Bits32, int, int> BuildRandomSnapshot()
    {
        var t = new SnapshotTrie<Bits32, int, int>(32, 16, 8, 8);
        t.BeginBulkLoad();
        for (var i = 0; i < WidthData.Prefix32.Length; i++) t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i);
        t.EndBulkLoad();
        return t;
    }

    private static BartTrie<Bits32, int, int> BuildRandomBart()
    {
        var t = new BartTrie<Bits32, int, int>(32);
        t.BeginBulkLoad();
        for (var i = 0; i < WidthData.Prefix32.Length; i++) t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i);
        t.EndBulkLoad();
        return t;
    }

    [Benchmark(Baseline = true), IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_SnapshotTrie() { var t = V4Snapshot.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) { var k = new Bits32(Optimization.FromAddressProperty(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_BartTrie() { var t = V4Bart.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) { var k = new Bits32(Optimization.FromAddressProperty(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv4 lookup")]
    public long IPv4_Frozen() { var t = V4Frozen.Value; long sum = 0; foreach (var a in RealData.V4.Addresses) { var k = new Bits32(Optimization.FromAddressProperty(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark(Baseline = true), IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_SnapshotTrie() { var t = V6Snapshot.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) { var k = new Bits128(IPBits.V6(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_BartTrie() { var t = V6Bart.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) { var k = new Bits128(IPBits.V6(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("IPv6 lookup")]
    public long IPv6_Frozen() { var t = V6Frozen.Value; long sum = 0; foreach (var a in RealData.V6.Addresses) { var k = new Bits128(IPBits.V6(a)); sum += t.TryGetMatch(in k, out var p, out var v) ? p.PrefixLength + v : -1; } return sum; }

    [Benchmark(Baseline = true), IterationCount(20), BenchmarkCategory("Random table in cache")]
    public long Random_SnapshotTrie() { var t = RandomSnapshot.Value; long sum = 0; foreach (var k in WidthData.Key32) sum += t.TryGetMatch(in k, out var p, out var v) ? p + v : -1; return sum; }

    [Benchmark, IterationCount(20), BenchmarkCategory("Random table in cache")]
    public long Random_BartTrie() { var t = RandomBart.Value; long sum = 0; foreach (var k in WidthData.Key32) sum += t.TryGetMatch(in k, out var p, out var v) ? p + v : -1; return sum; }

    [Benchmark(Baseline = true), IterationCount(10), BenchmarkCategory("IPv4 build")]
    public object IPv4_Build_SnapshotTrie_BulkLoad() => RealDataChecks.BulkSnapshot(RealData.V4, 32, 8);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv4 build")]
    public object IPv4_Build_BartTrie_BulkLoad() => BartCoreChecks.BuildBart(RealData.V4, 32);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv4 build")]
    public object IPv4_Build_BartTrie_OneAtATime() => BartCoreChecks.BuildBart(RealData.V4, 32, bulk: false);

    [Benchmark(Baseline = true), IterationCount(10), BenchmarkCategory("IPv6 build")]
    public object IPv6_Build_SnapshotTrie_BulkLoad() => RealDataChecks.BulkSnapshot(RealData.V6, 128, 8);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv6 build")]
    public object IPv6_Build_BartTrie_BulkLoad() => BartCoreChecks.BuildBart(RealData.V6, 128);

    [Benchmark, IterationCount(10), BenchmarkCategory("IPv6 build")]
    public object IPv6_Build_BartTrie_OneAtATime() => BartCoreChecks.BuildBart(RealData.V6, 128, bulk: false);
}
