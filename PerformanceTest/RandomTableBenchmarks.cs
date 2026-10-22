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
using Bitvantage.InternetProtocol;

namespace PerformanceTest;

/// <summary>
///     The benchmarks of version 2, on the same data: 100,000 random IPv4 prefixes and 1,000,000 random addresses, made
///     with the same generators and seeds, so the numbers compare with the v2 baseline in Documentation/Architecture.md.
/// </summary>
[MemoryDiagnoser]
public class RandomTableBenchmarks
{
    private static readonly List<NetworkPrefix> Networks = CreateNetworks();
    private static readonly List<IPAddress> Addresses = CreateAddresses();
    private static readonly uint[] AddressBits = Addresses.Select(IPAddressBits.ToIPv4Bits).ToArray();
    private static readonly NetworkPrefixLookup<string> Lookup = new(Networks.Select(n => KeyValuePair.Create(n, string.Empty)));
    private static readonly FrozenNetworkPrefixLookup<string> Frozen = Lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.LeafPushed });
    private static readonly FrozenNetworkPrefixLookup<string> FrozenCompressed = Lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });

    // v2: 100,000 prefixes taken from the random sequence, then made distinct
    public static List<NetworkPrefix> CreateNetworks() => RandomPrefixes(11).Take(100_000).Distinct().ToList();

    public static List<IPAddress> CreateAddresses() => RandomAddresses(12).Take(1_000_000).ToList();

    /// <summary>v2's IpNetworkLookupX1M: one million lookups by IPAddress.</summary>
    [Benchmark(Baseline = true), IterationCount(30)]
    public int Lookup_IPAddress_1M()
    {
        var found = 0;
        foreach (var address in Addresses)
            if (Lookup.TryGetMatch(address, out _, out _)) found++;
        return found;
    }

    [Benchmark, IterationCount(30)]
    public int Lookup_IPAddress_1M_Frozen()
    {
        var found = 0;
        foreach (var address in Addresses)
            if (Frozen.TryGetMatch(address, out _, out _)) found++;
        return found;
    }

    // scattered long prefixes make the leaf-pushed tables large: 105 MB here, against 8 MB compressed
    [Benchmark, IterationCount(30)]
    public int Lookup_IPAddress_1M_FrozenCompressed()
    {
        var found = 0;
        foreach (var address in Addresses)
            if (FrozenCompressed.TryGetMatch(address, out _, out _)) found++;
        return found;
    }

    [Benchmark, IterationCount(30)]
    public int Lookup_UInt32_1M()
    {
        var found = 0;
        foreach (var address in AddressBits)
            if (Lookup.TryGetMatchIPv4(address, out _, out _)) found++;
        return found;
    }

    [Benchmark, IterationCount(30)]
    public int Lookup_UInt32_1M_Frozen()
    {
        var found = 0;
        foreach (var address in AddressBits)
            if (Frozen.TryGetMatchIPv4(address, out _, out _)) found++;
        return found;
    }

    /// <summary>v2's AddX100K: one write at a time.</summary>
    [Benchmark, IterationCount(30)]
    public object Add_100K()
    {
        var lookup = new NetworkPrefixLookup<string?>();
        foreach (var network in Networks)
            lookup.TryAdd(network, default);
        return lookup;
    }

    [Benchmark, IterationCount(30)]
    public object Add_100K_Batch()
    {
        var lookup = new NetworkPrefixLookup<string?>();
        using var batch = lookup.BeginBatch();
        foreach (var network in Networks)
            lookup.TryAdd(network, default);
        batch.Commit();
        return lookup;
    }

    [Benchmark, IterationCount(30)]
    public object Freeze_100K() => Lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.LeafPushed });

    private static IEnumerable<NetworkPrefix> RandomPrefixes(int seed)
    {
        var random = new Random(seed);
        while (true)
        {
            var addressBytes = BitConverter.GetBytes(random.NextInt64(0, 0xff_ff_ff_ffL + 1)).Take(4).ToArray();
            var length = (ushort)Math.Cbrt(random.Next(0, 33 * 33 * 33));
            yield return new NetworkPrefix(new IPAddress(addressBytes), length);
        }
    }

    private static IEnumerable<IPAddress> RandomAddresses(int seed)
    {
        var random = new Random(seed);
        while (true)
            yield return new IPAddress(BitConverter.GetBytes(random.NextInt64(0, 0xff_ff_ff_ffL + 1)).Take(4).ToArray());
    }
}
