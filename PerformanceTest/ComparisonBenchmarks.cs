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
using Bitvantage.InternetProtocol.NetworkPrefixLookup;

namespace PerformanceTest;

/// <summary>
///     Version 3 on the tables of <see cref="ComparisonData" />. PerformanceTest.V2 runs the same benchmarks, with the
///     same names, against version 2.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ComparisonBenchmarks
{
    [ParamsSource(nameof(Names))]
    public string Table = "";

    public static IEnumerable<string> Names => ComparisonData.Names;

    private ComparisonData.Table _data = null!;
    private IPNetwork[] _networks = null!;
    private IPNetworkLookup<int> _lookup = null!;
    private FrozenIPNetworkLookup<int> _frozen = null!;

    /// <summary>A table's prefixes as <see cref="IPNetwork" /> values; version 2 makes its own NetworkPrefix objects.</summary>
    public static IPNetwork[] Networks(ComparisonData.Table data) => data.Prefixes.Select(p => new IPNetwork(p.Address, p.Length)).ToArray();

    [GlobalSetup]
    public void Setup()
    {
        _data = ComparisonData.Get(Table);
        _networks = Networks(_data);
        _lookup = Build(batch: true);
        _frozen = _lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.LeafPushed });
    }

    private IPNetworkLookup<int> Build(bool batch)
    {
        var lookup = new IPNetworkLookup<int>();
        var b = batch ? lookup.BeginBatch() : null;
        for (var i = 0; i < _networks.Length; i++)
            lookup.TryAdd(_networks[i], _data.Values[i]);
        b?.Commit();
        return lookup;
    }

    [Benchmark]
    public long Lookup()
    {
        var lookup = _lookup;
        long sum = 0;
        foreach (var address in _data.Addresses)
            sum += lookup.TryGetMatch(address, out var prefix, out var value) ? prefix.PrefixLength + value : -1;
        return sum;
    }

    [Benchmark]
    public long Lookup_Frozen()
    {
        var lookup = _frozen;
        long sum = 0;
        foreach (var address in _data.Addresses)
            sum += lookup.TryGetMatch(address, out var prefix, out var value) ? prefix.PrefixLength + value : -1;
        return sum;
    }

    [Benchmark]
    public object Build_OneAtATime() => Build(batch: false);

    [Benchmark]
    public object Build_Batch() => Build(batch: true);
}
