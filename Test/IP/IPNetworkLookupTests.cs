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
using Bitvantage.InternetProtocol.NetworkPrefixLookup;

namespace Test.IP;

/// <summary>
///     Tests for the IP layer through <see cref="IPNetworkLookup{TValue}" />: address conversion, both families, the
///     frozen form, and the scenarios of the v2 tests, which asserted tree shapes of the old trie and here assert lookups.
/// </summary>
public class IPNetworkLookupTests
{
    private static IPNetwork N(string text) => IPNetwork.Parse(text);

    private static IPNetworkLookup<string> Lookup(params string[] networks)
    {
        var lookup = new IPNetworkLookup<string>();
        foreach (var network in networks)
            lookup.Add(N(network), network);
        return lookup;
    }

    // both forms, so every assertion covers the read-write and the frozen lookup
    private static IEnumerable<IReadOnlyIPPrefixLookup<IPNetwork, string>> Forms(IPNetworkLookup<string> lookup)
    {
        yield return lookup;
        yield return lookup.Freeze();
        yield return lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }, new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });
    }

    private static string? Match(IReadOnlyIPPrefixLookup<IPNetwork, string> lookup, string address) =>
        lookup.TryGetMatch(IPAddress.Parse(address), out _, out var value) ? value : null;

    private static string[] Values(IEnumerable<PrefixEntry<IPNetwork, string>> entries) => entries.Select(e => e.Value).ToArray();

    [Test(Description = "v2 Add_IPv4_01: nested and adjacent IPv4 prefixes")]
    public void Nested_IPv4_prefixes_give_the_longest_match()
    {
        var lookup = Lookup("10.20.30.0/23", "10.20.30.0/24", "10.20.31.0/24", "10.20.16.0/20", "10.20.0.0/16", "200.20.0.0/16",
            "10.20.30.0/32", "10.20.30.1/32", "10.20.30.2/32", "10.20.30.3/32", "10.20.30.4/32", "10.20.30.5/32");
        foreach (var form in Forms(lookup))
        {
            Assert.That(Match(form, "10.20.30.0"), Is.EqualTo("10.20.30.0/32"));
            Assert.That(Match(form, "10.20.30.5"), Is.EqualTo("10.20.30.5/32"));
            Assert.That(Match(form, "10.20.30.6"), Is.EqualTo("10.20.30.0/24"));
            Assert.That(Match(form, "10.20.31.200"), Is.EqualTo("10.20.31.0/24"));
            Assert.That(Match(form, "10.20.17.1"), Is.EqualTo("10.20.16.0/20"));
            Assert.That(Match(form, "10.20.200.1"), Is.EqualTo("10.20.0.0/16"));
            Assert.That(Match(form, "200.20.1.1"), Is.EqualTo("200.20.0.0/16"));
            Assert.That(Match(form, "10.21.0.0"), Is.Null);
            Assert.That(form.GetMatches(IPAddress.Parse("10.20.30.3")).Select(e => e.Value),
                Is.EqualTo(new[] { "10.20.30.3/32", "10.20.30.0/24", "10.20.30.0/23", "10.20.16.0/20", "10.20.0.0/16" }));
            Assert.That(form.Select(e => e.Value), Is.EqualTo(new[]
            {
                "10.20.0.0/16", "10.20.16.0/20", "10.20.30.0/23", "10.20.30.0/24", "10.20.30.0/32", "10.20.30.1/32", "10.20.30.2/32",
                "10.20.30.3/32", "10.20.30.4/32", "10.20.30.5/32", "10.20.31.0/24", "200.20.0.0/16"
            }), "address order");
        }
    }

    [Test(Description = "v2 Add_IPv4_02 to 05, 07, 08, 10 and 11: small sets in different orders")]
    public void Small_sets_match_in_any_order()
    {
        string[][] sets =
        {
            new[] { "241.104.240.0/21", "128.0.0.0/5", "131.126.152.0/21" },
            new[] { "241.104.240.0/21", "128.0.0.0/5", "129.51.252.0/22" },
            new[] { "241.104.240.0/21", "128.0.0.0/10", "128.0.0.0/1" },
            new[] { "97.84.184.0/21", "76.97.224.0/21", "24.38.216.86/31", "116.11.0.0/16" },
            new[] { "51.229.96.0/23", "40.200.240.0/22", "32.0.0.0/3" },
            new[] { "0.0.0.0/0" }
        };

        foreach (var set in sets)
        {
            var expected = Lookup(set).ToTextTree(AddressFamily.InterNetwork);
            Assert.That(Lookup(set.Reverse().ToArray()).ToTextTree(AddressFamily.InterNetwork), Is.EqualTo(expected), string.Join(", ", set));
            foreach (var network in set)
            {
                var n = N(network);
                Assert.That(Match(Lookup(set), n.BaseAddress.ToString()), Is.EqualTo(set.Where(s => N(s).Contains(n.BaseAddress)).OrderByDescending(s => N(s).PrefixLength).First()));
            }
        }

        Assert.That(Match(Lookup("128.0.0.0/5", "131.126.152.0/21"), "131.126.153.1"), Is.EqualTo("131.126.152.0/21"));
        Assert.That(Match(Lookup("128.0.0.0/5", "131.126.152.0/21"), "131.126.160.1"), Is.EqualTo("128.0.0.0/5"));
        Assert.That(Match(Lookup("0.0.0.0/0"), "255.255.255.255"), Is.EqualTo("0.0.0.0/0"));
    }

    [Test(Description = "v2 Add_IPv4_09 and Delete_IPv4_03: insertion order and removal do not change the shape")]
    public void Order_and_removal_do_not_change_the_shape()
    {
        var random = new Random(55);
        var networks = Enumerable.Range(0, 20000)
            .Select(_ => new IPNetwork(new IPAddress(random.NextInt64(0, 1L << 32)), (int)Math.Cbrt(random.Next(0, 33 * 33 * 33))))
            .Select(n => new IPNetwork(IPAddressBits.FromIPv4Bits(IPAddressBits.ToIPv4Bits(n.BaseAddress) & (uint)(0xFFFFFFFF00000000UL >> n.PrefixLength)), n.PrefixLength))
            .Distinct().Take(10000).ToList();
        var keep = networks.Take(8000).ToList();

        var expected = new IPNetworkLookup<int>(keep.Select(n => KeyValuePair.Create(n, 0))).ToTextTree(AddressFamily.InterNetwork);
        var lookup = new IPNetworkLookup<int>();
        foreach (var network in networks.OrderBy(_ => random.Next()))
            lookup.Add(network, 0);
        foreach (var network in networks.Skip(8000))
            Assert.That(lookup.Remove(network), Is.True);
        Assert.That(lookup.ToTextTree(AddressFamily.InterNetwork), Is.EqualTo(expected));
        Assert.That(lookup.Count, Is.EqualTo(8000));
    }

    [Test(Description = "v2 Add_IPv6_01 and 02")]
    public void IPv6_prefixes_give_the_longest_match()
    {
        var lookup = Lookup("2001:db8:3333:4444:5555:6666:7777:8888/128", "2001:db8:3333:4444:cccc:dddd:eeee:ffff/128", "2001:db8::/128", "::/128",
            "::1234:5678/128", "2001:db8::1234:5678/128", "2001:db8::/32", "2000::/8", "2001:db8:1::ab9:c0a8:102/128", "::b16:212c/128");
        foreach (var form in Forms(lookup))
        {
            Assert.That(Match(form, "2001:db8::1234:5678"), Is.EqualTo("2001:db8::1234:5678/128"));
            Assert.That(Match(form, "2001:db8::1234:5679"), Is.EqualTo("2001:db8::/32"));
            Assert.That(Match(form, "2001:db9::"), Is.EqualTo("2000::/8"));
            Assert.That(Match(form, "::"), Is.EqualTo("::/128"));
            Assert.That(Match(form, "::1"), Is.Null);
            Assert.That(Match(form, "::11.22.33.44"), Is.EqualTo("::b16:212c/128"));
            Assert.That(Match(form, "10.0.0.1"), Is.Null, "IPv4 addresses only search the IPv4 prefixes");
        }

        Assert.That(lookup.ToTextTree(AddressFamily.InterNetworkV6), Does.Contain("2001:db8::/32"));
        Assert.That(lookup.ToTextTree(AddressFamily.InterNetwork), Is.Empty);
    }

    [Test(Description = "v2 TryGetMatch_IPv4_01")]
    public void A_network_matches_the_longest_prefix_that_contains_it()
    {
        var lookup = Lookup("10.0.0.0/8", "10.0.0.0/16", "10.0.0.0/20", "10.0.0.0/24", "10.0.0.0/32");
        foreach (var form in Forms(lookup))
        {
            Assert.That(form.TryGetMatch(N("10.0.0.0/25"), out var prefix, out _) && prefix == N("10.0.0.0/24"));
            Assert.That(form.TryGetMatch(N("10.0.0.0/24"), out prefix, out _) && prefix == N("10.0.0.0/24"));
            Assert.That(form.TryGetMatch(N("10.0.0.0/7"), out _, out _), Is.False);
            Assert.That(form.TryGetValue(N("10.0.0.0/20"), out var value) && value == "10.0.0.0/20");
            Assert.That(form.TryGetValue(N("10.0.0.0/21"), out _), Is.False);
            Assert.That(form.ContainsPrefix(N("10.0.0.0/32")));
            Assert.That(form.ContainsPrefix(N("10.0.0.1/32")), Is.False);
        }
    }

    [Test]
    public void Network_queries_find_the_prefixes_around_and_inside_a_network()
    {
        var lookup = Lookup("10.0.0.0/8", "10.1.0.0/16", "10.1.2.0/24", "10.1.3.0/24", "192.168.0.0/16", "2001:db8::/32", "2001:db8:1::/48");
        foreach (var form in Forms(lookup))
        {
            // the prefixes that contain a network, longest first
            Assert.That(Values(form.GetMatches(N("10.1.2.0/24"))), Is.EqualTo(new[] { "10.1.2.0/24", "10.1.0.0/16", "10.0.0.0/8" }));
            Assert.That(Values(form.GetMatches(N("10.1.5.0/24"))), Is.EqualTo(new[] { "10.1.0.0/16", "10.0.0.0/8" }));
            Assert.That(form.GetMatches(N("10.0.0.0/7")), Is.Empty, "10.0.0.0/8 lies inside it, not around it");
            Assert.That(Values(form.GetMatches(N("2001:db8:1:2::/64"))), Is.EqualTo(new[] { "2001:db8:1::/48", "2001:db8::/32" }));
            var span = new PrefixEntry<IPNetwork, string>[129];
            Assert.That(form.GetMatches(N("10.1.3.128/25"), span), Is.EqualTo(3));
            Assert.That(span[0].Value, Is.EqualTo("10.1.3.0/24"));

            // the prefixes inside a network, in address order
            Assert.That(Values(form.GetSubnets(N("10.1.0.0/16"))), Is.EqualTo(new[] { "10.1.0.0/16", "10.1.2.0/24", "10.1.3.0/24" }));
            Assert.That(Values(form.GetSubnets(N("10.1.2.0/23"))), Is.EqualTo(new[] { "10.1.2.0/24", "10.1.3.0/24" }), "the network itself need not be stored");
            Assert.That(form.GetSubnets(N("10.1.5.0/24")), Is.Empty);
            Assert.That(Values(form.GetSubnets(N("0.0.0.0/0"))), Is.EqualTo(new[] { "10.0.0.0/8", "10.1.0.0/16", "10.1.2.0/24", "10.1.3.0/24", "192.168.0.0/16" }));
            Assert.That(Values(form.GetSubnets(N("2001:db8::/31"))), Is.EqualTo(new[] { "2001:db8::/32", "2001:db8:1::/48" }));
            Assert.That(form.GetSubnets(N("10.0.0.0/8")).Any());

            // whether anything lies either way
            Assert.That(form.Overlaps(N("10.1.5.0/24")), "inside 10.1.0.0/16");
            Assert.That(form.Overlaps(N("10.0.0.0/7")), "around 10.0.0.0/8");
            Assert.That(form.Overlaps(N("10.1.2.0/24")), "stored itself");
            Assert.That(form.Overlaps(N("172.16.0.0/12")), Is.False);
            Assert.That(form.Overlaps(N("2001:db8:1:2::/64")));
            Assert.That(form.Overlaps(N("2001:db9::/32")), Is.False);
            Assert.That(form.Overlaps(N("0.0.0.0/0")) && form.Overlaps(N("::/0")));
        }

        Assert.That(new IPNetworkLookup<string>().Overlaps(N("::/0")), Is.False);
    }

    [Test]
    public void Network_queries_follow_the_mapping()
    {
        // with mapping on, a network inside ::ffff:0:0/96 gets the answers of the IPv4 network it holds, as an address does
        var map = new IPNetworkLookup<string>(new IPLookupOptions { MapIPv4MappedAddresses = true });
        foreach (var text in new[] { "10.0.0.0/8", "10.1.0.0/16", "::/0", "2001:db8::/32" })
            map.Add(N(text), text);
        foreach (var form in Forms(map))
        {
            Assert.That(form.TryGetMatch(N("::ffff:10.1.2.3/128"), out _, out var value) && value == "10.1.0.0/16");
            Assert.That(Match(form, "::ffff:10.1.2.3"), Is.EqualTo("10.1.0.0/16"), "the address agrees");
            Assert.That(Values(form.GetMatches(N("::ffff:10.1.0.0/112"))), Is.EqualTo(new[] { "10.1.0.0/16", "10.0.0.0/8" }));
            Assert.That(Values(form.GetSubnets(N("::ffff:10.0.0.0/104"))), Is.EqualTo(new[] { "10.0.0.0/8", "10.1.0.0/16" }));
            Assert.That(Values(form.GetSubnets(N("::ffff:0:0/96"))), Is.EqualTo(new[] { "10.0.0.0/8", "10.1.0.0/16" }), "all of IPv4");
            Assert.That(form.Overlaps(N("::ffff:10.1.0.0/112")));

            // ::/0 contains 192.168.0.0's mapped form, but answers none of its addresses
            Assert.That(form.Overlaps(N("::ffff:192.168.0.0/112")), Is.False);
            Assert.That(form.TryGetMatch(N("::ffff:192.168.0.0/112"), out _, out _), Is.False);
            Assert.That(form.GetMatches(N("::ffff:192.168.0.0/112")), Is.Empty);

            // a network that contains the whole mapped range contains every IPv4 prefix too, listed first
            Assert.That(Values(form.GetSubnets(N("::/0"))), Is.EqualTo(new[] { "10.0.0.0/8", "10.1.0.0/16", "::/0", "2001:db8::/32" }));
            Assert.That(Values(form.GetSubnets(N("::/80"))), Is.EqualTo(new[] { "10.0.0.0/8", "10.1.0.0/16" }));
            Assert.That(form.GetSubnets(N("::/81")), Is.Empty, "::/81 and ::ffff:0:0/96 are disjoint");
            Assert.That(Values(form.GetMatches(N("::/80"))), Is.EqualTo(new[] { "::/0" }), "no IPv4 prefix contains it");

            // exact lookups take a prefix as given
            Assert.That(form.ContainsPrefix(N("::ffff:10.0.0.0/104")) || form.TryGetValue(N("::ffff:10.0.0.0/104"), out _), Is.False);
        }

        var ipv4Only = new IPNetworkLookup<string>(new IPLookupOptions { MapIPv4MappedAddresses = true });
        ipv4Only.Add(N("10.0.0.0/8"), "10.0.0.0/8");
        foreach (var form in new IReadOnlyIPPrefixLookup<IPNetwork, string>[] { ipv4Only, ipv4Only.Freeze() })
        {
            Assert.That(form.Overlaps(N("::/64")), "the IPv4 prefixes count");
            Assert.That(form.Overlaps(N("2001:db8::/32")), Is.False);
        }

        // with mapping off, every network is taken as given
        var plain = Lookup("10.0.0.0/8", "::/0");
        foreach (var form in Forms(plain))
        {
            Assert.That(form.TryGetMatch(N("::ffff:10.1.2.3/128"), out _, out var value) && value == "::/0");
            Assert.That(Values(form.GetSubnets(N("::/0"))), Is.EqualTo(new[] { "::/0" }));
            Assert.That(form.GetSubnets(N("::ffff:10.0.0.0/104")), Is.Empty);
            Assert.That(form.Overlaps(N("::ffff:10.0.0.0/104")), "inside ::/0");
        }
    }

    [Test]
    public void A_lookup_can_be_built_from_another_lookup_or_a_query()
    {
        var lookup = Lookup("10.0.0.0/8", "10.1.0.0/16", "10.1.2.0/24", "192.168.0.0/16", "2001:db8::/32");
        foreach (var form in Forms(lookup))
        {
            Assert.That(Values(new IPNetworkLookup<string>(form)), Is.EqualTo(Values(lookup)));
            Assert.That(Values(new IPNetworkLookup<string>(form.GetSubnets(N("10.1.0.0/16")))), Is.EqualTo(new[] { "10.1.0.0/16", "10.1.2.0/24" }));
        }

        var mapped = new IPNetworkLookup<string>(lookup, new IPLookupOptions { MapIPv4MappedAddresses = true });
        Assert.That(Match(mapped, "::ffff:10.1.2.3"), Is.EqualTo("10.1.2.0/24"), "the copy takes its own options");

        var entry = new PrefixEntry<IPNetwork, string>(N("10.0.0.0/8"), "x");
        Assert.Throws<ArgumentException>(() => _ = new IPNetworkLookup<string>(new[] { entry, entry }));
        Assert.Throws<ArgumentNullException>(() => _ = new IPNetworkLookup<string>((IEnumerable<PrefixEntry<IPNetwork, string>>)null!));
    }

    [Test(Description = "Every lookup's BeginBatch returns WriteBatch, so a batch can be kept in a field or passed to a helper without naming the lookup's type.")]
    public void Every_lookup_returns_one_batch_type()
    {
        var lookup = new IPNetworkLookup<string>();
        var trie = new BitTrie<Bits32, int, int>();
        var batches = new List<WriteBatch> { lookup.BeginBatch(), trie.BeginBatch() };
        lookup.Add(N("10.0.0.0/8"), "kept");
        trie.Add(new Bits32(0x0A000000), 8, 0, 0);
        Assert.That(lookup.Count + trie.Count, Is.Zero, "nothing is published before the commit");

        foreach (var batch in batches)
            batch.Commit();
        Assert.That(lookup.Count + trie.Count, Is.EqualTo(2));
        Assert.Throws<ObjectDisposedException>(() => batches[0].Commit());
        Assert.DoesNotThrow(() => batches[1].Dispose(), "disposing after a commit does nothing");
    }

    [Test(Description = "Add is what lets a collection initializer fill a lookup, with values or, through the NoValue extension, without.")]
    public void Collection_initializers_fill_a_lookup()
    {
        var routes = new IPNetworkLookup<int> { { N("10.0.0.0/8"), 64512 }, { N("2001:db8::/32"), 64513 } };
        Assert.That(routes.TryGetMatchValue(IPAddress.Parse("10.1.2.3"), out var asn) && asn == 64512);
        Assert.That(routes.TryGetMatchValue(IPAddress.Parse("2001:db8::1"), out asn) && asn == 64513);

        var blocked = new IPNetworkLookup<NoValue> { N("192.0.2.0/24"), N("2001:db8::/32") };
        Assert.That(blocked.ContainsMatch(IPAddress.Parse("192.0.2.7")) && blocked.Count == 2);
        Assert.Throws<ArgumentException>(() => _ = new IPNetworkLookup<NoValue> { N("192.0.2.0/24"), N("192.0.2.0/24") }, "a duplicate throws, as Add does");
    }

    [Test]
    public void Counts_cover_each_family()
    {
        var lookup = Lookup("10.0.0.0/8", "10.1.0.0/16", "192.168.0.0/16", "2001:db8::/32", "::/0");
        foreach (var form in Forms(lookup))
            Assert.That((form.IPv4Count, form.IPv6Count, form.Count), Is.EqualTo((3, 2, 5)));

        using (var batch = lookup.BeginBatch())
        {
            lookup.Add(N("172.16.0.0/12"), "172.16.0.0/12");
            lookup.Remove(N("::/0"));
            Assert.That((lookup.IPv4Count, lookup.IPv6Count, lookup.Count), Is.EqualTo((3, 2, 5)), "an open batch does not count");
            batch.Commit();
        }

        Assert.That((lookup.IPv4Count, lookup.IPv6Count, lookup.Count), Is.EqualTo((4, 1, 5)));
        var empty = new IPNetworkLookup<string>().Freeze();
        Assert.That((empty.IPv4Count, empty.IPv6Count), Is.EqualTo((0, 0)));
    }

    [Test(Description = "v2 Add_IPv4_13: a set of networks without values")]
    public void Networks_without_values()
    {
        var lookup = new IPNetworkLookup<NoValue>();
        foreach (var network in new[] { "69.0.0.0/8", "9.213.0.0/21", "52.137.186.232/30", "59.168.0.0/13", "59.168.83.0/24", "69.169.125.128/30", "69.15.21.0/25", "69.248.13.0/26", "109.114.42.64/28", "109.195.0.0/17" })
            lookup.Add(N(network));
        Assert.That(lookup.TryAdd(N("69.0.0.0/8")), Is.False);
        Assert.That(lookup.TryGetMatch(IPAddress.Parse("69.15.21.1"), out var match) && match == N("69.15.21.0/25"));
        Assert.That(lookup.TryGetMatch(IPAddress.Parse("69.15.22.1"), out match) && match == N("69.0.0.0/8"));
        Assert.That(lookup.ContainsMatch(IPAddress.Parse("70.0.0.0")), Is.False);
        Assert.That(lookup.Freeze().ContainsMatch(IPAddress.Parse("59.168.83.9")));
    }

    [Test]
    public void Every_address_form_finds_the_same_match()
    {
        var lookup = Lookup("10.0.0.0/8", "10.1.0.0/16", "2001:db8::/32", "::ffff:0:0/96");
        foreach (var form in Forms(lookup))
        {
            Assert.That(form.TryGetMatch(new byte[] { 10, 1, 2, 3 }, out _, out var value) && value == "10.1.0.0/16");
            Assert.That(form.TryGetMatchIPv4(0x0A010203, out _, out value) && value == "10.1.0.0/16");
            Assert.That(form.TryGetMatchValueIPv4(0x0A020203, out value) && value == "10.0.0.0/8");
            Assert.That(form.TryGetMatchValue(IPAddress.Parse("10.1.9.9"), out value) && value == "10.1.0.0/16");
            Assert.That(form.TryGetMatchValue(new byte[] { 10, 9, 9, 9 }, out value) && value == "10.0.0.0/8");

            var v6 = IPAddress.Parse("2001:db8::1").GetAddressBytes();
            Assert.That(form.TryGetMatch(v6, out _, out value) && value == "2001:db8::/32");
            Assert.That(form.TryGetMatchIPv6(IPAddressBits.ToIPv6Bits(IPAddress.Parse("2001:db8::1")), out _, out value) && value == "2001:db8::/32");
            Assert.That(form.TryGetMatchValueIPv6(IPAddressBits.ToIPv6Bits(IPAddress.Parse("2001:db8::1")), out value) && value == "2001:db8::/32");

            Assert.That(form.ContainsMatch(IPAddress.Parse("10.9.9.9")) && form.ContainsMatch(new byte[] { 10, 9, 9, 9 }) && form.ContainsMatchIPv4(0x0A090909));
            Assert.That(form.ContainsMatch(IPAddress.Parse("11.9.9.9")) || form.ContainsMatch(new byte[] { 11, 9, 9, 9 }) || form.ContainsMatchIPv4(0x0B090909), Is.False);
            Assert.That(form.ContainsMatch(IPAddress.Parse("2001:db8::1")) && form.ContainsMatch(v6) && form.ContainsMatchIPv6(IPAddressBits.ToIPv6Bits(IPAddress.Parse("2001:db8::1"))));
            Assert.That(form.ContainsMatchIPv6(IPAddressBits.ToIPv6Bits(IPAddress.Parse("2001:db9::1"))), Is.False);

            Assert.Throws<ArgumentException>(() => form.TryGetMatch(new byte[5], out _, out _));
            Assert.Throws<ArgumentException>(() => form.ContainsMatch(new byte[5]));
            Assert.Throws<ArgumentNullException>(() => form.ContainsMatch((IPAddress)null!));
            Assert.Throws<ArgumentNullException>(() => form.TryGetMatch((IPAddress)null!, out _, out _));

            var span = new PrefixEntry<IPNetwork, string>[129];
            Assert.That(form.GetMatches(IPAddress.Parse("10.1.2.3"), span), Is.EqualTo(2));
            Assert.That(span[0].Value, Is.EqualTo("10.1.0.0/16"));

            // every form of GetMatches agrees with the IPAddress form
            foreach (var text in new[] { "10.1.2.3", "10.9.9.9", "11.0.0.1", "2001:db8::1", "2001:db9::1", "::ffff:10.1.2.3" })
            {
                var address = IPAddress.Parse(text);
                var expected = Values(form.GetMatches(address));
                Assert.That(Values(form.GetMatches(address.GetAddressBytes())), Is.EqualTo(expected), text);
                Assert.That(Values(span.Take(form.GetMatches(address.GetAddressBytes(), span))), Is.EqualTo(expected), text);
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    Assert.That(Values(form.GetMatchesIPv4(IPAddressBits.ToIPv4Bits(address))), Is.EqualTo(expected), text);
                    Assert.That(Values(span.Take(form.GetMatchesIPv4(IPAddressBits.ToIPv4Bits(address), span))), Is.EqualTo(expected), text);
                }
                else
                {
                    Assert.That(Values(form.GetMatchesIPv6(IPAddressBits.ToIPv6Bits(address))), Is.EqualTo(expected), text);
                    Assert.That(Values(span.Take(form.GetMatchesIPv6(IPAddressBits.ToIPv6Bits(address), span))), Is.EqualTo(expected), text);
                }
            }

            Assert.Throws<ArgumentException>(() => form.GetMatches(new byte[5]));
            Assert.Throws<ArgumentException>(() => form.GetMatchesIPv4(0x0A010203, new PrefixEntry<IPNetwork, string>[1]));
        }
    }

    [Test]
    public void Addresses_are_looked_up_as_given_unless_mapping_is_on()
    {
        var mapped = IPAddress.Parse("::ffff:10.1.2.3");

        // by default a mapped address is an IPv6 address like any other
        var plain = Lookup("10.0.0.0/8", "::ffff:0:0/96");
        Assert.That(plain.Options.MapIPv4MappedAddresses, Is.False);
        foreach (var form in Forms(plain))
        {
            Assert.That(Match(form, "::ffff:10.1.2.3"), Is.EqualTo("::ffff:0:0/96"));
            Assert.That(form.TryGetMatchValueIPv6(IPAddressBits.ToIPv6Bits(mapped), out var value) && value == "::ffff:0:0/96");
            Assert.That(Match(form, "10.1.2.3"), Is.EqualTo("10.0.0.0/8"));
        }

        // with mapping on, it gets exactly the answer of the IPv4 address it holds, in every form
        var map = new IPNetworkLookup<string>(new IPLookupOptions { MapIPv4MappedAddresses = true });
        map.Add(N("10.0.0.0/8"), "10.0.0.0/8");
        map.Add(N("::/0"), "::/0");
        foreach (var form in new IReadOnlyIPPrefixLookup<IPNetwork, string>[] { map, map.Freeze(), map.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }) })
        {
            Assert.That(Match(form, "::ffff:10.1.2.3"), Is.EqualTo("10.0.0.0/8"));
            Assert.That(form.TryGetMatch(mapped.GetAddressBytes(), out _, out var value) && value == "10.0.0.0/8");
            Assert.That(form.TryGetMatchIPv6(IPAddressBits.ToIPv6Bits(mapped), out _, out value) && value == "10.0.0.0/8");
            Assert.That(form.GetMatches(mapped).Select(m => m.Value), Is.EqualTo(new[] { "10.0.0.0/8" }));
            Assert.That(Values(form.GetMatches(mapped.GetAddressBytes())), Is.EqualTo(new[] { "10.0.0.0/8" }));
            Assert.That(Values(form.GetMatchesIPv6(IPAddressBits.ToIPv6Bits(mapped))), Is.EqualTo(new[] { "10.0.0.0/8" }));
            Assert.That(form.ContainsMatch(mapped) && form.ContainsMatch(mapped.GetAddressBytes()) && form.ContainsMatchIPv6(IPAddressBits.ToIPv6Bits(mapped)));

            // no IPv4 prefix holds 11.1.2.3, so neither form matches; ::/0, which contains every mapped address, never
            // answers one
            var other = IPAddress.Parse("::ffff:11.1.2.3");
            Assert.That(Match(form, "::ffff:11.1.2.3"), Is.Null);
            Assert.That(form.TryGetMatchValue(other.GetAddressBytes(), out _), Is.False);
            Assert.That(form.TryGetMatchValueIPv6(IPAddressBits.ToIPv6Bits(other), out _), Is.False);
            Assert.That(form.ContainsMatch(other) || form.ContainsMatch(other.GetAddressBytes()) || form.ContainsMatchIPv6(IPAddressBits.ToIPv6Bits(other)), Is.False);
            Assert.That(Match(form, "11.1.2.3"), Is.Null);
            Assert.That(Match(form, "2001:db8::1"), Is.EqualTo("::/0"), "other IPv6 addresses are unaffected");
        }
    }

    [Test]
    public void A_lookup_built_from_a_sequence_matches_one_built_a_prefix_at_a_time()
    {
        var random = new Random(41);
        var networks = new List<IPNetwork>();
        for (var i = 0; i < 4000; i++)
        {
            var length = random.Next(0, 33);
            networks.Add(new IPNetwork(IPAddressBits.FromIPv4Bits(length == 0 ? 0 : (uint)random.NextInt64(1L << 32) & (uint.MaxValue << (32 - length))), length));
            length = random.Next(0, 129);
            var bits = ((UInt128)(ulong)random.NextInt64() << 64 | (ulong)random.NextInt64()) & (length == 0 ? 0 : UInt128.MaxValue << (128 - length));
            networks.Add(new IPNetwork(IPAddressBits.FromIPv6Bits(bits), length));
        }

        networks = networks.Distinct().ToList();
        var oneByOne = new IPNetworkLookup<int>();
        for (var i = 0; i < networks.Count; i++)
            oneByOne.Add(networks[i], i);
        var built = new IPNetworkLookup<int>(networks.Select((n, i) => KeyValuePair.Create(n, i)));

        Assert.That(built.Count, Is.EqualTo(networks.Count));
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
            Assert.That(built.ToTextTree(family), Is.EqualTo(oneByOne.ToTextTree(family)), family.ToString());
        Assert.That(built.Select(e => e.Value), Is.EqualTo(oneByOne.Select(e => e.Value)), "enumeration");

        Assert.Throws<ArgumentException>(() => _ = new IPNetworkLookup<int>(new[] { KeyValuePair.Create(N("10.0.0.0/8"), 1), KeyValuePair.Create(N("10.0.0.0/8"), 2) }));
        Assert.Throws<ArgumentException>(() => _ = new IPNetworkLookup<int>(new[] { KeyValuePair.Create(N("::ffff:10.0.0.0/104"), 1) }, new IPLookupOptions { MapIPv4MappedAddresses = true }));
    }

    [Test]
    public void Conditional_writes_work_in_both_families()
    {
        var lookup = Lookup("10.0.0.0/8", "2001:db8::/32");
        foreach (var text in new[] { "10.0.0.0/8", "2001:db8::/32" })
        {
            var network = N(text);
            Assert.That(lookup.TryUpdate(network, "new", "other"), Is.False, text);
            Assert.That(lookup.TryUpdate(network, "new", text), Is.True, text);
            Assert.That(lookup.TryRemove(network, text), Is.False, text);
            Assert.That(lookup.TryRemove(network, "new"), Is.True, text);
            lookup.Add(network, "again");
            Assert.That(lookup.Remove(network, out var removed) && removed == "again", text);
            Assert.That(lookup.Remove(network, out _), Is.False, text);
        }

        Assert.That(lookup.Count, Is.Zero);
        var map = new IPNetworkLookup<string>(new IPLookupOptions { MapIPv4MappedAddresses = true });
        Assert.Throws<ArgumentException>(() => map.TryUpdate(N("::ffff:10.0.0.0/104"), "x", "y"));
        Assert.That(map.TryRemove(N("::ffff:10.0.0.0/104"), "y") || map.Remove(N("::ffff:10.0.0.0/104"), out _), Is.False);
    }

    [Test]
    public void With_mapping_on_prefixes_inside_the_mapped_range_are_refused()
    {
        var map = new IPNetworkLookup<string>(new IPLookupOptions { MapIPv4MappedAddresses = true });
        foreach (var text in new[] { "::ffff:0:0/96", "::ffff:10.0.0.0/104", "::ffff:10.1.2.3/128" })
        {
            var network = N(text);
            Assert.Throws<ArgumentException>(() => map.TryAdd(network, text), text);
            Assert.Throws<ArgumentException>(() => map.Add(network, text), text);
            Assert.Throws<ArgumentException>(() => map.AddOrUpdate(network, text), text);
            Assert.Throws<ArgumentException>(() => map.GetOrAdd(network, _ => text), text);
            Assert.That(map.Remove(network), Is.False, text);
        }

        // prefixes that merely contain the mapped range are stored, and so is everything with mapping off
        Assert.That(map.TryAdd(N("::/0"), "::/0"));
        Assert.That(map.TryAdd(N("::/80"), "::/80"));
        Assert.That(map.Count, Is.EqualTo(2));
        Assert.That(new IPNetworkLookup<string>().TryAdd(N("::ffff:10.0.0.0/104"), "as given"));

        // a refused write changes nothing, so the batch it was written in still commits
        using (var batch = map.BeginBatch())
        {
            map.Add(N("10.0.0.0/8"), "10.0.0.0/8");
            Assert.Throws<ArgumentException>(() => map.Add(N("::ffff:10.0.0.0/104"), "refused"));
            batch.Commit();
        }

        Assert.That(Match(map, "::ffff:10.1.2.3"), Is.EqualTo("10.0.0.0/8"));
    }

    [Test]
    public void A_commit_whose_IPv4_half_throws_publishes_nothing()
    {
        var lookup = Lookup("10.0.0.0/8");
        var restore = Test.Core.CommitFaults.BreakNextCommit(Test.Core.CommitFaults.Field(lookup, "_v4"));
        var batch = lookup.BeginBatch();
        lookup.Add(N("10.1.0.0/16"), "10.1.0.0/16");
        lookup.Add(N("10.2.0.0/16"), "10.2.0.0/16");
        lookup.Add(N("2001:db8::/32"), "2001:db8::/32");
        Assert.Throws<NullReferenceException>(() => batch.Commit());
        restore();

        Assert.That(lookup.Count, Is.EqualTo(1));
        Assert.That(Match(lookup, "10.1.2.3"), Is.EqualTo("10.0.0.0/8"));
        Assert.That(Match(lookup, "2001:db8::1"), Is.Null, "the IPv6 half is discarded too");
        lookup.Add(N("2001:db8::/32"), "2001:db8::/32");
        Assert.That(Match(lookup, "2001:db8::1"), Is.EqualTo("2001:db8::/32"));
    }

    [Test]
    public void A_batch_is_refused_on_another_thread_and_discarded_after_a_failed_write()
    {
        var lookup = Lookup("10.0.0.0/8");
        using (var owner = new Test.Core.DedicatedThread())
        {
            var batch = owner.Run(() =>
            {
                var started = lookup.BeginBatch();
                lookup.Add(N("2001:db8::/32"), "2001:db8::/32");
                return started;
            });
            Assert.Throws<InvalidOperationException>(() => batch.Commit());
            Assert.Throws<InvalidOperationException>(() => batch.Dispose());
            Assert.That(lookup.Count, Is.EqualTo(1));
            owner.Run(() => batch.Commit());
            Assert.That(lookup.Count, Is.EqualTo(2));
        }

        // a write that threw in either family discards the whole batch
        using (var batch = lookup.BeginBatch())
        {
            lookup.Add(N("11.0.0.0/8"), "11.0.0.0/8");
            lookup.Add(N("2001:db9::/32"), "2001:db9::/32");
            Test.Core.CommitFaults.FailBatch(Test.Core.CommitFaults.Field(lookup, "_v6"));
            Assert.Throws<InvalidOperationException>(() => batch.Commit());
        }

        Assert.That(lookup.Count, Is.EqualTo(2));
        Assert.That(Match(lookup, "11.1.2.3"), Is.Null, "the IPv4 half is discarded too");
        lookup.Add(N("11.0.0.0/8"), "11.0.0.0/8");
        Assert.That(Match(lookup, "11.1.2.3"), Is.EqualTo("11.0.0.0/8"));
    }

    [Test(Description = "Batches replace the one IPv4 and the one IPv6 prefix with a pair holding the same value while another thread freezes; every frozen copy must hold one pair.")]
    public void Freeze_copies_both_families_at_one_moment()
    {
        var lookup = new IPNetworkLookup<int>();
        lookup.Add(N("10.0.0.0/8"), 0);
        lookup.Add(N("2001:db8::/32"), 0);
        var stop = false;
        var writer = new Thread(() =>
        {
            for (var i = 1; !Volatile.Read(ref stop); i++)
            {
                using var batch = lookup.BeginBatch();
                lookup.AddOrUpdate(N("10.0.0.0/8"), i);
                lookup.AddOrUpdate(N("2001:db8::/32"), i);
                batch.Commit();
            }
        }) { IsBackground = true };
        writer.Start();

        int copies = 0, mixed = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (clock.ElapsedMilliseconds < 1000)
            {
                var frozen = lookup.Freeze();
                frozen.TryGetMatchValueIPv4(0x0A000001, out var ipv4);
                frozen.TryGetMatchValue(IPAddress.Parse("2001:db8::1"), out var ipv6);
                if (ipv4 != ipv6) mixed++;
                copies++;
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            writer.Join();
        }

        Assert.That(mixed, Is.Zero, $"{mixed:N0} of {copies:N0} copies mixed two batches");
    }

    [Test]
    public void One_freeze_layout_applies_to_both_families()
    {
        var lookup = new IPNetworkLookup<int>();
        var random = new Random(31);
        for (var i = 0; i < 2000; i++)
        {
            lookup.TryAdd(new IPNetwork(IPAddressBits.FromIPv4Bits((uint)random.Next()), 32), i);
            lookup.TryAdd(new IPNetwork(IPAddressBits.FromIPv6Bits(((UInt128)(ulong)random.NextInt64() << 64) | (ulong)random.NextInt64()), 128), i);
        }

        var compressed = new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed };
        Assert.That(lookup.Freeze(compressed).Bytes, Is.EqualTo(lookup.Freeze(compressed, compressed).Bytes));
        Assert.That(lookup.Freeze(compressed, new FrozenBitTrieOptions { Layout = FrozenLayout.LeafPushed }).Bytes, Is.GreaterThan(lookup.Freeze(compressed).Bytes), "scattered IPv6 host routes take more room leaf-pushed");
    }

    [Test]
    public void Address_conversions_check_the_family()
    {
        Assert.That(IPAddressBits.ToIPv4Bits(IPAddress.Parse("10.1.2.3")), Is.EqualTo(0x0A010203u));
        Assert.That(IPAddressBits.ToIPv6Bits(IPAddress.Parse("::ffff:10.1.2.3")), Is.EqualTo((UInt128)0xFFFF_0A010203UL));
        Assert.That(IPAddressBits.FromIPv6Bits(IPAddressBits.ToIPv6Bits(IPAddress.Parse("2001:db8::1"))), Is.EqualTo(IPAddress.Parse("2001:db8::1")));
        Assert.Throws<ArgumentException>(() => IPAddressBits.ToIPv4Bits(IPAddress.Parse("::1")));
        Assert.Throws<ArgumentException>(() => IPAddressBits.ToIPv6Bits(IPAddress.Parse("10.1.2.3")));
        Assert.Throws<ArgumentNullException>(() => IPAddressBits.ToIPv4Bits(null!));
        Assert.Throws<ArgumentNullException>(() => IPAddressBits.ToIPv6Bits(null!));
    }

    [Test]
    public void Writes_and_batches_cover_both_families()
    {
        var lookup = new IPNetworkLookup<int>();
        Assert.That(lookup.TryAdd(N("10.0.0.0/8"), 1));
        Assert.That(lookup.TryAdd(N("10.0.0.0/8"), 2), Is.False);
        Assert.Throws<ArgumentException>(() => lookup.Add(N("10.0.0.0/8"), 2));
        Assert.That(lookup.AddOrUpdate(N("10.0.0.0/8"), 3), Is.False);
        Assert.That(lookup.AddOrUpdate(N("2001:db8::/32"), 4), Is.True);
        Assert.That(lookup.GetOrAdd(N("2001:db8::/32"), _ => 5), Is.EqualTo(4));
        Assert.That(lookup.GetOrAdd(N("2001:db8::/48"), _ => 6), Is.EqualTo(6));
        Assert.That(lookup.Count, Is.EqualTo(3));
        var version = lookup.Version;

        using (var batch = lookup.BeginBatch())
        {
            lookup.Add(N("192.168.0.0/16"), 7);
            lookup.Remove(N("2001:db8::/48"));
            Assert.That(Task.Run(() => lookup.Count).Result, Is.EqualTo(3), "invisible until committed");
            batch.Commit();
        }

        Assert.That(lookup.Count, Is.EqualTo(3));
        Assert.That(lookup.Version, Is.GreaterThan(version));
        Assert.That(lookup.TryGetMatchValue(IPAddress.Parse("192.168.1.1"), out var value) && value == 7);

        using (lookup.BeginBatch())
            lookup.Clear();
        Assert.That(lookup.Count, Is.EqualTo(3), "discarded");

        Assert.That(lookup.GetStatistics().IPv4.Prefixes, Is.EqualTo(2));
        Assert.That(lookup.Freeze().Bytes, Is.GreaterThan(0));
        Assert.That(lookup.ToDotTree(AddressFamily.InterNetworkV6), Does.StartWith("digraph"));
        Assert.Throws<ArgumentException>(() => lookup.ToTextTree(AddressFamily.Unix));
        lookup.Clear();
        Assert.That(lookup.Count, Is.Zero);
    }

    [Test(Description = "v2 Add_IPv4_12: readers always find 0.0.0.0/32 while every other prefix on its path churns")]
    public void Readers_never_see_a_half_made_change()
    {
        var lookup = new IPNetworkLookup<string>();
        var networks = new List<IPNetwork>();
        for (var length = 0; length <= 31; length++)
        {
            networks.Add(new IPNetwork(IPAddress.Any, length));
            if (length > 0) networks.Add(new IPNetwork(IPAddressBits.FromIPv4Bits(1u << (32 - length)), length));
        }

        lookup.Add(N("0.0.0.0/32"), "Success");
        var stop = false;
        long failures = 0;
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
                if (!lookup.TryGetMatchValue(IPAddress.Any, out var value) || value != "Success")
                    Interlocked.Increment(ref failures);
        }) { IsBackground = true };
        reader.Start();

        try
        {
            var random = new Random(20);
            for (var round = 0; round < 500; round++)
            {
                foreach (var network in networks.OrderBy(_ => random.Next()))
                    lookup.Add(network, string.Empty);
                foreach (var network in networks.OrderBy(_ => random.Next()))
                    lookup.Remove(network);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            reader.Join();
        }

        Assert.That(failures, Is.Zero);
    }
}
