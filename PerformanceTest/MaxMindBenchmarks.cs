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
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using Bitvantage.InternetProtocol;
using MaxMind.Db;

namespace PerformanceTest;

/// <summary>
///     The MaxMind loader against MaxMind's own reader, on the GeoIP2 Country test database in Test/Data/MaxMind. That
///     database is small and fits in cache, so these numbers show the cost per lookup of each approach, not what a full
///     GeoIP2 database would give.
/// </summary>
[MemoryDiagnoser]
public class MaxMindBenchmarks
{
    private static readonly string DatabasePath = FindDatabase();
    private static readonly Reader Reader = new(DatabasePath, FileAccessMode.Memory);
    private static readonly MaxMindNetworkLookup<Dictionary<string, object>> Lookup = MaxMindDatabase.Load<Dictionary<string, object>>(Reader);
    private static readonly FrozenMaxMindNetworkLookup<Dictionary<string, object>> Frozen = Lookup.Freeze();
    private static readonly IPAddress[] Addresses = CreateAddresses();

    private static string FindDatabase()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "Test", "Data", "MaxMind", "GeoIP2-Country-Test.mmdb");
                if (File.Exists(path)) return path;
            }

        throw new FileNotFoundException("Test/Data/MaxMind/GeoIP2-Country-Test.mmdb was not found above the working directory.");
    }

    // 100,000 addresses: a random network of the database, then random bits for the rest of the address
    private static IPAddress[] CreateAddresses()
    {
        var networks = Lookup.Select(e => e.Prefix).ToArray();
        var random = new Random(5);
        var addresses = new IPAddress[100_000];
        for (var i = 0; i < addresses.Length; i++)
        {
            var network = networks[random.Next(networks.Length)];
            if (network.NetworkAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                var host = (uint)random.NextInt64(0, 1L << 32);
                var mask = network.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - network.PrefixLength);
                addresses[i] = IPAddressBits.FromIPv4Bits((IPAddressBits.ToIPv4Bits(network.NetworkAddress) & mask) | (host & ~mask));
            }
            else
            {
                var bytes = new byte[16];
                random.NextBytes(bytes);
                var host = new UInt128(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
                var mask = network.PrefixLength == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - network.PrefixLength);
                addresses[i] = IPAddressBits.FromIPv6Bits((IPAddressBits.ToIPv6Bits(network.NetworkAddress) & mask) | (host & ~mask));
            }
        }

        return addresses;
    }

    [Benchmark(Baseline = true)]
    public int Reader_Find()
    {
        var found = 0;
        foreach (var address in Addresses)
            if (Reader.Find<Dictionary<string, object>>(address) != null) found++;
        return found;
    }

    [Benchmark]
    public int Lookup_ReadWrite()
    {
        var found = 0;
        foreach (var address in Addresses)
            if (Lookup.TryGetMatchValue(address, out _)) found++;
        return found;
    }

    [Benchmark]
    public int Lookup_Frozen()
    {
        var found = 0;
        foreach (var address in Addresses)
            if (Frozen.TryGetMatchValue(address, out _)) found++;
        return found;
    }

    [Benchmark]
    public object Load() => MaxMindDatabase.Load<Dictionary<string, object>>(Reader);
}
