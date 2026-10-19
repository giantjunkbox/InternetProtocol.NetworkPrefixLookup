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

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;

namespace GenericTrie;

/// <summary>
///     Real routing tables: the CAIDA RouteViews prefix-to-AS snapshots for IPv4 and IPv6. Download them with
///     <c>Prototypes/GenericTrie/download-routeviews.sh</c> and set ROUTEVIEWS_DIR to the folder that holds the two
///     uncompressed <c>.pfx2as</c> files. Values are the origin AS numbers.
///     Lookup keys are random addresses inside randomly chosen announced prefixes, because uniformly random addresses
///     would rarely match an IPv6 prefix and would over-represent unannounced IPv4 space.
/// </summary>
public static class RealData
{
    public sealed record Table<TKey>(TKey[] Bits, int[] Lengths, IPNetwork[] Networks, int[] Asns, TKey[] Keys, IPAddress[] Addresses)
        where TKey : struct, IBitKey<TKey>;

    private static readonly Lazy<Table<Bits32>> V4Table = new(() => Load("rv2", 4, s => new Bits32(BinaryPrimitives.ReadUInt32BigEndian(s)), b => IPAddressFrom32(b.Value)));
    private static readonly Lazy<Table<Bits128>> V6Table = new(() => Load("rv6", 16, s => new Bits128(BinaryPrimitives.ReadUInt128BigEndian(s)), b => IPAddressFrom128(b.Value)));

    public static Table<Bits32> V4 => V4Table.Value;
    public static Table<Bits128> V6 => V6Table.Value;

    public static string Directory => Environment.GetEnvironmentVariable("ROUTEVIEWS_DIR")
        ?? throw new InvalidOperationException("Set ROUTEVIEWS_DIR to the folder with the RouteViews .pfx2as files.");

    private static IPAddress IPAddressFrom32(uint bits)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, bits);
        return new IPAddress(bytes);
    }

    private static IPAddress IPAddressFrom128(UInt128 bits)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, bits);
        return new IPAddress(bytes);
    }

    private static Table<TKey> Load<TKey>(string kind, int byteCount, Func<ReadOnlySpan<byte>, TKey> fromBytes, Func<TKey, IPAddress> toAddress)
        where TKey : struct, IBitKey<TKey>
    {
        var sw = Stopwatch.StartNew();
        var file = System.IO.Directory.GetFiles(Directory, $"routeviews-{kind}-*.pfx2as").OrderBy(f => f).Last();
        var bits = new List<TKey>();
        var lengths = new List<int>();
        var networks = new List<IPNetwork>();
        var asns = new List<int>();
        Span<byte> bytes = stackalloc byte[byteCount];

        foreach (var line in File.ReadLines(file))
        {
            var parts = line.Split('\t');
            var address = IPAddress.Parse(parts[0]);
            var length = int.Parse(parts[1]);
            address.TryWriteBytes(bytes, out _);
            var key = TKey.Truncate(fromBytes(bytes), length);
            // the origin AS field can be a set such as "64496_64497" or "64496,64497"; keep the first AS
            var asText = parts[2].Split('_', ',')[0];
            bits.Add(key);
            lengths.Add(length);
            networks.Add(new IPNetwork(toAddress(key), length));
            asns.Add(int.TryParse(asText, out var asn) ? asn : 0);
        }

        // 1,000,000 keys: a random announced prefix, then random bits for the rest of the address
        var random = new Random(42);
        var keys = new TKey[1_000_000];
        var addresses = new IPAddress[keys.Length];
        var width = byteCount * 8;
        Span<byte> noise = stackalloc byte[byteCount]; // outside the loop: stackalloc in a loop is only freed on return
        for (var i = 0; i < keys.Length; i++)
        {
            var p = random.Next(bits.Count);
            random.NextBytes(noise);
            var host = fromBytes(noise);
            // keep the prefix's bits and take the remaining bits from the random value
            var mask = TKey.MaskFor(lengths[p]);
            keys[i] = Combine(bits[p], host, mask, width);
            addresses[i] = toAddress(keys[i]);
        }

        Console.Error.WriteLine($"loaded {bits.Count:N0} {kind} prefixes from {Path.GetFileName(file)} in {sw.Elapsed.TotalSeconds:F1} s");
        return new Table<TKey>(bits.ToArray(), lengths.ToArray(), networks.ToArray(), asns.ToArray(), keys, addresses);
    }

    // (prefix & mask) | (host & ~mask), done through the key interface: truncate the host to nothing where the mask is set
    private static TKey Combine<TKey>(TKey prefix, TKey host, TKey mask, int width) where TKey : struct, IBitKey<TKey>
    {
        // IBitKey has no general bitwise operators, so fall back to the concrete types
        if (typeof(TKey) == typeof(Bits32))
        {
            var p = (Bits32)(object)prefix; var h = (Bits32)(object)host; var m = (Bits32)(object)mask;
            return (TKey)(object)new Bits32((p.Value & m.Value) | (h.Value & ~m.Value));
        }
        if (typeof(TKey) == typeof(Bits128))
        {
            var p = (Bits128)(object)prefix; var h = (Bits128)(object)host; var m = (Bits128)(object)mask;
            return (TKey)(object)new Bits128((p.Value & m.Value) | (h.Value & ~m.Value));
        }
        throw new NotSupportedException(typeof(TKey).Name);
    }
}
