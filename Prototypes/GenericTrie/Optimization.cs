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

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace GenericTrie;

/// <summary>
///     Builders, correctness checks and quick timings for the optimization steps. All variants store the benchmark's
///     IPv4 prefixes as IPNetwork values with string values, and look up the same 1,000,000 precomputed 32-bit keys.
/// </summary>
public static class Optimization
{
    public static CachedKeyTrie<Bits32, IPNetwork, string> BuildCached()
    {
        var t = new CachedKeyTrie<Bits32, IPNetwork, string>();
        for (var i = 0; i < WidthData.Prefix32.Length; i++) t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        return t;
    }

    public static InlineChildTrie<Bits32, IPNetwork, string> BuildInline()
    {
        var t = new InlineChildTrie<Bits32, IPNetwork, string>();
        for (var i = 0; i < WidthData.Prefix32.Length; i++) t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        return t;
    }

    public static PooledTrie<Bits32, IPNetwork, string> BuildPooled(NodeOrder? order = null, int indexBits = 0, int count = -1, bool candidate = false)
    {
        var t = new PooledTrie<Bits32, IPNetwork, string>();
        if (count < 0) count = WidthData.Prefix32.Length;
        for (var i = 0; i < count; i++) t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        if (count < WidthData.Prefix32.Length) t.TryAdd(new Bits32(0), 0, IPNetwork.Parse("0.0.0.0/0"), string.Empty); // small tables get a default route
        if (order != null) t.Relayout(order.Value);
        if (indexBits > 0)
        {
            if (candidate) t.BuildCandidateIndex(indexBits);
            else t.BuildIndex(indexBits);
        }
        return t;
    }

    public static InheritedBestTrie<Bits32, IPNetwork, string> BuildInherited(int indexBits = 0, int count = -1, bool relayout = true)
    {
        var t = new InheritedBestTrie<Bits32, IPNetwork, string>();
        if (count < 0) count = WidthData.Prefix32.Length;
        for (var i = 0; i < count; i++) t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        if (count < WidthData.Prefix32.Length) t.TryAdd(new Bits32(0), 0, IPNetwork.Parse("0.0.0.0/0"), string.Empty);
        if (relayout) t.RelayoutDepthFirst();
        if (indexBits > 0) t.BuildIndex(indexBits);
        return t;
    }

    private static IPNetwork[] Expected()
    {
        var reference = WidthData.BuildRef();
        var expected = new IPNetwork[WidthData.KeyRef.Length];
        for (var i = 0; i < expected.Length; i++)
        {
            reference.TryGetMatch(WidthData.KeyRef[i], out var index, out _);
            expected[i] = Data.IPNetworks[index];
        }
        return expected;
    }

    public static void Verify()
    {
        var expected = Expected();
        var keys = WidthData.Key32;

        void Check(string name, Func<int, IPNetwork?> lookup)
        {
            var mismatches = 0;
            for (var i = 0; i < keys.Length; i++)
                if (lookup(i) != expected[i])
                    mismatches++;
            Console.WriteLine($"  {name,-28} mismatches: {mismatches}");
        }

        void CheckBatch(string name, PooledTrie<Bits32, IPNetwork, string> trie, int lanes, bool prefetch)
        {
            var results = new int[keys.Length];
            trie.GetMatches(keys, results, lanes, prefetch);
            Check(name, i => results[i] >= 0 ? trie.GetPrefix(results[i]) : null);
        }

        Console.WriteLine($"verifying against the reference for {keys.Length:N0} keys");
        var cached = BuildCached();
        Check("cached (baseline)", i => cached.TryGetMatch(in keys[i], out var p, out _) ? p : null);
        var inline = BuildInline();
        Check("inline children", i => inline.TryGetMatch(in keys[i], out var p, out _) ? p : null);
        var pooled = BuildPooled();
        Check("pooled", i => pooled.TryGetMatch(in keys[i], out var p, out _) ? p : null);
        Console.WriteLine($"  pooled node size: {pooled.NodeSizeInBytes} bytes, nodes: {pooled.NodeCount:N0}");
        var bfs = BuildPooled(NodeOrder.BreadthFirst);
        Check("pooled, breadth-first", i => bfs.TryGetMatch(in keys[i], out var p, out _) ? p : null);
        var dfs = BuildPooled(NodeOrder.DepthFirst);
        Check("pooled, depth-first", i => dfs.TryGetMatch(in keys[i], out var p, out _) ? p : null);
        foreach (var bits in new[] { 8, 12, 16, 20 })
        {
            var indexed = BuildPooled(NodeOrder.DepthFirst, bits);
            Check($"pooled, depth-first, index {bits}", i => indexed.TryGetMatch(in keys[i], out var p, out _) ? p : null);
        }
        foreach (var bits in new[] { 8, 12, 15, 16, 17 })
        {
            var indexed = BuildPooled(NodeOrder.DepthFirst, bits, candidate: true);
            Check($"candidate index {bits}", i => indexed.TryGetMatch(in keys[i], out var p, out _) ? p : null);
            var small = BuildPooled(NodeOrder.DepthFirst, bits, 1_000, candidate: true);
            var smallReference = BuildPooled(NodeOrder.DepthFirst, 0, 1_000);
            var smallMismatches = 0;
            for (var i = 0; i < keys.Length; i++)
            {
                small.TryGetMatch(in keys[i], out var a, out _);
                smallReference.TryGetMatch(in keys[i], out var b, out _);
                if (a != b) smallMismatches++;
            }
            Console.WriteLine($"  candidate index {bits}, small table   mismatches: {smallMismatches}");
        }
        foreach (var bits in new[] { 0, 12, 16, 17, 18 })
        {
            var inherited = BuildInherited(bits);
            Check($"inherited best, index {bits}", i => inherited.TryGetMatch(in keys[i], out var p, out _) ? p : null);
            var small = BuildInherited(bits, 1_000);
            var smallReference = BuildPooled(NodeOrder.DepthFirst, 0, 1_000);
            var smallMismatches = 0;
            for (var i = 0; i < keys.Length; i++)
            {
                small.TryGetMatch(in keys[i], out var a, out _);
                smallReference.TryGetMatch(in keys[i], out var b, out _);
                if (a != b) smallMismatches++;
            }
            Console.WriteLine($"  inherited best, index {bits}, small  mismatches: {smallMismatches}");
        }

        // insertion order must not matter: build from a shuffled order and compare
        var shuffled = Enumerable.Range(0, WidthData.Prefix32.Length).OrderBy(i => (i * 2654435761u) % 1000003).ToArray();
        var shuffledTrie = new InheritedBestTrie<Bits32, IPNetwork, string>();
        foreach (var i in shuffled) shuffledTrie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        shuffledTrie.BuildIndex(16);
        Check("inherited best, shuffled inserts", i => shuffledTrie.TryGetMatch(in keys[i], out var p, out _) ? p : null);

        var batchTrie = BuildPooled(NodeOrder.DepthFirst, 16);
        CheckBatch("batch 8 lanes", batchTrie, 8, false);
        CheckBatch("batch 8 lanes, prefetch", batchTrie, 8, true);
        CheckBatch("batch 3 lanes, prefetch", batchTrie, 3, true);
        var batchNoIndex = BuildPooled(NodeOrder.DepthFirst);
        CheckBatch("batch 8 lanes, no index", batchNoIndex, 8, true);

        foreach (var bits in new[] { 0, 12, 16, 17, 18 })
        {
            var t = BuildPooled(NodeOrder.DepthFirst, bits);
            long visited = 0;
            for (var i = 0; i < keys.Length; i++) visited += t.CountVisited(in keys[i]);
            Console.WriteLine($"  index {bits,2}: average nodes visited per lookup {visited / (double)keys.Length:F2}, entries pointing at the sentinel {t.FinalEntryFraction:P0}");
        }
        foreach (var bits in new[] { 10, 12 })
        {
            var t = BuildPooled(NodeOrder.DepthFirst, bits, 1_000);
            Console.WriteLine($"  small table, index {bits}: entries pointing at the sentinel {t.FinalEntryFraction:P0}");
        }

        var conversions = 0;
        foreach (var a in Data.Addresses)
            if (FromAddressProperty(a) != IPBits.V4(a) || FromPrivateField(a) != IPBits.V4(a))
                conversions++;
        Console.WriteLine($"  address conversions disagreeing with TryWriteBytes: {conversions}");
    }

    private static CachedKeyTrie<Bits128, IPNetwork, string> Build128Cached()
    {
        var t = new CachedKeyTrie<Bits128, IPNetwork, string>();
        for (var i = 0; i < WidthData.Prefix128.Length; i++) t.TryAdd(in WidthData.Prefix128[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        return t;
    }

    private static PooledTrie<Bits128, IPNetwork, string> Build128Pooled(int bits)
    {
        var t = new PooledTrie<Bits128, IPNetwork, string>();
        for (var i = 0; i < WidthData.Prefix128.Length; i++) t.TryAdd(in WidthData.Prefix128[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        t.Relayout(NodeOrder.DepthFirst);
        if (bits > 0) t.BuildIndex(bits);
        return t;
    }

    private static PooledTrie<Bits32, IPNetwork, string> BuildNoSkip(int bits)
    {
        var t = BuildPooled(NodeOrder.DepthFirst);
        t.BuildIndex(bits, skipFinal: false);
        return t;
    }

    private static CachedKeyTrie<Bits32, IPNetwork, string> BuildSmallCached()
    {
        var t = new CachedKeyTrie<Bits32, IPNetwork, string>();
        for (var i = 0; i < 1_000; i++) t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        t.TryAdd(new Bits32(0), 0, IPNetwork.Parse("0.0.0.0/0"), string.Empty);
        return t;
    }

    // IPAddress.Address is obsolete because it only works for IPv4, which is exactly the case here
#pragma warning disable CS0618
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromAddressProperty(IPAddress address) => BinaryPrimitives.ReverseEndianness((uint)address.Address);
#pragma warning restore CS0618

    // reads IPAddress's private IPv4 field directly; depends on a runtime implementation detail
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_addressOrScopeId")]
    private static extern ref uint AddressField(IPAddress address);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromPrivateField(IPAddress address) => BinaryPrimitives.ReverseEndianness(AddressField(address));

    /// <summary>Times one variant in a fresh process: the fastest of 15 runs of 1M lookups, after warm-up.</summary>
    public static void Time(string variant)
    {
        var keys = WidthData.Key32;
        Func<int> run = variant switch
        {
            "cached" => Single(BuildCached(), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            "inline" => Single(BuildInline(), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            "pooled" => Single(BuildPooled(), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            "pooled-bfs" => Single(BuildPooled(NodeOrder.BreadthFirst), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            "cached128" => Single128(Build128Cached()),
            "pooled128-dfs" => Single128(Build128Pooled(0)),
            "pooled128-index16" => Single128(Build128Pooled(16)),
            "noskip17" => Single(BuildNoSkip(17), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            "pooled-dfs" => Single(BuildPooled(NodeOrder.DepthFirst), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            _ when variant.StartsWith("index") => Single(BuildPooled(NodeOrder.DepthFirst, int.Parse(variant[5..])), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            // small table: the first 1,000 prefixes plus a default route; small-index0 means no index
            _ when variant.StartsWith("cindex") => Single(BuildPooled(NodeOrder.DepthFirst, int.Parse(variant[6..]), candidate: true), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            _ when variant.StartsWith("small-cindex") => Single(BuildPooled(NodeOrder.DepthFirst, int.Parse(variant[12..]), 1_000, candidate: true), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            _ when variant.StartsWith("inherit") && !variant.StartsWith("inherit-small") => Single(BuildInherited(int.Parse(variant[7..])), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            _ when variant.StartsWith("inherit-small") => Single(BuildInherited(int.Parse(variant[13..]), 1_000), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            _ when variant.StartsWith("small-index") => Single(BuildPooled(NodeOrder.DepthFirst, int.Parse(variant[11..]), 1_000), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            "small-cached" => Single(BuildSmallCached(), (t, i) => t.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1),
            // end to end from IPAddress, with a 16-bit index, using different address conversions
            "e2e-trywritebytes" => EndToEnd(IPBits.V4),
            "e2e-address" => EndToEnd(FromAddressProperty),
            "e2e-accessor" => EndToEnd(FromPrivateField),
            "e2e-current" => Current(),
            _ when variant.StartsWith("batch") => Batch(variant),
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
        Console.WriteLine($"{variant,-24} best of 15: {best,7:F1} ms per 1M lookups");

        Func<int> EndToEnd(Func<IPAddress, uint> convert)
        {
            var trie = BuildPooled(NodeOrder.DepthFirst, 16);
            var addresses = Data.Addresses;
            return () =>
            {
                var sum = 0;
                foreach (var a in addresses)
                {
                    var key = new Bits32(convert(a));
                    sum += trie.TryGetMatch(in key, out var p, out var v) ? p.PrefixLength + v.Length : -1;
                }
                return sum;
            };
        }

        Func<int> Current()
        {
            var lookup = new Bitvantage.InternetProtocol.NetworkPrefixLookup<string>();
            foreach (var p in Data.Prefixes) lookup.TryAdd(p, string.Empty);
            var addresses = Data.Addresses;
            return () =>
            {
                var sum = 0;
                foreach (var a in addresses)
                    sum += lookup.TryGetMatch(a, out var r) ? r.Prefix.Length + r.Value!.Length : -1;
                return sum;
            };
        }

        Func<int> Single128(dynamic trie)
        {
            var keys128 = WidthData.Key128;
            if (trie is CachedKeyTrie<Bits128, IPNetwork, string> c)
                return () => { var sum = 0; for (var i = 0; i < keys128.Length; i++) sum += c.TryGetMatch(in keys128[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; };
            var t = (PooledTrie<Bits128, IPNetwork, string>)trie;
            return () => { var sum = 0; for (var i = 0; i < keys128.Length; i++) sum += t.TryGetMatch(in keys128[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; };
        }

        Func<int> Single<T>(T trie, Func<T, int, int> lookup) => () =>
        {
            var sum = 0;
            for (var i = 0; i < keys.Length; i++) sum += lookup(trie, i);
            return sum;
        };

        // variant name: batch<lanes>[p][-noindex], for example batch8p or batch8p-noindex
        Func<int> Batch(string name)
        {
            var noIndex = name.EndsWith("-noindex");
            var spec = noIndex ? name[5..^8] : name[5..];
            var prefetch = spec.EndsWith('p');
            var lanes = int.Parse(prefetch ? spec[..^1] : spec);
            var trie = BuildPooled(NodeOrder.DepthFirst, noIndex ? 0 : 16);
            var results = new int[keys.Length];
            return () =>
            {
                trie.GetMatches(keys, results, lanes, prefetch);
                var sum = 0;
                for (var i = 0; i < results.Length; i++)
                    sum += results[i] >= 0 ? trie.GetPrefix(results[i]).PrefixLength + trie.GetValue(results[i]).Length : -1;
                return sum;
            };
        }
    }
}
