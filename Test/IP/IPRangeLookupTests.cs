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

namespace Test.IP;

/// <summary>A range type and its adapter, as a library user would write them.</summary>
public sealed record TestRange(IPAddress First, IPAddress Last)
{
    public static TestRange Parse(string text)
    {
        var parts = text.Split('-');
        return new TestRange(IPAddress.Parse(parts[0]), IPAddress.Parse(parts[^1]));
    }

    public override string ToString() => $"{First}-{Last}";
}

public readonly struct TestRangeAdapter : IIPRangeAdapter<TestRange>
{
    public static IPAddress GetFirst(TestRange range) => range.First;

    public static IPAddress GetLast(TestRange range) => range.Last;
}

/// <summary>A range type whose ends can change after it is stored.</summary>
public sealed class MutableRange(IPAddress first, IPAddress last)
{
    public IPAddress First { get; set; } = first;
    public IPAddress Last { get; set; } = last;
    public override string ToString() => $"{First}-{Last}";
}

public readonly struct MutableRangeAdapter : IIPRangeAdapter<MutableRange>
{
    public static IPAddress GetFirst(MutableRange range) => range.First;

    public static IPAddress GetLast(MutableRange range) => range.Last;
}

/// <summary>Tests for <see cref="IPRangeLookup{TRange,TAdapter,TValue}" /> and its frozen form.</summary>
public class IPRangeLookupTests
{
    private static TestRange R(string text) => TestRange.Parse(text);

    private static IPRangeLookup<TestRange, TestRangeAdapter, string> Lookup(params string[] ranges)
    {
        var lookup = new IPRangeLookup<TestRange, TestRangeAdapter, string>();
        foreach (var range in ranges)
            lookup.Add(R(range), range);
        return lookup;
    }

    private static IEnumerable<IReadOnlyIPPrefixLookup<TestRange, string>> Forms(IPRangeLookup<TestRange, TestRangeAdapter, string> lookup)
    {
        yield return lookup;
        yield return lookup.Freeze();
        yield return lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed });
    }

    private static string? Match(IReadOnlyIPPrefixLookup<TestRange, string> lookup, string address) =>
        lookup.TryGetMatch(IPAddress.Parse(address), out _, out var value) ? value : null;

    [Test]
    public void A_range_covers_exactly_its_addresses()
    {
        var lookup = Lookup("192.168.0.10-192.168.0.20", "2001:db8::5-2001:db8::1:0");
        foreach (var form in Forms(lookup))
        {
            Assert.That(Match(form, "192.168.0.9"), Is.Null);
            Assert.That(Match(form, "192.168.0.10"), Is.EqualTo("192.168.0.10-192.168.0.20"));
            Assert.That(Match(form, "192.168.0.16"), Is.EqualTo("192.168.0.10-192.168.0.20"));
            Assert.That(Match(form, "192.168.0.20"), Is.EqualTo("192.168.0.10-192.168.0.20"));
            Assert.That(Match(form, "192.168.0.21"), Is.Null);
            Assert.That(Match(form, "2001:db8::4"), Is.Null);
            Assert.That(Match(form, "2001:db8::5"), Is.EqualTo("2001:db8::5-2001:db8::1:0"));
            Assert.That(Match(form, "2001:db8::ffff"), Is.EqualTo("2001:db8::5-2001:db8::1:0"));
            Assert.That(Match(form, "2001:db8::1:0"), Is.EqualTo("2001:db8::5-2001:db8::1:0"));
            Assert.That(Match(form, "2001:db8::1:1"), Is.Null);
            Assert.That(form.Count, Is.EqualTo(2));
        }

        // 10, 11-12... as prefixes: .10/31, .12/30, .16/30, .20/32
        Assert.That(lookup.GetStatistics().IPv4.Prefixes, Is.EqualTo(4));
    }

    [Test]
    public void Ranges_split_into_the_fewest_prefixes()
    {
        Assert.That(Lookup("0.0.0.1-255.255.255.254").GetStatistics().IPv4.Prefixes, Is.EqualTo(62), "the worst case for IPv4");
        Assert.That(Lookup("::1-ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffe").GetStatistics().IPv6.Prefixes, Is.EqualTo(254), "the worst case for IPv6");
        Assert.That(Lookup("10.0.0.0-10.255.255.255").GetStatistics().IPv4.Prefixes, Is.EqualTo(1), "a CIDR block");

        var everything = Lookup("0.0.0.0-255.255.255.255", "::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff");
        Assert.That(everything.GetStatistics().IPv4.Prefixes, Is.EqualTo(1));
        Assert.That(everything.GetStatistics().IPv6.Prefixes, Is.EqualTo(1));
        Assert.That(Match(everything, "255.255.255.255"), Is.EqualTo("0.0.0.0-255.255.255.255"));
        Assert.That(Match(everything, "ffff::1"), Is.EqualTo("::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"));

        var single = Lookup("10.1.2.3-10.1.2.3");
        Assert.That(Match(single, "10.1.2.3"), Is.EqualTo("10.1.2.3-10.1.2.3"));
        Assert.That(Match(single, "10.1.2.4"), Is.Null);
    }

    [Test]
    public void Nested_ranges_give_the_inner_range_and_survive_removal()
    {
        // the inner range splits into a piece identical to one of the outer range's pieces: 10.0.0.0/24
        const string outer = "10.0.0.0-10.0.1.9", inner = "10.0.0.0-10.0.0.255", innermost = "10.0.0.100-10.0.0.120";
        var lookup = Lookup(outer, inner, innermost);
        foreach (var form in Forms(lookup))
        {
            Assert.That(Match(form, "10.0.0.5"), Is.EqualTo(inner));
            Assert.That(Match(form, "10.0.0.110"), Is.EqualTo(innermost));
            Assert.That(Match(form, "10.0.1.5"), Is.EqualTo(outer));
            Assert.That(form.GetMatches(IPAddress.Parse("10.0.0.110")).Select(e => e.Value), Is.EqualTo(new[] { innermost, inner, outer }));
            Assert.That(form.TryGetMatch(R("10.0.0.101-10.0.0.102"), out _, out var value) && value == innermost);
            Assert.That(form.TryGetMatch(R("10.0.0.90-10.0.0.102"), out _, out value) && value == inner);
            Assert.That(form.TryGetMatch(R("10.0.0.90-10.0.1.0"), out _, out value) && value == outer);
            Assert.That(form.TryGetMatch(R("10.0.0.90-10.0.1.10"), out _, out _), Is.False);
            Assert.That(form.Select(e => e.Value), Is.EqualTo(new[] { outer, inner, innermost }), "address order, the larger range first");
        }

        Assert.That(lookup.Remove(R(inner)), Is.True);
        Assert.That(Match(lookup, "10.0.0.5"), Is.EqualTo(outer), "the outer range is uncovered again");
        Assert.That(Match(lookup, "10.0.0.110"), Is.EqualTo(innermost));
        lookup.Add(R(inner), inner);
        Assert.That(Match(lookup, "10.0.0.5"), Is.EqualTo(inner));
        Assert.That(lookup.Remove(R(outer)), Is.True);
        Assert.That(Match(lookup, "10.0.0.5"), Is.EqualTo(inner));
        Assert.That(Match(lookup, "10.0.1.5"), Is.Null);
        Assert.That(lookup.Remove(R(outer)), Is.False);
    }

    [Test]
    public void Random_nested_ranges_match_a_brute_force_search()
    {
        // ranges that nest or do not touch, made by splitting intervals recursively
        var random = new Random(31);
        var ranges = new List<(uint First, uint Last)>();
        void Split(uint first, uint last, int depth)
        {
            if (depth == 0 || last - first < 8) return;
            var a = first + (uint)random.NextInt64(0, last - first);
            var b = a + (uint)random.NextInt64(0, last - a + 1);
            ranges.Add((a, b));
            Split(a, b, depth - 1);
            if (a > first) Split(first, a - 1, depth - 1);
            if (b < last) Split(b + 1, last, depth - 1);
        }

        Split(0, uint.MaxValue, 9);
        ranges = ranges.Distinct().ToList();
        var lookup = new IPRangeLookup<TestRange, TestRangeAdapter, int>();
        for (var i = 0; i < ranges.Count; i++)
            lookup.Add(new TestRange(IPAddressBits.FromIPv4Bits(ranges[i].First), IPAddressBits.FromIPv4Bits(ranges[i].Last)), i);

        var probes = ranges.SelectMany(r => new[] { r.First, r.Last, r.First - 1, r.Last + 1, r.First + (r.Last - r.First) / 2 }).ToList();
        for (var i = 0; i < 2000; i++) probes.Add((uint)random.NextInt64(0, 1L << 32));
        foreach (var form in new IReadOnlyIPPrefixLookup<TestRange, int>[] { lookup, lookup.Freeze(), lookup.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }) })
        {
            var failures = 0;
            foreach (var address in probes)
            {
                var containing = Enumerable.Range(0, ranges.Count).Where(i => ranges[i].First <= address && address <= ranges[i].Last)
                    .OrderBy(i => ranges[i].Last - ranges[i].First).ToList();
                var found = form.TryGetMatchValueIPv4(address, out var value);
                if (found != containing.Count > 0 || (found && value != containing[0])) failures++;
                if (!form.GetMatches(IPAddressBits.FromIPv4Bits(address)).Select(e => e.Value).SequenceEqual(containing)) failures++;
            }

            Assert.That(failures, Is.Zero, $"{ranges.Count} ranges, {probes.Count} probes");
        }

        // removing every other range
        for (var i = 0; i < ranges.Count; i += 2)
            Assert.That(lookup.Remove(new TestRange(IPAddressBits.FromIPv4Bits(ranges[i].First), IPAddressBits.FromIPv4Bits(ranges[i].Last))), Is.True);
        foreach (var address in probes)
        {
            var containing = Enumerable.Range(0, ranges.Count).Where(i => i % 2 == 1 && ranges[i].First <= address && address <= ranges[i].Last)
                .OrderBy(i => ranges[i].Last - ranges[i].First).ToList();
            var found = lookup.TryGetMatchValueIPv4(address, out var value);
            Assert.That(found == containing.Count > 0 && (!found || value == containing[0]), $"after removal, at {IPAddressBits.FromIPv4Bits(address)}");
        }
    }

    [Test]
    public void Writes_follow_dictionary_rules()
    {
        var lookup = Lookup("10.0.0.5-10.0.0.9");
        Assert.That(lookup.TryAdd(R("10.0.0.5-10.0.0.9"), "again"), Is.False);
        Assert.Throws<ArgumentException>(() => lookup.Add(R("10.0.0.5-10.0.0.9"), "again"));
        Assert.That(lookup.AddOrUpdate(R("10.0.0.5-10.0.0.9"), "updated"), Is.False);
        Assert.That(Match(lookup, "10.0.0.7"), Is.EqualTo("updated"));
        Assert.That(lookup.TryGetValue(R("10.0.0.5-10.0.0.9"), out var value) && value == "updated");
        Assert.That(lookup.ContainsPrefix(R("10.0.0.5-10.0.0.8")), Is.False);
        Assert.That(lookup.GetOrAdd(R("10.0.0.5-10.0.0.9"), _ => "new"), Is.EqualTo("updated"));
        Assert.That(lookup.GetOrAdd(R("10.0.1.0-10.0.1.0"), _ => "new"), Is.EqualTo("new"));
        Assert.That(lookup.AddOrUpdate(R("10.0.2.0-10.0.2.3"), "added"), Is.True);
        Assert.That(lookup.Count, Is.EqualTo(3));

        Assert.Throws<ArgumentException>(() => lookup.Add(R("10.0.0.9-10.0.0.5"), ""), "ends before it starts");
        Assert.Throws<ArgumentException>(() => lookup.Add(new TestRange(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("::1")), ""), "mixed families");
        Assert.Throws<ArgumentNullException>(() => lookup.Add(null!, ""));

        var version = lookup.Version;
        using (var batch = lookup.BeginBatch())
        {
            lookup.Add(R("172.16.0.1-172.16.0.200"), "batch");
            lookup.Remove(R("10.0.1.0-10.0.1.0"));
            Assert.That(Task.Run(() => lookup.Count).Result, Is.EqualTo(3));
            Assert.That(Task.Run(() => Match(lookup, "172.16.0.9")).Result, Is.Null, "invisible until committed");
            Assert.Throws<InvalidOperationException>(() => lookup.BeginBatch());
            batch.Commit();
        }

        Assert.That(lookup.Version, Is.GreaterThan(version));
        Assert.That(Match(lookup, "172.16.0.9"), Is.EqualTo("batch"));
        Assert.That(Match(lookup, "10.0.1.0"), Is.Null);

        using (lookup.BeginBatch())
        {
            lookup.Clear();
            lookup.Add(R("1.1.1.1-1.1.1.2"), "gone");
        }

        Assert.That(lookup.Count, Is.EqualTo(3), "discarded");
        Assert.That(Match(lookup, "1.1.1.1"), Is.Null);

        var frozen = lookup.Freeze();
        lookup.Clear();
        Assert.That(lookup.Count, Is.Zero);
        Assert.That(frozen.Count, Is.EqualTo(3), "the frozen copy keeps its ranges");
        Assert.That(Match(frozen, "172.16.0.9"), Is.EqualTo("batch"));
        Assert.That(frozen.TryGetValue(R("172.16.0.1-172.16.0.200"), out value) && value == "batch");
        Assert.That(frozen.Bytes, Is.GreaterThan(0));
    }

    [Test]
    public void Writes_in_a_batch_see_the_batch()
    {
        // the two ranges share the piece 10.0.0.0/24, which the second writes while the first is still unpublished
        var inner = new KeyValuePair<TestRange, string>(R("10.0.0.0-10.0.0.255"), "inner");
        var outer = new KeyValuePair<TestRange, string>(R("10.0.0.0-10.0.1.0"), "outer");
        var lookup = new IPRangeLookup<TestRange, TestRangeAdapter, string>([outer, inner]);
        foreach (var form in Forms(lookup))
        {
            Assert.That(Match(form, "10.0.0.5"), Is.EqualTo("inner"));
            Assert.That(Match(form, "10.0.1.0"), Is.EqualTo("outer"));
            Assert.That(form.GetMatches(IPAddress.Parse("10.0.0.5")).Select(m => m.Value), Is.EqualTo(new[] { "inner", "outer" }));
        }

        using (var batch = lookup.BeginBatch())
        {
            lookup.Add(R("10.0.0.0-10.0.0.127"), "added and removed");
            lookup.Remove(R("10.0.0.0-10.0.0.127"));
            lookup.Add(R("192.168.0.3-192.168.0.200"), "added");
            lookup.AddOrUpdate(R("192.168.0.3-192.168.0.200"), "updated");
            lookup.Remove(inner.Key);
            batch.Commit();
        }

        foreach (var form in Forms(lookup))
        {
            Assert.That(form.Count, Is.EqualTo(2));
            Assert.That(Match(form, "10.0.0.5"), Is.EqualTo("outer"));
            Assert.That(form.GetMatches(IPAddress.Parse("10.0.0.5")).Select(m => m.Value), Is.EqualTo(new[] { "outer" }));
            Assert.That(Match(form, "192.168.0.3"), Is.EqualTo("updated"));
            Assert.That(Match(form, "192.168.0.200"), Is.EqualTo("updated"));
        }

        using (var batch = lookup.BeginBatch())
        {
            lookup.Clear();
            lookup.Add(R("10.0.0.0-10.0.0.255"), "after clear");
            batch.Commit();
        }

        foreach (var form in Forms(lookup))
        {
            Assert.That(form.Count, Is.EqualTo(1));
            Assert.That(form.GetMatches(IPAddress.Parse("10.0.0.5")).Select(m => m.Value), Is.EqualTo(new[] { "after clear" }), "the cleared ranges stay gone");
            Assert.That(Match(form, "10.0.1.0"), Is.Null);
            Assert.That(Match(form, "192.168.0.3"), Is.Null);
        }
    }

    [Test]
    public void A_range_is_read_once()
    {
        var lookup = new IPRangeLookup<MutableRange, MutableRangeAdapter, string>();
        var outer = new MutableRange(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.1.0"));
        var inner = new MutableRange(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.0.255"));
        lookup.Add(outer, "outer");
        lookup.Add(inner, "inner");

        // changing the stored objects changes nothing in the lookup
        outer.Last = IPAddress.Parse("10.0.0.1");
        inner.First = IPAddress.Parse("192.168.0.0");
        inner.Last = IPAddress.Parse("192.168.0.255");
        Assert.That(lookup.TryGetMatch(IPAddress.Parse("10.0.0.5"), out _, out var value) ? value : null, Is.EqualTo("inner"));
        Assert.That(lookup.TryGetMatch(IPAddress.Parse("10.0.1.0"), out _, out value) ? value : null, Is.EqualTo("outer"));
        Assert.That(lookup.TryGetMatchValue(IPAddress.Parse("192.168.0.5"), out _), Is.False);

        Assert.That(lookup.Remove(new MutableRange(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.0.255"))), Is.True);
        Assert.That(lookup.TryGetMatch(IPAddress.Parse("10.0.0.5"), out _, out value) ? value : null, Is.EqualTo("outer"));
        Assert.That(lookup.Remove(new MutableRange(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.1.0"))), Is.True);
        Assert.That(lookup.Count, Is.Zero);
        Assert.That(lookup.TryGetMatchValue(IPAddress.Parse("10.0.0.5"), out _), Is.False);
    }

    [Test]
    public void A_commit_that_throws_leaves_the_listed_ranges_matching_the_found_ones()
    {
        var lookup = Lookup("10.0.0.0-10.0.0.9");

        // the IPv4 half fails: nothing is published, the IPv6 half included
        var restore = Test.Core.CommitFaults.BreakNextCommit(Test.Core.CommitFaults.Field(lookup, "_v4"));
        var batch = lookup.BeginBatch();
        lookup.Add(R("10.1.0.0-10.2.255.255"), "v4");
        lookup.Add(R("2001:db8::-2001:db8::ff"), "v6");
        Assert.Throws<NullReferenceException>(() => batch.Commit());
        restore();
        Assert.That(lookup.Select(e => e.Value), Is.EqualTo(new[] { "10.0.0.0-10.0.0.9" }));
        Assert.That(Match(lookup, "10.1.2.3"), Is.Null);
        Assert.That(Match(lookup, "2001:db8::1"), Is.Null);

        // the IPv6 half fails: the IPv4 changes stay published, and the listed ranges match
        restore = Test.Core.CommitFaults.BreakNextCommit(Test.Core.CommitFaults.Field(lookup, "_v6"));
        batch = lookup.BeginBatch();
        lookup.Add(R("10.1.0.0-10.2.255.255"), "v4");
        lookup.Add(R("2001:db8::-2001:db8:ffff:ffff:ffff:ffff:ffff:ffff"), "v6");
        lookup.Add(R("2001:db9::-2001:db9:ffff:ffff:ffff:ffff:ffff:ffff"), "v6 too");
        Assert.Throws<NullReferenceException>(() => batch.Commit());
        restore();
        Assert.That(lookup.Select(e => e.Value), Is.EqualTo(new[] { "10.0.0.0-10.0.0.9", "v4" }));
        Assert.That(Match(lookup, "10.1.2.3"), Is.EqualTo("v4"));
        Assert.That(Match(lookup, "2001:db8::1"), Is.Null);

        // and the lookup still takes writes
        lookup.Add(R("2001:db8::-2001:db8:ffff:ffff:ffff:ffff:ffff:ffff"), "later");
        Assert.That(Match(lookup, "2001:db8::1"), Is.EqualTo("later"));
        Assert.That(lookup.Count, Is.EqualTo(3));
    }

    [Test]
    public void Mapped_addresses_find_IPv4_ranges()
    {
        var lookup = Lookup("10.0.0.1-10.0.0.3", "::ffff:10.0.0.0-::ffff:10.0.0.9");
        foreach (var form in Forms(lookup))
        {
            Assert.That(Match(form, "::ffff:10.0.0.2"), Is.EqualTo("10.0.0.1-10.0.0.3"));
            Assert.That(Match(form, "::ffff:10.0.0.5"), Is.EqualTo("::ffff:10.0.0.0-::ffff:10.0.0.9"), "an IPv6 range when no IPv4 range holds it");
            Assert.That(Match(form, "10.0.0.5"), Is.Null);
            Assert.That(form.GetMatches(IPAddress.Parse("::ffff:10.0.0.2")).Select(m => m.Value), Is.EqualTo(new[] { "10.0.0.1-10.0.0.3", "::ffff:10.0.0.0-::ffff:10.0.0.9" }));
        }
    }
}
