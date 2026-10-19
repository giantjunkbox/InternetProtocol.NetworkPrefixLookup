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
using Bitvantage.InternetProtocol;

namespace GenericTrie;

/// <summary>
///     Memory kept by each design after building, measured as the growth of the managed heap after full collections.
///     The callers' own key objects (NetworkPrefix objects for the current library, IPNetwork values and their IPAddress
///     objects for the others) are created before the measurement and are not counted.
/// </summary>
public static class MemoryComparison
{
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

    public static void Run()
    {
        // create all shared test data first, so that no measurement window includes it
        GC.KeepAlive(Data.Prefixes);
        GC.KeepAlive(Data.IPNetworks);
        GC.KeepAlive(WidthData.Prefix32);
        GC.KeepAlive(RealData.V4);
        GC.KeepAlive(RealData.V6);

        Console.WriteLine("| Table | Design | Retained | Bytes per prefix |");
        Console.WriteLine("| --- | --- | --- | --- |");

        // random IPv4 benchmark data: NetworkPrefix inputs already exist in Data.Prefixes
        Report("Random IPv4, 96,944 prefixes", Data.Prefixes.Count, Data.Prefixes,
            () => { var l = new NetworkPrefixLookup<string>(); foreach (var p in Data.Prefixes) l.TryAdd(p, string.Empty); return l; },
            Optimization.BuildCached,
            trim => { var t = Optimization.BuildPooled(); if (trim) t.TrimExcess(); return t; },
            (trim, bits) => { var t = Optimization.BuildPooled(NodeOrder.DepthFirst, bits); if (trim) t.TrimExcess(); return t; },
            16);

        foreach (var (name, table, bits) in new (string, object, int)[] { ("Real IPv4", RealData.V4, 20), ("Real IPv6", RealData.V6, 20) })
        {
            if (table is RealData.Table<Bits32> v4) ReportReal(name, v4, bits);
            else ReportReal(name, (RealData.Table<Bits128>)table, bits);
        }
    }

    private static void ReportReal<TKey>(string name, RealData.Table<TKey> table, int bits) where TKey : struct, IBitKey<TKey>
    {
        var prefixes = table.Networks.Select(n => new NetworkPrefix(n.BaseAddress, n.PrefixLength)).ToArray();
        Report($"{name}, {table.Bits.Length:N0} prefixes", table.Bits.Length, prefixes,
            () => { var l = new NetworkPrefixLookup<int>(); for (var i = 0; i < prefixes.Length; i++) l.TryAdd(prefixes[i], table.Asns[i]); return l; },
            () => RealDataChecks.BuildCached(table),
            trim =>
            {
                var t = new PooledTrie<TKey, IPNetwork, int>();
                for (var i = 0; i < table.Bits.Length; i++) t.TryAdd(in table.Bits[i], table.Lengths[i], table.Networks[i], table.Asns[i]);
                if (trim) t.TrimExcess();
                return t;
            },
            (trim, indexBits) => { var t = RealDataChecks.BuildPooled(table, indexBits); if (trim) t.TrimExcess(); return t; },
            bits);
    }

    private static void Report(string name, int count, object keepAlive, Func<object> current, Func<object> cached,
        Func<bool, object> pooled, Func<bool, int, object> indexed, int indexBits)
    {
        void Row(string design, long bytes) =>
            Console.WriteLine($"| {name} | {design} | {bytes / 1048576.0:F1} MB | {bytes / (double)count:F0} |");

        Row("Current library", Retained(current));
        Row("Baseline: CachedKeyTrie", Retained(cached));
        Row("Node pool, as built", Retained(() => pooled(false)));
        Row("Node pool, trimmed", Retained(() => pooled(true)));
        Row($"Node pool, depth-first, {indexBits}-bit index, as built", Retained(() => indexed(false, indexBits)));
        Row($"Node pool, depth-first, {indexBits}-bit index, trimmed", Retained(() => indexed(true, indexBits)));
        GC.KeepAlive(keepAlive);

        dynamic trimmed = indexed(true, indexBits);
        var (nodes, lengths, entries, index) = ((long, long, long, long))trimmed.MemoryBreakdown();
        Console.WriteLine($"|   | breakdown, trimmed: nodes {nodes / 1048576.0:F1} MB, lengths {lengths / 1048576.0:F1} MB, prefixes and values {entries / 1048576.0:F1} MB, index {index / 1048576.0:F1} MB | | |");
    }
}
