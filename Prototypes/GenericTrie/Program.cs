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
using BenchmarkDotNet.Running;
using Bitvantage.InternetProtocol;
using GenericTrie;

if (args.Length > 0 && args[0] == "verify") { Verify.Run(); return; }
if (args.Length > 0 && args[0] == "verify-width") { WidthData.Verify(); return; }
BenchmarkSwitcher.FromTypes(new[] { typeof(Comparison), typeof(WidthComparison) }).Run(args);

public static class Data
{
    // identical generators and seeds to PerformanceTest/Program.cs
    public static readonly List<NetworkPrefix> Prefixes = Gen(11).Take(100_000).Distinct().ToList();
    public static readonly List<IPNetwork> IPNetworks = Prefixes.Select(p => new IPNetwork(p.Address, p.Length)).ToList();
    public static readonly List<IPAddress> Addresses = Addrs(12).Take(1_000_000).ToList();

    private static IEnumerable<NetworkPrefix> Gen(int seed) { var r = new Random(seed); while (true) { var b = BitConverter.GetBytes(r.NextInt64(0, 0xff_ff_ff_ffL + 1)).Take(4).ToArray(); yield return new NetworkPrefix(new IPAddress(b), (ushort)Math.Cbrt(r.Next(0, 33 * 33 * 33))); } }
    private static IEnumerable<IPAddress> Addrs(int seed) { var r = new Random(seed); while (true) { var b = BitConverter.GetBytes(r.NextInt64(0, 0xff_ff_ff_ffL + 1)).Take(4).ToArray(); yield return new IPAddress(b); } }
}

public static class Verify
{
    public static void Run()
    {
        long Retained(Func<object> build) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var before = GC.GetTotalMemory(true); var o = build(); GC.Collect(); var after = GC.GetTotalMemory(true); GC.KeepAlive(o); return after - before; }
        var n = Data.Prefixes.Count;

        var current = new NetworkPrefixLookup<string>(); foreach (var p in Data.Prefixes) current.TryAdd(p, "");
        var control = new UInt128Control<string>(); foreach (var p in Data.Prefixes) control.TryAdd(p, "");
        var genNp = new IPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, string>(); foreach (var p in Data.Prefixes) genNp.TryAdd(p, "");
        var genIp = new IPPrefixLookup<IPNetwork, IPNetworkAdapter, string>(); foreach (var p in Data.IPNetworks) genIp.TryAdd(p, "");

        Console.WriteLine($"prefixes: {n:N0}");
        Console.WriteLine($"node counts (value/routing incl. root): current=96,944/75,986 (measured earlier)  control={control.V4.CountNodes()}  generic-NetworkPrefix={genNp.V4.CountNodes()}  generic-IPNetwork={genIp.V4.CountNodes()}");
        Console.WriteLine($"Count property: current={current.Count:N0} (known undercount)  generic={genNp.V4.Count:N0}");

        int mismatches = 0, matched = 0;
        foreach (var a in Data.Addresses)
        {
            var hasCur = current.TryGetMatch(a, out var cur);
            var hasCtl = control.TryGetMatch(a, out var ctl, out _);
            var hasNp = genNp.TryGetMatch(a, out var np, out _);
            var hasIp = genIp.TryGetMatch(a, out var ip, out _);
            if (hasCur) matched++;
            var curText = hasCur ? cur!.Prefix.ToString() : "-";
            if ((hasCtl ? ctl.ToString() : "-") != curText || (hasNp ? np.ToString() : "-") != curText || (hasIp ? ip.ToString() : "-") != curText)
                if (mismatches++ < 5) Console.WriteLine($"  MISMATCH {a}: current={curText} control={ctl} np={np} ip={ip}");
        }
        Console.WriteLine($"lookups compared: {Data.Addresses.Count:N0}, matched: {matched:N0}, mismatches: {mismatches}");

        Console.WriteLine($"retained bytes per prefix: current={Retained(() => { var l = new NetworkPrefixLookup<string>(); foreach (var p in Data.Prefixes) l.TryAdd(p, ""); return l; }) / (double)n:F0}" +
                          $"  control={Retained(() => { var l = new UInt128Control<string>(); foreach (var p in Data.Prefixes) l.TryAdd(p, ""); return l; }) / (double)n:F0}" +
                          $"  generic-NetworkPrefix={Retained(() => { var l = new IPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, string>(); foreach (var p in Data.Prefixes) l.TryAdd(p, ""); return l; }) / (double)n:F0}" +
                          $"  generic-IPNetwork={Retained(() => { var l = new IPPrefixLookup<IPNetwork, IPNetworkAdapter, string>(); foreach (var p in Data.IPNetworks) l.TryAdd(p, ""); return l; }) / (double)n:F0}");
        Console.WriteLine("  (excludes the NetworkPrefix and IPNetwork key objects, which exist before the build)");

        // IPv6 smoke test through the generic layer
        var v6 = new IPPrefixLookup<IPNetwork, IPNetworkAdapter, string>();
        v6.TryAdd(IPNetwork.Parse("2001:db8::/32"), "a"); v6.TryAdd(IPNetwork.Parse("2001:db8:1::/48"), "b"); v6.TryAdd(IPNetwork.Parse("::/0"), "default");
        foreach (var s in new[] { "2001:db8:1::5", "2001:db8:2::5", "fe80::1" })
            Console.WriteLine($"  IPv6 {s} => {(v6.TryGetMatch(IPAddress.Parse(s), out var pfx, out var val) ? $"{pfx} {val}" : "none")}");
    }
}

[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class Comparison
{
    private static readonly NetworkPrefixLookup<string> Current = new();
    private static readonly UInt128Control<string> Control = new();
    private static readonly IPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, string> GenericNp = new();
    private static readonly IPPrefixLookup<IPNetwork, IPNetworkAdapter, string> GenericIp = new();

    static Comparison()
    {
        foreach (var p in Data.Prefixes) { Current.TryAdd(p, string.Empty); Control.TryAdd(p, string.Empty); GenericNp.TryAdd(p, string.Empty); }
        foreach (var p in Data.IPNetworks) GenericIp.TryAdd(p, string.Empty);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void Current_Lookup() { foreach (var a in Data.Addresses) if (Current.GetMatch(a).Value != string.Empty) throw new Exception(); }

    [Benchmark, BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void Control128_Lookup() { foreach (var a in Data.Addresses) if (!Control.TryGetMatch(a, out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void GenericNetworkPrefix_Lookup() { foreach (var a in Data.Addresses) if (!GenericNp.TryGetMatch(a, out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void GenericIPNetwork_Lookup() { foreach (var a in Data.Addresses) if (!GenericIp.TryGetMatch(a, out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark(Baseline = true), BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object Current_Build() { var l = new NetworkPrefixLookup<string?>(); foreach (var p in Data.Prefixes) l.TryAdd(p, default); return l; }

    [Benchmark, BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object Control128_Build() { var l = new UInt128Control<string?>(); foreach (var p in Data.Prefixes) l.TryAdd(p, default); return l; }

    [Benchmark, BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object GenericNetworkPrefix_Build() { var l = new IPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, string?>(); foreach (var p in Data.Prefixes) l.TryAdd(p, default); return l; }

    [Benchmark, BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object GenericIPNetwork_Build() { var l = new IPPrefixLookup<IPNetwork, IPNetworkAdapter, string?>(); foreach (var p in Data.IPNetworks) l.TryAdd(p, default); return l; }
}

/// <summary>
///     Key width study. The same IPv4 prefixes and addresses are encoded as keys of different widths, so every trie has
///     the same shape and only the key representation differs. Keys are precomputed, so the timings measure the trie
///     walk alone, without IPAddress conversion.
/// </summary>
public static class WidthData
{
    // a fixed 192-bit context placed in front of the IPv4 bits for the worst-case 256-bit layout
    private const ulong C0 = 0x0123456789ABCDEF, C1 = 0xFEDCBA9876543210, C2 = 0x0F1E2D3C4B5A6978;

    public static readonly int[] PrefixLengths = Data.Prefixes.Select(p => p.Length).ToArray();
    private static readonly uint[] PrefixBits = Data.Prefixes.Select(p => (uint)p.NetworkBits).ToArray();
    private static readonly uint[] AddressBits = Data.Addresses.Select(IPBits.V4).ToArray();

    public static readonly uint[] PrefixRef = PrefixBits;
    public static readonly Bits32[] Prefix32 = PrefixBits.Select(b => new Bits32(b)).ToArray();
    public static readonly Bits128[] Prefix128 = PrefixBits.Select(b => new Bits128((UInt128)b << 96)).ToArray();
    public static readonly Bits256[] Prefix256Front = PrefixBits.Select(Front).ToArray();
    public static readonly Bits256[] Prefix256Back = PrefixBits.Select(Back).ToArray();
    public static readonly BitsVar[] PrefixVarBack = PrefixBits.Select(VarBack).ToArray();

    public static readonly uint[] KeyRef = AddressBits;
    public static readonly Bits32[] Key32 = AddressBits.Select(b => new Bits32(b)).ToArray();
    public static readonly Bits128[] Key128 = AddressBits.Select(b => new Bits128((UInt128)b << 96)).ToArray();
    public static readonly Bits256[] Key256Front = AddressBits.Select(Front).ToArray();
    public static readonly Bits256[] Key256Back = AddressBits.Select(Back).ToArray();
    public static readonly BitsVar[] KeyVarBack = AddressBits.Select(VarBack).ToArray();

    private static Bits256 Front(uint b) { var k = new Bits256(); k.Words[0] = (ulong)b << 32; return k; }
    private static Bits256 Back(uint b) { var k = new Bits256(); k.Words[0] = C0; k.Words[1] = C1; k.Words[2] = C2; k.Words[3] = (ulong)b << 32; return k; }
    private static BitsVar VarBack(uint b) => new(new[] { C0, C1, C2, (ulong)b << 32 });

    public static BitTrie<uint, int, string?> BuildRef() { var t = new BitTrie<uint, int, string?>(); for (var i = 0; i < PrefixRef.Length; i++) t.TryAdd(PrefixRef[i], PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<Bits32, int, string?> Build32() { var t = new KeyTrie<Bits32, int, string?>(); for (var i = 0; i < Prefix32.Length; i++) t.TryAdd(in Prefix32[i], PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<Bits128, int, string?> Build128() { var t = new KeyTrie<Bits128, int, string?>(); for (var i = 0; i < Prefix128.Length; i++) t.TryAdd(in Prefix128[i], PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<Bits256, int, string?> Build256Front() { var t = new KeyTrie<Bits256, int, string?>(); for (var i = 0; i < Prefix256Front.Length; i++) t.TryAdd(in Prefix256Front[i], PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<Bits256, int, string?> Build256Back() { var t = new KeyTrie<Bits256, int, string?>(); for (var i = 0; i < Prefix256Back.Length; i++) t.TryAdd(in Prefix256Back[i], 192 + PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<BitsVar, int, string?> BuildVarBack() { var t = new KeyTrie<BitsVar, int, string?>(); for (var i = 0; i < PrefixVarBack.Length; i++) t.TryAdd(in PrefixVarBack[i], 192 + PrefixLengths[i], i, string.Empty); return t; }

    public static void Verify()
    {
        long Retained(Func<object> build) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var before = GC.GetTotalMemory(true); var o = build(); GC.Collect(); var after = GC.GetTotalMemory(true); GC.KeepAlive(o); return after - before; }
        double PerPrefix(long bytes) => bytes / (double)PrefixLengths.Length;

        var r = BuildRef(); var k32 = Build32(); var k128 = Build128(); var f256 = Build256Front(); var b256 = Build256Back(); var vb = BuildVarBack();
        Console.WriteLine($"nodes (value, routing incl. root): ref={r.CountNodes()} 32={k32.CountNodes()} 128={k128.CountNodes()} 256front={f256.CountNodes()} 256back={b256.CountNodes()} varBack={vb.CountNodes()}");

        int mismatches = 0;
        for (var i = 0; i < KeyRef.Length; i++)
        {
            r.TryGetMatch(KeyRef[i], out var expected, out _);
            k32.TryGetMatch(in Key32[i], out var a, out _);
            k128.TryGetMatch(in Key128[i], out var b, out _);
            f256.TryGetMatch(in Key256Front[i], out var c, out _);
            b256.TryGetMatch(in Key256Back[i], out var d, out _);
            vb.TryGetMatch(in KeyVarBack[i], out var e, out _);
            if (a != expected || b != expected || c != expected || d != expected || e != expected)
                if (mismatches++ < 5) Console.WriteLine($"  MISMATCH key {i}: ref={expected} 32={a} 128={b} 256f={c} 256b={d} var={e}");
        }
        Console.WriteLine($"lookups compared: {KeyRef.Length:N0}, mismatches: {mismatches}");

        Console.WriteLine($"retained bytes per prefix: ref={PerPrefix(Retained(BuildRef)):F0} 32={PerPrefix(Retained(Build32)):F0} 128={PerPrefix(Retained(Build128)):F0} " +
                          $"256front={PerPrefix(Retained(Build256Front)):F0} 256back={PerPrefix(Retained(Build256Back)):F0} varBack={PerPrefix(Retained(BuildVarBack)):F0}");
    }
}

[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class WidthComparison
{
    private static readonly BitTrie<uint, int, string?> Ref = WidthData.BuildRef();
    private static readonly KeyTrie<Bits32, int, string?> K32 = WidthData.Build32();
    private static readonly KeyTrie<Bits128, int, string?> K128 = WidthData.Build128();
    private static readonly KeyTrie<Bits256, int, string?> K256Front = WidthData.Build256Front();
    private static readonly KeyTrie<Bits256, int, string?> K256Back = WidthData.Build256Back();
    private static readonly KeyTrie<BitsVar, int, string?> KVarBack = WidthData.BuildVarBack();

    [Benchmark(Baseline = true), BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void Ref32_IBinaryInteger_Lookup() { var keys = WidthData.KeyRef; for (var i = 0; i < keys.Length; i++) if (!Ref.TryGetMatch(keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void Key32_Lookup() { var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) if (!K32.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void Key128_Lookup() { var keys = WidthData.Key128; for (var i = 0; i < keys.Length; i++) if (!K128.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void Key256Front_Lookup() { var keys = WidthData.Key256Front; for (var i = 0; i < keys.Length; i++) if (!K256Front.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void Key256Back_Lookup() { var keys = WidthData.Key256Back; for (var i = 0; i < keys.Length; i++) if (!K256Back.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, BenchmarkCategory("Lookup 1M"), IterationCount(30)]
    public void KeyVarBack_Lookup() { var keys = WidthData.KeyVarBack; for (var i = 0; i < keys.Length; i++) if (!KVarBack.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark(Baseline = true), BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object Ref32_IBinaryInteger_Build() => WidthData.BuildRef();

    [Benchmark, BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object Key32_Build() => WidthData.Build32();

    [Benchmark, BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object Key128_Build() => WidthData.Build128();

    [Benchmark, BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object Key256Front_Build() => WidthData.Build256Front();

    [Benchmark, BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object Key256Back_Build() => WidthData.Build256Back();

    [Benchmark, BenchmarkCategory("Build 97K"), IterationCount(30)]
    public object KeyVarBack_Build() => WidthData.BuildVarBack();
}
