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
BenchmarkRunner.Run<Comparison>(args: args);

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
