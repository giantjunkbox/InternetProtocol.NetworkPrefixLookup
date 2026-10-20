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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Bitvantage.InternetProtocol;
using PerformanceTest;

namespace PerformanceTest.V2;

/// <summary>
///     The comparison benchmarks of PerformanceTest, against the original library in Original/ (see get-original.sh). The
///     original has no batches and no frozen form, so only the matching benchmarks exist. Run it from this folder, so that
///     BenchmarkDotNet finds this project, and choose the runtime with -f net7.0 or -f net10.0.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--memory")
        {
            Memory();
            return;
        }

        BenchmarkSwitcher.FromTypes(new[] { typeof(ComparisonBenchmarks) }).Run(args.Length == 0 ? new[] { "--filter", "*" } : args);
    }

    private static void Memory()
    {
        foreach (var name in ComparisonData.Names)
        {
            var data = ComparisonData.Get(name);
            var before = Heap();
            var sw = Stopwatch.StartNew();
            var lookup = new NetworkPrefixLookup<int>();
            for (var i = 0; i < data.Prefixes.Length; i++)
                lookup.TryAdd(data.Prefixes[i], data.Values[i]);
            var seconds = sw.Elapsed.TotalSeconds;
            var held = Heap() - before;

            var allocated = GC.GetAllocatedBytesForCurrentThread();
            foreach (var address in data.Addresses.Take(100_000))
                lookup.TryGetMatch(address, out _);
            var perLookup = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 100_000.0;
            Console.WriteLine($"{name,-10} original on .NET {Environment.Version.Major}: {held / 1048576.0,8:F1} MB held, built in {seconds:F2} s, {perLookup:F0} bytes allocated per lookup");
            GC.KeepAlive(lookup);
        }
    }

    private static long Heap()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }
}

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ComparisonBenchmarks
{
    [ParamsSource(nameof(Names))]
    public string Table = "";

    public static IEnumerable<string> Names => ComparisonData.Names;

    private ComparisonData.Table _data = null!;
    private NetworkPrefixLookup<int> _lookup = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = ComparisonData.Get(Table);
        _lookup = Build();
    }

    private NetworkPrefixLookup<int> Build()
    {
        var lookup = new NetworkPrefixLookup<int>();
        for (var i = 0; i < _data.Prefixes.Length; i++)
            lookup.TryAdd(_data.Prefixes[i], _data.Values[i]);
        return lookup;
    }

    [Benchmark]
    public long Lookup()
    {
        var lookup = _lookup;
        long sum = 0;
        foreach (var address in _data.Addresses)
            sum += lookup.TryGetMatch(address, out var match) ? match.Prefix.Length + match.Value : -1;
        return sum;
    }

    [Benchmark]
    public object Build_OneAtATime() => Build();
}
