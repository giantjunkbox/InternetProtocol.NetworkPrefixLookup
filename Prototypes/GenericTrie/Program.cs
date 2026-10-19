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
if (args.Length > 1 && args[0] == "isolate") { WidthData.Isolate(args[1]); return; }
if (args.Length > 0 && args[0] == "verify-shapes") { ResultShapeComparison.Verify(); return; }
if (args.Length > 0 && args[0] == "verify-opt") { Optimization.Verify(); return; }
if (args.Length > 1 && args[0] == "time") { Optimization.Time(args[1]); return; }
if (args.Length > 0 && args[0] == "verify-real") { RealDataChecks.Verify(); return; }
if (args.Length > 0 && args[0] == "memory") { MemoryComparison.Run(); return; }
if (args.Length > 0 && args[0] == "snapshot-costs") { RealDataChecks.SnapshotCosts(); return; }
if (args.Length > 0 && args[0] == "interference") { RealDataChecks.Interference(); return; }
if (args.Length > 0 && args[0] == "tail-latency") { RealDataChecks.TailLatency(); return; }
if (args.Length > 1 && args[0] == "time-real") { RealDataChecks.Time(args[1]); return; }
BenchmarkSwitcher.FromTypes(new[] { typeof(Comparison), typeof(WidthComparison), typeof(CachedWidthComparison), typeof(Width192Comparison), typeof(ResultShapeComparison), typeof(LargeValueShapeComparison),
    typeof(OptimizationComparison), typeof(EndToEndComparison), typeof(BuildComparison),
    typeof(RealIPv4Lookup), typeof(RealIPv6Lookup), typeof(RealFrozenLookup), typeof(RealBuild) }).Run(args);

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
    public static readonly Bits192[] Prefix192Front = PrefixBits.Select(b => new Bits192((ulong)b << 32, 0, 0)).ToArray();
    public static readonly Bits192[] Prefix192Back = PrefixBits.Select(b => new Bits192(C0, C1, (ulong)b << 32)).ToArray();

    public static readonly uint[] KeyRef = AddressBits;
    public static readonly Bits32[] Key32 = AddressBits.Select(b => new Bits32(b)).ToArray();
    public static readonly Bits128[] Key128 = AddressBits.Select(b => new Bits128((UInt128)b << 96)).ToArray();
    public static readonly Bits256[] Key256Front = AddressBits.Select(Front).ToArray();
    public static readonly Bits256[] Key256Back = AddressBits.Select(Back).ToArray();
    public static readonly BitsVar[] KeyVarBack = AddressBits.Select(VarBack).ToArray();
    public static readonly Bits192[] Key192Front = AddressBits.Select(b => new Bits192((ulong)b << 32, 0, 0)).ToArray();
    public static readonly Bits192[] Key192Back = AddressBits.Select(b => new Bits192(C0, C1, (ulong)b << 32)).ToArray();

    private static Bits256 Front(uint b) { var k = new Bits256(); k.Words[0] = (ulong)b << 32; return k; }
    private static Bits256 Back(uint b) { var k = new Bits256(); k.Words[0] = C0; k.Words[1] = C1; k.Words[2] = C2; k.Words[3] = (ulong)b << 32; return k; }
    private static BitsVar VarBack(uint b) => new(new[] { C0, C1, C2, (ulong)b << 32 });

    public static BitTrie<uint, int, string?> BuildRef() { var t = new BitTrie<uint, int, string?>(); for (var i = 0; i < PrefixRef.Length; i++) t.TryAdd(PrefixRef[i], PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<Bits32, int, string?> Build32() { var t = new KeyTrie<Bits32, int, string?>(); for (var i = 0; i < Prefix32.Length; i++) t.TryAdd(in Prefix32[i], PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<Bits128, int, string?> Build128() { var t = new KeyTrie<Bits128, int, string?>(); for (var i = 0; i < Prefix128.Length; i++) t.TryAdd(in Prefix128[i], PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<Bits256, int, string?> Build256Front() { var t = new KeyTrie<Bits256, int, string?>(); for (var i = 0; i < Prefix256Front.Length; i++) t.TryAdd(in Prefix256Front[i], PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<Bits256, int, string?> Build256Back() { var t = new KeyTrie<Bits256, int, string?>(); for (var i = 0; i < Prefix256Back.Length; i++) t.TryAdd(in Prefix256Back[i], 192 + PrefixLengths[i], i, string.Empty); return t; }
    public static CachedKeyTrie<Bits32, int, string?> BuildC32() { var t = new CachedKeyTrie<Bits32, int, string?>(); for (var i = 0; i < Prefix32.Length; i++) t.TryAdd(in Prefix32[i], PrefixLengths[i], i, string.Empty); return t; }
    public static CachedKeyTrie<Bits128, int, string?> BuildC128() { var t = new CachedKeyTrie<Bits128, int, string?>(); for (var i = 0; i < Prefix128.Length; i++) t.TryAdd(in Prefix128[i], PrefixLengths[i], i, string.Empty); return t; }
    public static CachedKeyTrie<Bits256, int, string?> BuildC256Front() { var t = new CachedKeyTrie<Bits256, int, string?>(); for (var i = 0; i < Prefix256Front.Length; i++) t.TryAdd(in Prefix256Front[i], PrefixLengths[i], i, string.Empty); return t; }
    public static CachedKeyTrie<Bits256, int, string?> BuildC256Back() { var t = new CachedKeyTrie<Bits256, int, string?>(); for (var i = 0; i < Prefix256Back.Length; i++) t.TryAdd(in Prefix256Back[i], 192 + PrefixLengths[i], i, string.Empty); return t; }
    // the 192-bit worst case puts a 128-bit context in front, so every comparison covers all three words
    public static CachedKeyTrie<Bits192, int, string?> BuildC192Front() { var t = new CachedKeyTrie<Bits192, int, string?>(); for (var i = 0; i < Prefix192Front.Length; i++) t.TryAdd(in Prefix192Front[i], PrefixLengths[i], i, string.Empty); return t; }
    public static CachedKeyTrie<Bits192, int, string?> BuildC192Back() { var t = new CachedKeyTrie<Bits192, int, string?>(); for (var i = 0; i < Prefix192Back.Length; i++) t.TryAdd(in Prefix192Back[i], 128 + PrefixLengths[i], i, string.Empty); return t; }
    public static CachedKeyTrie<BitsVar, int, string?> BuildCVarBack() { var t = new CachedKeyTrie<BitsVar, int, string?>(); for (var i = 0; i < PrefixVarBack.Length; i++) t.TryAdd(in PrefixVarBack[i], 192 + PrefixLengths[i], i, string.Empty); return t; }
    public static KeyTrie<BitsVar, int, string?> BuildVarBack() { var t = new KeyTrie<BitsVar, int, string?>(); for (var i = 0; i < PrefixVarBack.Length; i++) t.TryAdd(in PrefixVarBack[i], 192 + PrefixLengths[i], i, string.Empty); return t; }

    /// <summary>
    ///     Builds only one trie in a fresh process and times 1M lookups, so heap layout is not affected by the other
    ///     variants. Prints the fastest of 15 timed runs after warm-up.
    /// </summary>
    public static void Isolate(string variant)
    {
        Func<int> run = variant switch
        {
            "ref32" => Make(BuildRef(), KeyRef, (t, k) => t.TryGetMatch(k, out var p, out _) ? p : -1),
            "key32" => Make(Build32(), Key32, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "key128" => Make(Build128(), Key128, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "key256front" => Make(Build256Front(), Key256Front, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "key256back" => Make(Build256Back(), Key256Back, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "keyvarback" => Make(BuildVarBack(), KeyVarBack, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "cached32" => Make(BuildC32(), Key32, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "cached128" => Make(BuildC128(), Key128, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "cached256front" => Make(BuildC256Front(), Key256Front, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "cached256back" => Make(BuildC256Back(), Key256Back, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "cachedvarback" => Make(BuildCVarBack(), KeyVarBack, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "cached192front" => Make(BuildC192Front(), Key192Front, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            "cached192back" => Make(BuildC192Back(), Key192Back, (t, k) => t.TryGetMatch(in k, out var p, out _) ? p : -1),
            _ => throw new ArgumentException(variant)
        };
        for (var i = 0; i < 5; i++) run();
        var best = double.MaxValue;
        for (var i = 0; i < 15; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            run();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"{variant,-12} best of 15: {best,7:F1} ms per 1M lookups");

        static Func<int> Make<TTrie, TKey>(TTrie trie, TKey[] keys, Func<TTrie, TKey, int> lookup) => () =>
        {
            var sum = 0;
            for (var i = 0; i < keys.Length; i++) sum += lookup(trie, keys[i]);
            return sum;
        };
    }

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

        var c32 = BuildC32(); var c128 = BuildC128(); var cf256 = BuildC256Front(); var cb256 = BuildC256Back(); var cvb = BuildCVarBack();
        int cachedMismatches = 0;
        for (var i = 0; i < KeyRef.Length; i++)
        {
            r.TryGetMatch(KeyRef[i], out var expected, out _);
            c32.TryGetMatch(in Key32[i], out var a, out _);
            c128.TryGetMatch(in Key128[i], out var b, out _);
            cf256.TryGetMatch(in Key256Front[i], out var c, out _);
            cb256.TryGetMatch(in Key256Back[i], out var d, out _);
            cvb.TryGetMatch(in KeyVarBack[i], out var e, out _);
            if (a != expected || b != expected || c != expected || d != expected || e != expected)
                if (cachedMismatches++ < 5) Console.WriteLine($"  CACHED MISMATCH key {i}: ref={expected} 32={a} 128={b} 256f={c} 256b={d} var={e}");
        }
        Console.WriteLine($"cached-mask variants: lookups compared: {KeyRef.Length:N0}, mismatches: {cachedMismatches}");
        Console.WriteLine($"cached-mask retained bytes per prefix: 32={PerPrefix(Retained(BuildC32)):F0} 128={PerPrefix(Retained(BuildC128)):F0} 256={PerPrefix(Retained(BuildC256Back)):F0} var={PerPrefix(Retained(BuildCVarBack)):F0}");

        var f192 = BuildC192Front(); var b192 = BuildC192Back();
        int mismatches192 = 0;
        for (var i = 0; i < KeyRef.Length; i++)
        {
            r.TryGetMatch(KeyRef[i], out var expected, out _);
            f192.TryGetMatch(in Key192Front[i], out var a, out _);
            b192.TryGetMatch(in Key192Back[i], out var b, out _);
            if (a != expected || b != expected)
                if (mismatches192++ < 5) Console.WriteLine($"  192 MISMATCH key {i}: ref={expected} front={a} back={b}");
        }
        Console.WriteLine($"192-bit cached: lookups compared: {KeyRef.Length:N0}, mismatches: {mismatches192}, retained bytes per prefix: {PerPrefix(Retained(BuildC192Back)):F0}");

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

/// <summary>Lookups through <see cref="CachedKeyTrie{TKey,TPrefix,TValue}" />, with two references from WidthComparison.</summary>
[MemoryDiagnoser]
public class CachedWidthComparison
{
    private static readonly BitTrie<uint, int, string?> Ref = WidthData.BuildRef();
    private static readonly KeyTrie<Bits32, int, string?> K32 = WidthData.Build32();
    private static readonly CachedKeyTrie<Bits32, int, string?> C32 = WidthData.BuildC32();
    private static readonly CachedKeyTrie<Bits128, int, string?> C128 = WidthData.BuildC128();
    private static readonly CachedKeyTrie<Bits256, int, string?> C256Front = WidthData.BuildC256Front();
    private static readonly CachedKeyTrie<Bits256, int, string?> C256Back = WidthData.BuildC256Back();
    private static readonly CachedKeyTrie<BitsVar, int, string?> CVarBack = WidthData.BuildCVarBack();

    [Benchmark(Baseline = true), IterationCount(30)]
    public void Ref32_IBinaryInteger_Lookup() { var keys = WidthData.KeyRef; for (var i = 0; i < keys.Length; i++) if (!Ref.TryGetMatch(keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Key32_Computed_Lookup() { var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) if (!K32.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached32_Lookup() { var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) if (!C32.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached128_Lookup() { var keys = WidthData.Key128; for (var i = 0; i < keys.Length; i++) if (!C128.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached256Front_Lookup() { var keys = WidthData.Key256Front; for (var i = 0; i < keys.Length; i++) if (!C256Front.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached256Back_Lookup() { var keys = WidthData.Key256Back; for (var i = 0; i < keys.Length; i++) if (!C256Back.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void CachedVarBack_Lookup() { var keys = WidthData.KeyVarBack; for (var i = 0; i < keys.Length; i++) if (!CVarBack.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }
}

/// <summary>192-bit keys against the 128-bit and 256-bit key types, all with cached masks, in one session.</summary>
[MemoryDiagnoser]
public class Width192Comparison
{
    private static readonly BitTrie<uint, int, string?> Ref = WidthData.BuildRef();
    private static readonly CachedKeyTrie<Bits128, int, string?> C128 = WidthData.BuildC128();
    private static readonly CachedKeyTrie<Bits192, int, string?> C192Front = WidthData.BuildC192Front();
    private static readonly CachedKeyTrie<Bits192, int, string?> C192Back = WidthData.BuildC192Back();
    private static readonly CachedKeyTrie<Bits256, int, string?> C256Front = WidthData.BuildC256Front();
    private static readonly CachedKeyTrie<Bits256, int, string?> C256Back = WidthData.BuildC256Back();

    [Benchmark(Baseline = true), IterationCount(30)]
    public void Ref32_IBinaryInteger_Lookup() { var keys = WidthData.KeyRef; for (var i = 0; i < keys.Length; i++) if (!Ref.TryGetMatch(keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached128_Lookup() { var keys = WidthData.Key128; for (var i = 0; i < keys.Length; i++) if (!C128.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached192Front_Lookup() { var keys = WidthData.Key192Front; for (var i = 0; i < keys.Length; i++) if (!C192Front.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached192Back_Lookup() { var keys = WidthData.Key192Back; for (var i = 0; i < keys.Length; i++) if (!C192Back.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached256Front_Lookup() { var keys = WidthData.Key256Front; for (var i = 0; i < keys.Length; i++) if (!C256Front.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }

    [Benchmark, IterationCount(30)]
    public void Cached256Back_Lookup() { var keys = WidthData.Key256Back; for (var i = 0; i < keys.Length; i++) if (!C256Back.TryGetMatch(in keys[i], out _, out var v) || v != string.Empty) throw new Exception(); }
}

/// <summary>
///     Result shapes: the same lookup returning its result four ways. Prefixes are IPNetwork values and values are strings,
///     so results contain object references, as real ones would. The large table is the usual 97,000 prefixes. The small
///     table is the first 1,000 prefixes plus 0.0.0.0/0, so every key matches and the walk is short enough for the cost of
///     returning the result to show.
/// </summary>
[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class ResultShapeComparison
{
    private static readonly ResultShapeTrie<Bits32, IPNetwork, string> Large = Build(Data.Prefixes.Count);
    private static readonly ResultShapeTrie<Bits32, IPNetwork, string> Small = Build(1_000, addDefault: true);

    private static ResultShapeTrie<Bits32, IPNetwork, string> Build(int count, bool addDefault = false)
    {
        var t = new ResultShapeTrie<Bits32, IPNetwork, string>();
        for (var i = 0; i < count; i++)
            t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], string.Empty);
        if (addDefault)
            t.TryAdd(new Bits32(0), 0, IPNetwork.Parse("0.0.0.0/0"), string.Empty);
        return t;
    }

    public static void Verify()
    {
        var reference = WidthData.BuildRef();
        int mismatches = 0;
        for (var i = 0; i < WidthData.KeyRef.Length; i++)
        {
            reference.TryGetMatch(WidthData.KeyRef[i], out var index, out _);
            var expected = Data.IPNetworks[index];
            Large.TryGetMatch(in WidthData.Key32[i], out var a, out _);
            Large.TryGetMatch(in WidthData.Key32[i], out PrefixMatch<IPNetwork, string> b);
            var c = Large.GetMatch(in WidthData.Key32[i]);
            var d = Large.FindEntry(in WidthData.Key32[i]);
            if (a != expected || b.Prefix != expected || c.Prefix != expected || d?.Prefix != expected)
                if (mismatches++ < 5) Console.WriteLine($"  MISMATCH key {i}: expected={expected} a={a} b={b.Prefix} c={c.Prefix} d={d?.Prefix}");
        }
        Console.WriteLine($"result shapes: lookups compared: {WidthData.KeyRef.Length:N0}, mismatches: {mismatches}");
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Large table"), IterationCount(30)]
    public void A_OutParameters_Large() => A(Large);

    [Benchmark, BenchmarkCategory("Large table"), IterationCount(30)]
    public void B_OutStruct_Large() => B(Large);

    [Benchmark, BenchmarkCategory("Large table"), IterationCount(30)]
    public void C_ReturnedStruct_Large() => C(Large);

    [Benchmark, BenchmarkCategory("Large table"), IterationCount(30)]
    public void D_ReturnedEntry_Large() => D(Large);

    [Benchmark(Baseline = true), BenchmarkCategory("Small table"), IterationCount(30)]
    public void A_OutParameters_Small() => A(Small);

    [Benchmark, BenchmarkCategory("Small table"), IterationCount(30)]
    public void B_OutStruct_Small() => B(Small);

    [Benchmark, BenchmarkCategory("Small table"), IterationCount(30)]
    public void C_ReturnedStruct_Small() => C(Small);

    [Benchmark, BenchmarkCategory("Small table"), IterationCount(30)]
    public void D_ReturnedEntry_Small() => D(Small);

    // each consumer reads both the prefix and the value of every result
    private static void A(ResultShapeTrie<Bits32, IPNetwork, string> trie)
    {
        var keys = WidthData.Key32;
        for (var i = 0; i < keys.Length; i++)
            if (!trie.TryGetMatch(in keys[i], out var prefix, out var value) || value != string.Empty || prefix.PrefixLength < 0) throw new Exception();
    }

    private static void B(ResultShapeTrie<Bits32, IPNetwork, string> trie)
    {
        var keys = WidthData.Key32;
        for (var i = 0; i < keys.Length; i++)
            if (!trie.TryGetMatch(in keys[i], out PrefixMatch<IPNetwork, string> match) || match.Value != string.Empty || match.Prefix.PrefixLength < 0) throw new Exception();
    }

    private static void C(ResultShapeTrie<Bits32, IPNetwork, string> trie)
    {
        var keys = WidthData.Key32;
        for (var i = 0; i < keys.Length; i++)
        {
            var match = trie.GetMatch(in keys[i]);
            if (!match.Found || match.Value != string.Empty || match.Prefix.PrefixLength < 0) throw new Exception();
        }
    }

    private static void D(ResultShapeTrie<Bits32, IPNetwork, string> trie)
    {
        var keys = WidthData.Key32;
        for (var i = 0; i < keys.Length; i++)
        {
            var entry = trie.FindEntry(in keys[i]);
            if (entry == null || entry.Value != string.Empty || entry.Prefix.PrefixLength < 0) throw new Exception();
        }
    }
}

/// <summary>A 64-byte value, to see whether copying the value out of the trie costs anything.</summary>
public readonly struct Payload64
{
    public readonly long A, B, C, D, E, F, G, H;
    public Payload64(long a) { A = a; B = C = D = E = F = G = H = a; }
}

/// <summary>
///     The four result shapes on the small table, once with string values and once with 64-byte struct values, in one
///     session. Consumers read the prefix length and one field of the value.
/// </summary>
[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class LargeValueShapeComparison
{
    private static readonly ResultShapeTrie<Bits32, IPNetwork, string> SmallString = Build<string>(i => string.Empty);
    // values start at 1 so that the consumers' zero check never fires, including for the default route (index -1)
    private static readonly ResultShapeTrie<Bits32, IPNetwork, Payload64> SmallPayload = Build(i => new Payload64(i + 2));

    private static ResultShapeTrie<Bits32, IPNetwork, T> Build<T>(Func<int, T> value)
    {
        var t = new ResultShapeTrie<Bits32, IPNetwork, T>();
        for (var i = 0; i < 1_000; i++)
            t.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], Data.IPNetworks[i], value(i));
        t.TryAdd(new Bits32(0), 0, IPNetwork.Parse("0.0.0.0/0"), value(-1));
        return t;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("string value"), IterationCount(30)]
    public void A_OutParameters_String() { var t = SmallString; var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) if (!t.TryGetMatch(in keys[i], out var p, out var v) || v != string.Empty || p.PrefixLength < 0) throw new Exception(); }

    [Benchmark, BenchmarkCategory("string value"), IterationCount(30)]
    public void B_OutStruct_String() { var t = SmallString; var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) if (!t.TryGetMatch(in keys[i], out PrefixMatch<IPNetwork, string> m) || m.Value != string.Empty || m.Prefix.PrefixLength < 0) throw new Exception(); }

    [Benchmark, BenchmarkCategory("string value"), IterationCount(30)]
    public void C_ReturnedStruct_String() { var t = SmallString; var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) { var m = t.GetMatch(in keys[i]); if (!m.Found || m.Value != string.Empty || m.Prefix.PrefixLength < 0) throw new Exception(); } }

    [Benchmark, BenchmarkCategory("string value"), IterationCount(30)]
    public void D_ReturnedEntry_String() { var t = SmallString; var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) { var e = t.FindEntry(in keys[i]); if (e == null || e.Value != string.Empty || e.Prefix.PrefixLength < 0) throw new Exception(); } }

    [Benchmark(Baseline = true), BenchmarkCategory("64-byte value"), IterationCount(30)]
    public void A_OutParameters_Payload() { var t = SmallPayload; var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) if (!t.TryGetMatch(in keys[i], out var p, out var v) || v.A == 0 || p.PrefixLength < 0) throw new Exception(); }

    [Benchmark, BenchmarkCategory("64-byte value"), IterationCount(30)]
    public void B_OutStruct_Payload() { var t = SmallPayload; var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) if (!t.TryGetMatch(in keys[i], out PrefixMatch<IPNetwork, Payload64> m) || m.Value.A == 0 || m.Prefix.PrefixLength < 0) throw new Exception(); }

    [Benchmark, BenchmarkCategory("64-byte value"), IterationCount(30)]
    public void C_ReturnedStruct_Payload() { var t = SmallPayload; var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) { var m = t.GetMatch(in keys[i]); if (!m.Found || m.Value.A == 0 || m.Prefix.PrefixLength < 0) throw new Exception(); } }

    [Benchmark, BenchmarkCategory("64-byte value"), IterationCount(30)]
    public void D_ReturnedEntry_Payload() { var t = SmallPayload; var keys = WidthData.Key32; for (var i = 0; i < keys.Length; i++) { var e = t.FindEntry(in keys[i]); if (e == null || e.Value.A == 0 || e.Prefix.PrefixLength < 0) throw new Exception(); } }
}
