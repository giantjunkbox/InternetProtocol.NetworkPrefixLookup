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

    // spec "16-8-32": root bits, sub-table bits, threshold
    private static PooledTrie<TKey, IPNetwork, int> MultiLevel<TKey>(RealData.Table<TKey> table, string spec, int width) where TKey : struct, IBitKey<TKey>
    {
        var parts = spec.Split('-').Select(int.Parse).ToArray();
        return BuildMultiLevel(table, parts[0], parts[1], parts[2], width);
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
