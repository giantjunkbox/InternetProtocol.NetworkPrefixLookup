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
using Bitvantage.InternetProtocol;

namespace GenericTrie;

/// <summary>Correctness checks and quick timings on the real RouteViews tables.</summary>
public static class RealDataChecks
{
    public static CachedKeyTrie<TKey, IPNetwork, int> BuildCached<TKey>(RealData.Table<TKey> table) where TKey : struct, IBitKey<TKey>
    {
        var t = new CachedKeyTrie<TKey, IPNetwork, int>();
        for (var i = 0; i < table.Bits.Length; i++) t.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
        return t;
    }

    public static PooledTrie<TKey, IPNetwork, int> BuildPooled<TKey>(RealData.Table<TKey> table, int indexBits) where TKey : struct, IBitKey<TKey>
    {
        var t = new PooledTrie<TKey, IPNetwork, int>();
        for (var i = 0; i < table.Bits.Length; i++) t.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
        t.Relayout(NodeOrder.DepthFirst);
        if (indexBits > 0) t.BuildIndex(indexBits);
        return t;
    }

    public static PooledTrie<TKey, IPNetwork, int> BuildMultiLevel<TKey>(RealData.Table<TKey> table, int rootBits, int subBits, int threshold, int width, bool report = false)
        where TKey : struct, IBitKey<TKey>
    {
        var t = BuildPooled(table, 0);
        var (subTables, bytes) = t.BuildMultiLevelIndex(rootBits, subBits, threshold, width);
        if (report)
            Console.WriteLine($"  {"",-26} multi-level {rootBits}/{subBits}/{threshold}: {subTables:N0} sub-tables, index {bytes / 1048576.0:F1} MB");
        return t;
    }

    public static SnapshotTrie<TKey, IPNetwork, int> BuildSnapshot<TKey>(RealData.Table<TKey> table, int width, int threshold = 8, bool compact = true)
        where TKey : struct, IBitKey<TKey>
    {
        var t = new SnapshotTrie<TKey, IPNetwork, int>(width, 16, 8, threshold);
        for (var i = 0; i < table.Bits.Length; i++) t.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
        if (compact) t.Compact();
        return t;
    }

    public static SnapshotTrie<TKey, IPNetwork, int> BulkSnapshot<TKey>(RealData.Table<TKey> table, int width, int threshold)
        where TKey : struct, IBitKey<TKey>
    {
        var t = new SnapshotTrie<TKey, IPNetwork, int>(width, 16, 8, threshold);
        t.BeginBulkLoad();
        for (var i = 0; i < table.Bits.Length; i++) t.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
        t.EndBulkLoad();
        return t;
    }

    /// <summary>Insert cost with the index kept current, the garbage that leaves, and what compaction costs and reclaims.</summary>
    public static void SnapshotCosts()
    {
        Costs("Real IPv4", RealData.V4, 32);
        Costs("Real IPv6", RealData.V6, 128);
    }

    private static void Costs<TKey>(string name, RealData.Table<TKey> table, int width) where TKey : struct, IBitKey<TKey>
    {
        foreach (var threshold in new[] { 8, 32 })
        {
            GC.Collect();
            var sw = Stopwatch.StartNew();
            var t = new SnapshotTrie<TKey, IPNetwork, int>(width, 16, 8, threshold);
            for (var i = 0; i < table.Bits.Length; i++) t.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
            var insert = sw.Elapsed.TotalMilliseconds;
            var garbage = t.Garbage;
            var before = t.MemoryInBytes();
            sw.Restart();
            t.Compact();
            var compact = sw.Elapsed.TotalMilliseconds;
            var after = t.MemoryInBytes();

            sw.Restart();
            for (var i = 0; i < table.Bits.Length; i += 100) t.TryRemove(in table.Bits[i], table.Lengths[i]);
            for (var i = 0; i < table.Bits.Length; i += 100) t.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
            var churnOps = 2 * ((table.Bits.Length + 99) / 100);
            var churn = sw.Elapsed.TotalMilliseconds;

            GC.Collect();
            sw.Restart();
            var bulk = new SnapshotTrie<TKey, IPNetwork, int>(width, 16, 8, threshold);
            bulk.BeginBulkLoad();
            for (var i = 0; i < table.Bits.Length; i++) bulk.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
            var bulkInsert = sw.Elapsed.TotalMilliseconds;
            bulk.EndBulkLoad();
            var bulkTotal = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"{name}, threshold {threshold}: bulk load {bulkInsert:F0} ms inserts + {bulkTotal - bulkInsert:F0} ms index = {bulkTotal:F0} ms, memory {bulk.MemoryInBytes() / 1048576.0:F1} MB");

            Console.WriteLine($"{name}, threshold {threshold}: insert all {insert:F0} ms ({insert * 1000 / table.Bits.Length:F2} us each), " +
                              $"garbage sub-table entries {garbage.SubEntries:N0}, memory {before / 1048576.0:F1} MB -> compact {compact:F0} ms -> {after / 1048576.0:F1} MB; " +
                              $"then {churnOps:N0} removes and re-adds: {churn * 1000 / churnOps:F2} us each");
        }
    }

    public static NetworkPrefixLookup<int> BuildCurrent<TKey>(RealData.Table<TKey> table) where TKey : struct, IBitKey<TKey>
    {
        var lookup = new NetworkPrefixLookup<int>();
        for (var i = 0; i < table.Networks.Length; i++)
            lookup.TryAdd(new NetworkPrefix(table.Networks[i].BaseAddress, table.Networks[i].PrefixLength), table.Asns[i]);
        return lookup;
    }

    public static void Verify()
    {
        VerifyTable("IPv4", RealData.V4, new[] { 0, 16, 18, 20 }, 32);
        VerifyTable("IPv6", RealData.V6, new[] { 0, 16, 20 }, 128);
    }

    private static void VerifyTable<TKey>(string name, RealData.Table<TKey> table, int[] indexSizes, int width) where TKey : struct, IBitKey<TKey>
    {
        Console.WriteLine($"{name}: {table.Bits.Length:N0} prefixes, {table.Keys.Length:N0} keys");
        var current = BuildCurrent(table);
        var expected = new string?[table.Keys.Length];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = current.TryGetMatch(table.Addresses[i], out var r) ? $"{r.Prefix.Address}/{r.Prefix.Length}" : null;

        void Check(string variant, Func<int, IPNetwork?> lookup)
        {
            var mismatches = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                var r = lookup(i);
                var text = r == null ? null : $"{r.Value.BaseAddress}/{r.Value.PrefixLength}";
                if (text != expected[i] && mismatches++ < 3)
                    Console.WriteLine($"    mismatch at {table.Addresses[i]}: expected {expected[i]}, got {text}");
            }
            Console.WriteLine($"  {variant,-26} mismatches: {mismatches}");
        }

        var cached = BuildCached(table);
        Check("cached (baseline)", i => cached.TryGetMatch(in table.Keys[i], out var p, out _) ? p : null);
        foreach (var bits in indexSizes)
        {
            var pooled = BuildPooled(table, bits);
            Check(bits == 0 ? "pooled, depth-first" : $"pooled, index {bits}", i => pooled.TryGetMatch(in table.Keys[i], out var p, out _) ? p : null);
            long visited = 0;
            for (var i = 0; i < table.Keys.Length; i++) visited += pooled.CountVisited(in table.Keys[i]);
            Console.WriteLine($"  {"",-26} nodes: {pooled.NodeCount:N0}, average nodes visited: {visited / (double)table.Keys.Length:F2}");
        }

        foreach (var (rootBits, subBits, threshold) in new[] { (16, 8, 8), (16, 8, 32), (16, 4, 4), (16, 4, 16) })
        {
            var ml = BuildMultiLevel(table, rootBits, subBits, threshold, width, report: true);
            Check($"multi-level {rootBits}/{subBits}/{threshold}", i => ml.TryGetMatch(in table.Keys[i], out var p, out _) ? p : null);
            long visited = 0;
            for (var i = 0; i < table.Keys.Length; i++) visited += ml.CountVisited(in table.Keys[i]);
            Console.WriteLine($"  {"",-26} average nodes visited: {visited / (double)table.Keys.Length:F2}");
        }
    }

    /// <summary>Quick timing on real data. Variants: v4-cached, v4-pooled, v4-index&lt;k&gt;, v6-cached, v6-pooled, v6-index&lt;k&gt;.</summary>
    public static void Time(string variant)
    {
        Func<long> run = variant switch
        {
            "v4-cached" => Single(RealData.V4, BuildCached(RealData.V4)),
            "v4-pooled" => Single(RealData.V4, BuildPooled(RealData.V4, 0)),
            _ when variant.StartsWith("v4-index") => Single(RealData.V4, BuildPooled(RealData.V4, int.Parse(variant[8..]))),
            "v6-cached" => Single(RealData.V6, BuildCached(RealData.V6)),
            "v6-pooled" => Single(RealData.V6, BuildPooled(RealData.V6, 0)),
            _ when variant.StartsWith("v6-index") => Single(RealData.V6, BuildPooled(RealData.V6, int.Parse(variant[8..]))),
            "v4-snap" => SingleSnapshot(RealData.V4, BuildSnapshot(RealData.V4, 32)),
            "v6-snap" => SingleSnapshot(RealData.V6, BuildSnapshot(RealData.V6, 128)),
            "v6-snap4" => SingleSnapshot(RealData.V6, BuildSnapshot(RealData.V6, 128, 4)),
            // churn: bulk load, remove a share of the prefixes and add them back in random order, optionally compact.
            // The contents end up identical, so any slowdown comes from layout and garbage. Example: v4-churn50, v6-churn50c
            _ when variant.StartsWith("v4-churn") => SingleSnapshot(RealData.V4, Churned(RealData.V4, 32, variant[8..])),
            _ when variant.StartsWith("v6-churn") => SingleSnapshot(RealData.V6, Churned(RealData.V6, 128, variant[8..])),
            _ when variant.StartsWith("v4-ml") => Single(RealData.V4, MultiLevel(RealData.V4, variant[5..], 32)),
            _ when variant.StartsWith("v6-ml") => Single(RealData.V6, MultiLevel(RealData.V6, variant[5..], 128)),
            _ => throw new ArgumentException(variant)
        };

        for (var i = 0; i < 5; i++) run();
        var best = double.MaxValue;
        for (var i = 0; i < 15; i++)
        {
            var sw = Stopwatch.StartNew();
            run();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"{variant,-16} best of 15: {best,7:F1} ms per 1M lookups");
    }

    private static SnapshotTrie<TKey, IPNetwork, int> Churned<TKey>(RealData.Table<TKey> table, int width, string spec) where TKey : struct, IBitKey<TKey>
    {
        var compact = spec.EndsWith('c');
        var percent = int.Parse(compact ? spec[..^1] : spec);
        var trie = BulkSnapshot(table, width, 8);
        var memoryFresh = trie.MemoryInBytes();

        var random = new Random(5);
        var chosen = Enumerable.Range(0, table.Bits.Length).Where(_ => random.Next(100) < percent).OrderBy(_ => random.Next()).ToArray();
        var sw = Stopwatch.StartNew();
        foreach (var i in chosen) trie.TryRemove(in table.Bits[i], table.Lengths[i]);
        foreach (var i in chosen.OrderBy(_ => random.Next())) trie.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
        var churnMs = sw.Elapsed.TotalMilliseconds;
        var garbage = trie.Garbage;
        var memoryChurned = trie.MemoryInBytes();
        if (compact) trie.Compact();

        Console.Error.WriteLine($"{percent}% churn: {chosen.Length * 2:N0} changes in {churnMs:F0} ms; garbage nodes {garbage.Nodes:N0}, values {garbage.Values:N0}, sub-table entries {garbage.SubEntries:N0}; " +
                                $"memory {memoryFresh / 1048576.0:F0} MB fresh, {memoryChurned / 1048576.0:F0} MB churned{(compact ? $", {trie.MemoryInBytes() / 1048576.0:F0} MB compacted" : "")}");
        return trie;
    }

    /// <summary>
    ///     Reader throughput on the real IPv4 table with three reader threads for 10 seconds, while the writer is idle,
    ///     makes continuous updates, or makes updates and compacts every 50,000 changes.
    /// </summary>
    public static void Interference()
    {
        var table = RealData.V4;
        foreach (var mode in new[] { "idle writer", "continuous updates", "updates and compaction", "idle writer again" })
        {
            var trie = BulkSnapshot(table, 32, 8);
            var stop = 0;
            long writes = 0, compactions = 0;
            var counts = new long[3];

            var writer = new Thread(() =>
            {
                if (mode.StartsWith("idle")) return;
                var random = new Random(9);
                var removed = new HashSet<int>();
                while (Volatile.Read(ref stop) == 0)
                {
                    var i = random.Next(table.Bits.Length);
                    if (removed.Remove(i)) trie.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
                    else { removed.Add(i); trie.TryRemove(in table.Bits[i], table.Lengths[i]); }
                    if (++writes % 50_000 == 0 && mode == "updates and compaction") { trie.Compact(); compactions++; }
                }
            });

            var readers = Enumerable.Range(0, 3).Select(r => new Thread(() =>
            {
                var keys = table.Keys;
                long n = 0;
                var k = r * 333_333;
                while (Volatile.Read(ref stop) == 0)
                {
                    for (var j = 0; j < 1000; j++)
                    {
                        trie.TryGetMatch(in keys[k], out _, out _);
                        if (++k == keys.Length) k = 0;
                    }
                    n += 1000;
                }
                counts[r] = n;
            })).ToArray();

            writer.Start();
            foreach (var t in readers) t.Start();
            Thread.Sleep(TimeSpan.FromSeconds(10));
            Volatile.Write(ref stop, 1);
            writer.Join();
            foreach (var t in readers) t.Join();

            var total = counts.Sum() / 10.0;
            Console.WriteLine($"{mode,-24} readers: {total / 1e6:F2} M lookups/s in total ({total / 3e6:F2} M per thread); writer: {writes / 10.0:N0} changes/s, {compactions} compactions");
        }
    }

    // spec "16-8-32": root bits, sub-table bits, threshold
    private static PooledTrie<TKey, IPNetwork, int> MultiLevel<TKey>(RealData.Table<TKey> table, string spec, int width) where TKey : struct, IBitKey<TKey>
    {
        var parts = spec.Split('-').Select(int.Parse).ToArray();
        return BuildMultiLevel(table, parts[0], parts[1], parts[2], width);
    }

    private static Func<long> SingleSnapshot<TKey>(RealData.Table<TKey> table, SnapshotTrie<TKey, IPNetwork, int> trie) where TKey : struct, IBitKey<TKey>
    {
        var keys = table.Keys;
        return () => { long sum = 0; for (var i = 0; i < keys.Length; i++) sum += trie.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v : -1; return sum; };
    }

    private static Func<long> Single<TKey>(RealData.Table<TKey> table, CachedKeyTrie<TKey, IPNetwork, int> trie) where TKey : struct, IBitKey<TKey>
    {
        var keys = table.Keys;
        return () => { long sum = 0; for (var i = 0; i < keys.Length; i++) sum += trie.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v : -1; return sum; };
    }

    private static Func<long> Single<TKey>(RealData.Table<TKey> table, PooledTrie<TKey, IPNetwork, int> trie) where TKey : struct, IBitKey<TKey>
    {
        var keys = table.Keys;
        return () => { long sum = 0; for (var i = 0; i < keys.Length; i++) sum += trie.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v : -1; return sum; };
    }
}
