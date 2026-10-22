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
        Assert.That(lean.Options.MapIPv4MappedAddresses, "MaxMind lookups map by default");
        Assert.That(new MaxMindNetworkLookup<string>().Options.MapIPv4MappedAddresses);
        Assert.That(new IPNetworkLookup<string>().Options.MapIPv4MappedAddresses, Is.False, "other lookups do not");

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

    // A minimal MaxMind DB: a search tree of 24-bit records, one data record { "id": 1 }, and metadata. records(i) gives
    // node i's left and right record: a node number, nodeCount for no data, or -1 for the data record.
    private static byte[] TinyDatabase(int nodeCount, Func<int, (int Left, int Right)> records, int ipVersion)
    {
        var tree = new byte[nodeCount * 6];
        for (var i = 0; i < nodeCount; i++)
        {
            var (left, right) = records(i);
            Write24(tree, i * 6, left == -1 ? nodeCount + 16 : left);
            Write24(tree, i * 6 + 3, right == -1 ? nodeCount + 16 : right);
        }

        var data = new byte[] { 0xE1, 0x42, (byte)'i', (byte)'d', 0xC1, 0x01 };
        var meta = new List<byte> { 0xAB, 0xCD, 0xEF };
        meta.AddRange("MaxMind.com"u8.ToArray());
        meta.Add(0xE9);
        void Key(string key)
        {
            meta.Add((byte)(0x40 | key.Length));
            meta.AddRange(System.Text.Encoding.ASCII.GetBytes(key));
        }

        Key("binary_format_major_version"); meta.AddRange(new byte[] { 0xA1, 2 });
        Key("binary_format_minor_version"); meta.Add(0xA0);
        Key("build_epoch"); meta.AddRange(new byte[] { 0x00, 0x02 });
        Key("database_type"); meta.Add(0x44); meta.AddRange("Test"u8.ToArray());
        Key("description"); meta.Add(0xE0);
        Key("ip_version"); meta.AddRange(new byte[] { 0xA1, (byte)ipVersion });
        Key("languages"); meta.AddRange(new byte[] { 0x00, 0x04 });
        Key("node_count"); meta.AddRange(new byte[] { 0xC2, (byte)(nodeCount >> 8), (byte)nodeCount });
        Key("record_size"); meta.AddRange(new byte[] { 0xA1, 24 });
        return tree.Concat(new byte[16]).Concat(data).Concat(meta).ToArray();

        static void Write24(byte[] bytes, int at, int value)
        {
            bytes[at] = (byte)(value >> 16);
            bytes[at + 1] = (byte)(value >> 8);
            bytes[at + 2] = (byte)value;
        }
    }

    [Test]
    public void A_record_above_the_IPv4_networks_answers_IPv4_addresses_as_the_reader_does()
    {
        // an IPv6 tree whose only record is ::/64, which holds the whole IPv4 part at ::/96
        var bytes = TinyDatabase(64, i => (i == 63 ? -1 : i + 1, 64), 6);
        using var reader = new Reader(new MemoryStream(bytes));
        var lookup = MaxMindDatabase.Load<Dictionary<string, object>>(reader);
        var full = MaxMindDatabase.Load<Dictionary<string, object>>(reader, new MaxMindLoadOptions { IncludeAliasedNetworks = true });
        foreach (var text in new[] { "::", "::1", "0:0:0:1::", "1.2.3.4", "::ffff:1.2.3.4", "::1.2.3.4" })
        {
            var address = IPAddress.Parse(text);
            var expected = reader.Find<Dictionary<string, object>>(address) != null;
            Assert.That(lookup.TryGetMatchValue(address, out _), Is.EqualTo(expected), text);
            Assert.That(full.TryGetMatchValue(address, out _), Is.EqualTo(expected), $"{text}, aliases loaded");
        }
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
