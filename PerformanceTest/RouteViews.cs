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
using System.Net;
using Bitvantage.InternetProtocol;

namespace PerformanceTest;

/// <summary>
///     The CAIDA RouteViews prefix-to-AS tables for IPv4 and IPv6, as the prototype uses them. Download them with
///     <c>Prototypes/GenericTrie/download-routeviews.sh</c> and set ROUTEVIEWS_DIR to the folder. Values are origin AS
///     numbers. The one million lookup keys are random addresses inside randomly chosen announced prefixes, made with the
///     prototype's seed, so results compare with Documentation/Optimization-Log.md.
/// </summary>
public static class RouteViews
{
    public sealed record Table(IPNetwork[] Networks, int[] Asns, UInt128[] Keys, IPAddress[] Addresses);

    private static readonly Lazy<Table> V4 = new(() => Load("rv2", 32));
    private static readonly Lazy<Table> V6 = new(() => Load("rv6", 128));

    public static bool Available => Environment.GetEnvironmentVariable("ROUTEVIEWS_DIR") is { Length: > 0 } dir && Directory.Exists(dir);

    public static Table IPv4 => V4.Value;

    public static Table IPv6 => V6.Value;

    public static IPNetworkLookup<int> Build(Table table)
    {
        var lookup = new IPNetworkLookup<int>();
        using var batch = lookup.BeginBatch();
        for (var i = 0; i < table.Networks.Length; i++)
            lookup.TryAdd(table.Networks[i], table.Asns[i]);
        batch.Commit();
        return lookup;
    }

    private static Table Load(string kind, int width)
    {
        var sw = Stopwatch.StartNew();
        var directory = Environment.GetEnvironmentVariable("ROUTEVIEWS_DIR") ?? throw new InvalidOperationException("Set ROUTEVIEWS_DIR.");
        var file = Directory.GetFiles(directory, $"routeviews-{kind}-*.pfx2as").OrderBy(f => f).Last();
        var bits = new List<UInt128>();
        var lengths = new List<int>();
        var networks = new List<IPNetwork>();
        var asns = new List<int>();
        foreach (var line in File.ReadLines(file))
        {
            var parts = line.Split('\t');
            var address = IPAddress.Parse(parts[0]);
            var length = int.Parse(parts[1]);
            var value = width == 32 ? IPAddressBits.ToIPv4Bits(address) : IPAddressBits.ToIPv6Bits(address);
            value &= Mask(length, width);
            // the origin AS field can be a set such as "64496_64497" or "64496,64497"; keep the first AS
            var asText = parts[2].Split('_', ',')[0];
            bits.Add(value);
            lengths.Add(length);
            networks.Add(new IPNetwork(ToAddress(value, width), length));
            asns.Add(int.TryParse(asText, out var asn) ? asn : 0);
        }

        // the prototype's keys: a random announced prefix, then random bits for the rest of the address
        var random = new Random(42);
        var keys = new UInt128[1_000_000];
        var addresses = new IPAddress[keys.Length];
        var noise = new byte[width / 8];
        for (var i = 0; i < keys.Length; i++)
        {
            var p = random.Next(bits.Count);
            random.NextBytes(noise);
            var host = width == 32 ? (UInt128)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(noise) : System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(noise);
            var mask = Mask(lengths[p], width);
            keys[i] = (bits[p] & mask) | (host & ~mask & Mask(width, width));
            addresses[i] = ToAddress(keys[i], width);
        }

        Console.Error.WriteLine($"loaded {bits.Count:N0} {kind} prefixes from {Path.GetFileName(file)} in {sw.Elapsed.TotalSeconds:F1} s");
        return new Table(networks.ToArray(), asns.ToArray(), keys, addresses);
    }

    // the first length bits of a width-bit number, right-aligned
    private static UInt128 Mask(int length, int width)
    {
        var all = width == 128 ? UInt128.MaxValue : (UInt128.One << width) - 1;
        return length == 0 ? UInt128.Zero : all & ~(all >> length);
    }

    private static IPAddress ToAddress(UInt128 value, int width) => width == 32 ? IPAddressBits.FromIPv4Bits((uint)value) : IPAddressBits.FromIPv6Bits(value);
}
