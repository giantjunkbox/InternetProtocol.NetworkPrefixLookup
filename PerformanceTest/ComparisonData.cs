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
using Bitvantage.InternetProtocol;

namespace PerformanceTest;

/// <summary>
///     The tables for comparing version 3 with version 2, as <see cref="NetworkPrefix" /> objects so that both versions
///     store the same type. Compiled into PerformanceTest and into PerformanceTest.V2, which runs the same benchmarks
///     against version 2 in its own process, because the two library versions have the same assembly name.
/// </summary>
/// <remarks>
///     Each table has 1,000,000 lookup addresses. For the RouteViews tables these are random addresses inside randomly
///     chosen stored prefixes; for the random table, uniformly random addresses, as in the version 2 benchmarks. The
///     small tables are every 1,054th IPv4 prefix and every 240th IPv6 prefix of the RouteViews tables.
/// </remarks>
public static class ComparisonData
{
    public sealed record Table(NetworkPrefix[] Prefixes, int[] Values, IPAddress[] Addresses);

    private static readonly Lazy<Table> RealIPv4 = new(() => Load("rv2", 32));
    private static readonly Lazy<Table> RealIPv6 = new(() => Load("rv6", 128));

    private static readonly Dictionary<string, Lazy<Table>> Tables = new()
    {
        ["SmallIPv4"] = new(() => Sample(RealIPv4.Value, 1054)),
        ["SmallIPv6"] = new(() => Sample(RealIPv6.Value, 240)),
        ["RandomIPv4"] = new(Random),
        ["RealIPv4"] = new(() => RealIPv4.Value),
        ["RealIPv6"] = new(() => RealIPv6.Value)
    };

    private static string? Directory => Environment.GetEnvironmentVariable("ROUTEVIEWS_DIR") is { Length: > 0 } d && System.IO.Directory.Exists(d) ? d : null;

    /// <summary>The names of the tables available: the random table always, the others when ROUTEVIEWS_DIR is set.</summary>
    public static IEnumerable<string> Names => Directory == null ? new[] { "RandomIPv4" } : Tables.Keys;

    public static Table Get(string name) => Tables[name].Value;

    private static Table Load(string kind, int width)
    {
        var file = System.IO.Directory.GetFiles(Directory!, $"routeviews-{kind}-*.pfx2as").OrderBy(f => f).Last();
        var seen = new HashSet<(UInt128, int)>();
        var prefixes = new List<NetworkPrefix>();
        var values = new List<int>();
        var bits = new List<UInt128>();
        foreach (var line in File.ReadLines(file))
        {
            var parts = line.Split('\t');
            var length = int.Parse(parts[1]);
            var value = ToBits(IPAddress.Parse(parts[0]), width) & Mask(length, width);
            if (!seen.Add((value, length))) continue; // the IPv6 table lists a few prefixes twice
            prefixes.Add(new NetworkPrefix(ToAddress(value, width), length));
            values.Add(int.TryParse(parts[2].Split('_', ',')[0], out var asn) ? asn : 0);
            bits.Add(value);
        }

        return new Table(prefixes.ToArray(), values.ToArray(), Addresses(prefixes, bits, width));
    }

    private static Table Sample(Table table, int every)
    {
        var prefixes = table.Prefixes.Where((_, i) => i % every == 0).ToList();
        var width = prefixes[0].Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var bits = prefixes.Select(p => ToBits(p.Address, width)).ToList();
        return new Table(prefixes.ToArray(), table.Values.Where((_, i) => i % every == 0).ToArray(), Addresses(prefixes, bits, width));
    }

    // random addresses inside randomly chosen prefixes of the table
    private static IPAddress[] Addresses(List<NetworkPrefix> prefixes, List<UInt128> bits, int width)
    {
        var random = new Random(42);
        var addresses = new IPAddress[1_000_000];
        var noise = new byte[16];
        for (var i = 0; i < addresses.Length; i++)
        {
            var p = random.Next(prefixes.Count);
            random.NextBytes(noise);
            var host = new UInt128(BitConverter.ToUInt64(noise, 0), BitConverter.ToUInt64(noise, 8)) & Mask(width, width);
            var mask = Mask(prefixes[p].Length, width);
            addresses[i] = ToAddress((bits[p] & mask) | (host & ~mask & Mask(width, width)), width);
        }

        return addresses;
    }

    // version 2's benchmark data: 100,000 random IPv4 prefixes from seed 11, made distinct, and random addresses from seed 12
    private static Table Random()
    {
        var random = new Random(11);
        var prefixes = new List<NetworkPrefix>();
        for (var i = 0; i < 100_000; i++)
        {
            var address = new IPAddress(BitConverter.GetBytes(random.NextInt64(0, 0xff_ff_ff_ffL + 1)).Take(4).ToArray());
            prefixes.Add(new NetworkPrefix(address, (ushort)Math.Cbrt(random.Next(0, 33 * 33 * 33))));
        }

        var distinct = prefixes.Distinct().ToArray();
        var keys = new Random(12);
        var addresses = new IPAddress[1_000_000];
        for (var i = 0; i < addresses.Length; i++)
            addresses[i] = new IPAddress(BitConverter.GetBytes(keys.NextInt64(0, 0xff_ff_ff_ffL + 1)).Take(4).ToArray());
        return new Table(distinct, Enumerable.Range(0, distinct.Length).ToArray(), addresses);
    }

    private static UInt128 ToBits(IPAddress address, int width)
    {
        var bytes = address.GetAddressBytes();
        UInt128 value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        return value;
    }

    private static IPAddress ToAddress(UInt128 value, int width)
    {
        var bytes = new byte[width / 8];
        for (var i = bytes.Length - 1; i >= 0; i--, value >>= 8) bytes[i] = (byte)value;
        return new IPAddress(bytes);
    }

    // the first length bits of a width-bit number
    private static UInt128 Mask(int length, int width)
    {
        var all = width == 128 ? UInt128.MaxValue : (UInt128.One << width) - 1;
        return length == 0 ? UInt128.Zero : all & ~(all >> length);
    }
}
