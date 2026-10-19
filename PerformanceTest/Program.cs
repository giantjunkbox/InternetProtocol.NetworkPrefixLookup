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

using System.IO.Compression;
using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Bitvantage.InternetProtocol;

namespace PerformanceTest;

public class Program
{
    [MemoryDiagnoser]
    public class MyTests
    {
        private static readonly NetworkPrefixLookup<string> NetworkPrefixLookup = new();
        private static readonly Dictionary<IPAddress, object> NetworkDictionary = new();
        private static readonly List<NetworkPrefix> IPNetworks = GetRandomIpv4Prefixes(11).Take(100_000).Distinct().ToList();
        private static readonly List<IPAddress> IPAddresses = GetRandomIpv4Addresses(12).Take(1_000_000).ToList();

        static MyTests()
        {
            foreach (var ipNetwork in IPNetworks)
                NetworkPrefixLookup.TryAdd(ipNetwork, string.Empty);

            foreach (var ipAddress in IPAddresses)
                NetworkDictionary.TryAdd(ipAddress, new object());

            using (var r = IPAddresses.GetEnumerator())
                while (NetworkDictionary.Count < 100_000)
                {
                    NetworkDictionary.TryAdd(r.Current, new object());
                    r.MoveNext();
                }
        }

        [IterationCount(30)]
        [Benchmark]
        public void IpNetworkLookupX1M()
        {
            foreach (var ipAddress in IPAddresses)
                if (NetworkPrefixLookup.GetMatch(ipAddress).Value != String.Empty)
                    throw new Exception();
        }

        [IterationCount(30)]
        [Benchmark]
        public void AddX100K()
        {
            var networkLookup = new NetworkPrefixLookup<string?>();
            foreach (var network in IPNetworks)
                networkLookup.TryAdd(network, default);
        }

        [IterationCount(30)]
        [Benchmark]
        public void ConcurrenceAddX100K()
        {
            var networkLookup = new ConcurrenceNetworkPrefixLookup<string?>();
            foreach (var network in IPNetworks)
                networkLookup.TryAdd(network, default);
        }

        [IterationCount(30)]
        [Benchmark]
        public void HashTableLookup()
        {
            foreach (var ipAddress in IPAddresses)
                _ = NetworkDictionary[ipAddress];
        }
    }

    static void Compress(string inputFileName, string outputFileName)
    {
        using (var input = File.OpenRead(inputFileName))
        using (var output = File.Create(outputFileName))
        using (var compressor = new BrotliStream(output, CompressionLevel.SmallestSize))
        {
            input.CopyTo(compressor);
        }
    }

    static void Main(string[] args)
    {
        // pass the command line through so BenchmarkDotNet options such as --filter and --runtimes work
        var summary = BenchmarkRunner.Run<MyTests>(args: args);
        Console.WriteLine(summary);
    }

    public static IEnumerable<NetworkPrefix> GetRandomIpv4Prefixes(int? seed = null)
    {
        var random = seed.HasValue ? new Random(seed.Value) : new Random();

        do
        {
            var addressBits = random.NextInt64(0, 0xff_ff_ff_ffL + 1);
            var addressBytes = BitConverter.GetBytes(addressBits).Take(4).ToArray();
            var randomIpAddress = new IPAddress(addressBytes);
            var randomPrefix = (ushort)Math.Cbrt(random.Next(0, 33 * 33 * 33));
            var randomNetwork = new NetworkPrefix(randomIpAddress, randomPrefix);

            yield return randomNetwork;
        } while (true);
    }

    public static IEnumerable<IPAddress> GetRandomIpv4Addresses(int? seed = null)
    {
        var random = seed.HasValue ? new Random(seed.Value) : new Random();

        do
        {
            var addressBits = random.NextInt64(0, 0xff_ff_ff_ffL + 1);
            var addressBytes = BitConverter.GetBytes(addressBits).Take(4).ToArray();
            var randomIpAddress = new IPAddress(addressBytes);

            yield return randomIpAddress;

        } while (true);
    }
}