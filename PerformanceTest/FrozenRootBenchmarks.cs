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

using BenchmarkDotNet.Attributes;
using Bitvantage.InternetProtocol;

namespace PerformanceTest;

/// <summary>Lookups in small frozen tables: the layouts' full roots against the root sized from the number of prefixes.</summary>
public class FrozenRootBenchmarks
{
    [Params(64, 256, 1000, 4000)]
    public int Prefixes;

    private FrozenNetworkPrefixLookup<string> _fullRoot = null!;
    private FrozenNetworkPrefixLookup<string> _sizedRoot = null!;
    private FrozenNetworkPrefixLookup<string> _compressedFullRoot = null!;
    private FrozenNetworkPrefixLookup<string> _compressedSizedRoot = null!;
    private uint[] _keys = null!;

    [GlobalSetup]
    public void Setup()
    {
        var lookup = new NetworkPrefixLookup<string>(RandomTableBenchmarks.CreateNetworks().Take(Prefixes).Select(n => KeyValuePair.Create(n, string.Empty)));
        _fullRoot = lookup.Freeze(new FrozenBitTrieOptions { RootBits = 16 });
        _sizedRoot = lookup.Freeze();
        _compressedFullRoot = lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed, RootBits = 18 });
        _compressedSizedRoot = lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });
        _keys = RandomTableBenchmarks.CreateAddresses().Select(IPAddressBits.ToIPv4Bits).ToArray();
        Console.WriteLine($"// {Prefixes} prefixes: leaf-pushed {_fullRoot.Bytes:N0} bytes full, {_sizedRoot.Bytes:N0} sized; " +
                          $"compressed {_compressedFullRoot.Bytes:N0} full, {_compressedSizedRoot.Bytes:N0} sized");
    }

    private static int Count(FrozenNetworkPrefixLookup<string> lookup, uint[] keys)
    {
        var found = 0;
        foreach (var key in keys)
            if (lookup.TryGetMatchValueIPv4(key, out _)) found++;
        return found;
    }

    [Benchmark(Baseline = true)]
    public int LeafPushed_FullRoot() => Count(_fullRoot, _keys);

    [Benchmark]
    public int LeafPushed_SizedRoot() => Count(_sizedRoot, _keys);

    [Benchmark]
    public int Compressed_FullRoot() => Count(_compressedFullRoot, _keys);

    [Benchmark]
    public int Compressed_SizedRoot() => Count(_compressedSizedRoot, _keys);
}
