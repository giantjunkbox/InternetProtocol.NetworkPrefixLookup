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
using System.Text.Json;
using Bitvantage.InternetProtocol;
using MaxMind.Db;
using NetTools;

namespace Test.Adapters;

/// <summary>Tests for the adapter packages: each reads its library's type correctly, in both families and both forms.</summary>
public class AdapterTests
{
    [Test]
    public void NetworkPrefix_lookups()
    {
        var lookup = new NetworkPrefixLookup<string>();
        foreach (var prefix in new[] { "10.0.0.0/8", "10.1.0.0/16", "2001:db8::/32", "2001:db8:1::/48", "0.0.0.0/0" })
            lookup.Add(NetworkPrefix.Parse(prefix), prefix);

        foreach (IReadOnlyIPPrefixLookup<NetworkPrefix, string> form in new IReadOnlyIPPrefixLookup<NetworkPrefix, string>[] { lookup, lookup.Freeze() })
        {
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("10.1.2.3"), out var value) && value == "10.1.0.0/16");
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("10.2.2.3"), out value) && value == "10.0.0.0/8");
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("11.0.0.1"), out value) && value == "0.0.0.0/0");
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("2001:db8:1::5"), out value) && value == "2001:db8:1::/48");
            Assert.That(form.TryGetMatch(IPAddress.Parse("2001:db8:2::5"), out var prefix, out _) && prefix.ToString() == "2001:db8::/32");
            Assert.That(form.TryGetMatch(NetworkPrefix.Parse("10.1.2.0/24"), out prefix, out _) && prefix.ToString() == "10.1.0.0/16");
            Assert.That(form.ContainsPrefix(NetworkPrefix.Parse("2001:db8::/32")));
            Assert.That(form.Count, Is.EqualTo(5));
        }

        var set = new NetworkPrefixLookup(new[] { NetworkPrefix.Parse("192.168.0.0/16"), NetworkPrefix.Parse("192.168.10.0/24") });
        Assert.That(set.TryAdd(NetworkPrefix.Parse("192.168.10.0/24")), Is.False);
        Assert.That(set.TryGetMatch(IPAddress.Parse("192.168.10.9"), out var match) && match.ToString() == "192.168.10.0/24");
        FrozenNetworkPrefixLookup frozen = set.Freeze();
        Assert.That(frozen.ContainsMatch(IPAddress.Parse("192.168.200.1")));
        Assert.That(frozen.ContainsMatch(IPAddress.Parse("192.169.0.1")), Is.False);
    }

    [Test]
    public void IPNetwork2_lookups()
    {
        var lookup = new IPNetwork2Lookup<int>();
        lookup.Add(IPNetwork2.Parse("10.0.0.0/8"), 8);
        lookup.Add(IPNetwork2.Parse("10.128.0.0/9"), 9);
        lookup.Add(IPNetwork2.Parse("fd00::/8"), 108);
        lookup.Add(IPNetwork2.Parse("fd12:3456::/32"), 132);
        Assert.That(lookup.TryAdd(IPNetwork2.Parse("10.0.0.0/8"), 0), Is.False);

        foreach (IReadOnlyIPPrefixLookup<IPNetwork2, int> form in new IReadOnlyIPPrefixLookup<IPNetwork2, int>[] { lookup, lookup.Freeze() })
        {
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("10.200.0.1"), out var value) && value == 9);
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("10.1.0.1"), out value) && value == 8);
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("fd12:3456::1"), out value) && value == 132);
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("fd99::1"), out value) && value == 108);
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("fe80::1"), out _), Is.False);
            Assert.That(form.TryGetMatch(IPAddress.Parse("10.200.0.1"), out var network, out _) && network.ToString() == "10.128.0.0/9");
        }

        Assert.That(lookup.Remove(IPNetwork2.Parse("10.128.0.0/9")));
        Assert.That(lookup.TryGetMatchValue(IPAddress.Parse("10.200.0.1"), out var after) && after == 8);
    }

    [Test]
    public void IPAddressRange_lookups()
    {
        var lookup = new IPAddressRangeLookup<string>();
        lookup.Add(IPAddressRange.Parse("192.168.0.10-192.168.0.20"), "small");
        lookup.Add(IPAddressRange.Parse("192.168.0.0/24"), "block");
        lookup.Add(IPAddressRange.Parse("fe80::1-fe80::ff"), "link");

        foreach (IReadOnlyIPPrefixLookup<IPAddressRange, string> form in new IReadOnlyIPPrefixLookup<IPAddressRange, string>[] { lookup, lookup.Freeze() })
        {
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("192.168.0.15"), out var value) && value == "small");
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("192.168.0.9"), out value) && value == "block");
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("192.168.1.9"), out _), Is.False);
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("fe80::80"), out value) && value == "link");
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("fe80::100"), out _), Is.False);
            Assert.That(form.TryGetMatch(IPAddress.Parse("192.168.0.20"), out var range, out _) && range.ToString() == "192.168.0.10-192.168.0.20");
            Assert.That(form.ContainsPrefix(IPAddressRange.Parse("192.168.0.0-192.168.0.255")), "matched by its ends, however it was written");
            Assert.That(form.Count, Is.EqualTo(3));
        }

        Assert.That(lookup.Remove(IPAddressRange.Parse("192.168.0.10-192.168.0.20")));
        Assert.That(lookup.TryGetMatchValue(IPAddress.Parse("192.168.0.15"), out var after) && after == "block");
    }

    private static string DataPath(string file) => Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", "MaxMind", file);

    private static string Json(object? record) => JsonSerializer.Serialize(record);

    // the first and last address of every network in the database, with their neighbours, plus random addresses
    private static List<IPAddress> Probes(Reader reader, int seed)
    {
        var probes = new List<IPAddress>();
        foreach (var node in reader.FindAll<Dictionary<string, object>>())
        {
            if (node.PrefixLength < 0) continue;
            var v4 = node.Start.AddressFamily == AddressFamily.InterNetwork;
            if (v4)
            {
                var first = IPAddressBits.ToIPv4Bits(node.Start);
                var last = first | (uint)(0xFFFFFFFFUL >> node.PrefixLength);
                // each address as IPv4, IPv4-compatible and IPv4-mapped
                foreach (var address in new[] { first, last, first - 1, last + 1 })
                {
                    probes.Add(IPAddressBits.FromIPv4Bits(address));
                    probes.Add(IPAddressBits.FromIPv6Bits(address));
                    probes.Add(IPAddressBits.FromIPv6Bits(address | ((UInt128)0xFFFF << 32)));
                }
            }
            else
            {
                var first = IPAddressBits.ToIPv6Bits(node.Start);
                var last = first | (node.PrefixLength == 0 ? UInt128.MaxValue : UInt128.MaxValue >> node.PrefixLength);
                probes.AddRange(new[] { first, last, first - 1, last + 1 }.Select(IPAddressBits.FromIPv6Bits));
            }
        }

        var random = new Random(seed);
        for (var i = 0; i < 2000; i++)
        {
            probes.Add(IPAddressBits.FromIPv4Bits((uint)random.NextInt64(0, 1L << 32)));
            var bytes = new byte[16];
            random.NextBytes(bytes);
            probes.Add(new IPAddress(bytes));
        }

        return probes;
    }

    // the aliases of the IPv4 networks in MaxMind's IPv6 databases: IPv4-mapped, Teredo and 6to4
    private static bool InAlias(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bits = IPAddressBits.ToIPv6Bits(address);
        return bits >> 32 == 0xFFFF || bits >> 96 == 0x20010000 || bits >> 112 == 0x2002;
    }

    // ::/96, where MaxMind's IPv6 databases keep the IPv4 networks
    private static bool IsIPv4Compatible(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && IPAddressBits.ToIPv6Bits(address) >> 32 == 0;

    [TestCase("MaxMind-DB-test-mixed-24.mmdb")]
    [TestCase("GeoIP2-Country-Test.mmdb")]
    public void A_loaded_MaxMind_database_answers_like_the_reader(string file)
    {
        using var reader = new Reader(DataPath(file));
        var probes = Probes(reader, 7);

        // with the aliases loaded and no mapping, every address must match the reader exactly
        var full = MaxMindDatabase.Load<Dictionary<string, object>>(reader, new MaxMindLoadOptions { IncludeAliasedNetworks = true, LookupOptions = new IPLookupOptions { MapIPv4MappedAddresses = false } });
        // by default the aliases are skipped, and IPv4-mapped addresses are looked up as IPv4
        var lean = MaxMindDatabase.Load<Dictionary<string, object>>(DataPath(file));
        Assert.That(lean.Count, Is.LessThanOrEqualTo(full.Count));

        var failures = new List<string>();
        var found = 0;
        foreach (var address in probes)
        {
            var expected = Json(reader.Find<Dictionary<string, object>>(address));
            if (expected != "null") found++;
            foreach (var (name, form) in new (string, IReadOnlyIPPrefixLookup<Network, Dictionary<string, object>>)[] { ("full", full), ("full frozen", full.Freeze()), ("lean", lean), ("lean frozen", lean.Freeze()) })
            {
                if (name.StartsWith("lean") && (IsIPv4Compatible(address) || (InAlias(address) && IPAddressBits.ToIPv6Bits(address) >> 32 != 0xFFFF)))
                    continue; // by default, only IPv4-mapped addresses find the IPv4 networks
                var actual = Json(form.TryGetMatchValue(address, out var record) ? record : null);
                if (actual != expected)
                    failures.Add($"{name} at {address}: {actual}, expected {expected}");
            }
        }

        Assert.That(failures, Is.Empty, $"{failures.Count} of {probes.Count * 4}; the first: {failures.FirstOrDefault()}");
        Assert.That(found, Is.GreaterThan(50), "enough probes land in a network");
    }

    [Test]
    public void The_loader_skips_the_IPv4_aliases_by_default()
    {
        using var reader = new Reader(DataPath("MaxMind-DB-test-mixed-24.mmdb"));
        var full = MaxMindDatabase.Load<Dictionary<string, object>>(reader, new MaxMindLoadOptions { IncludeAliasedNetworks = true });
        var lean = MaxMindDatabase.Load<Dictionary<string, object>>(reader);
        var ipv4 = full.Count(e => e.Prefix.NetworkAddress.AddressFamily == AddressFamily.InterNetwork);
        Assert.That(ipv4, Is.GreaterThan(0));
        Assert.That(full.Count - lean.Count, Is.EqualTo(4 * ipv4), "each IPv4 network appears under ::/96 and three aliases");
        Assert.That(lean.Any(e => InAlias(e.Prefix.NetworkAddress)), Is.False);
    }
}
