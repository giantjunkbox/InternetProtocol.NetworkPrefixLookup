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

namespace GenericTrie;

/// <summary>Lookup speed of each optimization step, 1,000,000 precomputed 32-bit keys against 97,000 prefixes.</summary>
[MemoryDiagnoser]
public class OptimizationComparison
{
    private static readonly CachedKeyTrie<Bits32, IPNetwork, string> Cached = Optimization.BuildCached();
    private static readonly InlineChildTrie<Bits32, IPNetwork, string> Inline = Optimization.BuildInline();
    private static readonly PooledTrie<Bits32, IPNetwork, string> Pooled = Optimization.BuildPooled();
    private static readonly PooledTrie<Bits32, IPNetwork, string> PooledBfs = Optimization.BuildPooled(NodeOrder.BreadthFirst);
    private static readonly PooledTrie<Bits32, IPNetwork, string> PooledDfs = Optimization.BuildPooled(NodeOrder.DepthFirst);
    private static readonly PooledTrie<Bits32, IPNetwork, string> Index12 = Optimization.BuildPooled(NodeOrder.DepthFirst, 12);
    private static readonly PooledTrie<Bits32, IPNetwork, string> Index16 = Optimization.BuildPooled(NodeOrder.DepthFirst, 16);
    private static readonly PooledTrie<Bits32, IPNetwork, string> Index17 = Optimization.BuildPooled(NodeOrder.DepthFirst, 17);

    [Benchmark(Baseline = true), IterationCount(30)]
    public int A_Baseline_CachedKeyTrie() { var keys = WidthData.Key32; var sum = 0; for (var i = 0; i < keys.Length; i++) sum += Cached.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; }

    [Benchmark, IterationCount(30)]
    public int B_InlineChildren() { var keys = WidthData.Key32; var sum = 0; for (var i = 0; i < keys.Length; i++) sum += Inline.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; }

    [Benchmark, IterationCount(30)]
    public int C_NodePool() { var keys = WidthData.Key32; var sum = 0; for (var i = 0; i < keys.Length; i++) sum += Pooled.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; }

    [Benchmark, IterationCount(30)]
    public int D_NodePool_BreadthFirst() { var keys = WidthData.Key32; var sum = 0; for (var i = 0; i < keys.Length; i++) sum += PooledBfs.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; }

    [Benchmark, IterationCount(30)]
    public int E_NodePool_DepthFirst() { var keys = WidthData.Key32; var sum = 0; for (var i = 0; i < keys.Length; i++) sum += PooledDfs.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; }

    [Benchmark, IterationCount(30)]
    public int F_DepthFirst_Index12() { var keys = WidthData.Key32; var sum = 0; for (var i = 0; i < keys.Length; i++) sum += Index12.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; }

    [Benchmark, IterationCount(30)]
    public int G_DepthFirst_Index16() { var keys = WidthData.Key32; var sum = 0; for (var i = 0; i < keys.Length; i++) sum += Index16.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; }

    [Benchmark, IterationCount(30)]
    public int H_DepthFirst_Index17() { var keys = WidthData.Key32; var sum = 0; for (var i = 0; i < keys.Length; i++) sum += Index17.TryGetMatch(in keys[i], out var p, out var v) ? p.PrefixLength + v.Length : -1; return sum; }
}

/// <summary>End to end from IPAddress objects: the current library against the optimized design.</summary>
[MemoryDiagnoser]
public class EndToEndComparison
{
    private static readonly Bitvantage.InternetProtocol.NetworkPrefixLookup<string> Current = BuildCurrent();
    private static readonly PooledTrie<Bits32, IPNetwork, string> Optimized = Optimization.BuildPooled(NodeOrder.DepthFirst, 17);

    private static Bitvantage.InternetProtocol.NetworkPrefixLookup<string> BuildCurrent()
    {
        var lookup = new Bitvantage.InternetProtocol.NetworkPrefixLookup<string>();
        foreach (var p in Data.Prefixes) lookup.TryAdd(p, string.Empty);
        return lookup;
    }

    [Benchmark(Baseline = true), IterationCount(30)]
    public int CurrentLibrary() { var sum = 0; foreach (var a in Data.Addresses) sum += Current.TryGetMatch(a, out var r) ? r.Prefix.Length + r.Value!.Length : -1; return sum; }

    [Benchmark, IterationCount(30)]
    public int Optimized_TryWriteBytes() { var sum = 0; foreach (var a in Data.Addresses) { var key = new Bits32(IPBits.V4(a)); sum += Optimized.TryGetMatch(in key, out var p, out var v) ? p.PrefixLength + v.Length : -1; } return sum; }

    [Benchmark, IterationCount(30)]
    public int Optimized_AddressProperty() { var sum = 0; foreach (var a in Data.Addresses) { var key = new Bits32(Optimization.FromAddressProperty(a)); sum += Optimized.TryGetMatch(in key, out var p, out var v) ? p.PrefixLength + v.Length : -1; } return sum; }

    [Benchmark, IterationCount(30)]
    public int Optimized_PrivateField() { var sum = 0; foreach (var a in Data.Addresses) { var key = new Bits32(Optimization.FromPrivateField(a)); sum += Optimized.TryGetMatch(in key, out var p, out var v) ? p.PrefixLength + v.Length : -1; } return sum; }
}

/// <summary>Building a table from 97,000 prefixes.</summary>
[MemoryDiagnoser]
public class BuildComparison
{
    [Benchmark(Baseline = true), IterationCount(30)]
    public object CurrentLibrary() { var lookup = new Bitvantage.InternetProtocol.NetworkPrefixLookup<string>(); foreach (var p in Data.Prefixes) lookup.TryAdd(p, string.Empty); return lookup; }

    [Benchmark, IterationCount(30)]
    public object CachedKeyTrie() => Optimization.BuildCached();

    [Benchmark, IterationCount(30)]
    public object NodePool() => Optimization.BuildPooled();

    [Benchmark, IterationCount(30)]
    public object NodePool_DepthFirst() => Optimization.BuildPooled(NodeOrder.DepthFirst);

    [Benchmark, IterationCount(30)]
    public object NodePool_DepthFirst_Index17() => Optimization.BuildPooled(NodeOrder.DepthFirst, 17);
}
