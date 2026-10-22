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

using Bitvantage.InternetProtocol;

namespace Test.Core;

/// <summary>
///     Tests for <see cref="BitTrie{TKey,TPrefix,TValue}" /> and <see cref="FrozenBitTrie{TKey,TPrefix,TValue}" />:
///     every query against an independent reference, for every shipped key width and every frozen layout; canonical
///     shape; batches; and lock-free readers alongside a writer.
/// </summary>
public class BitTrieTests
{
    // the prefix object is the prefix's index and the value is index * 7, so a lookup's two results can be checked apart
    private static BitTrie<TKey, int, int> Build<TKey>(List<(TKey Bits, int Length)> prefixes, IEnumerable<int> order, bool batch = false)
        where TKey : struct, IBitKey<TKey>
    {
        var trie = new BitTrie<TKey, int, int>();
        var b = batch ? trie.BeginBatch() : null;
        foreach (var i in order)
            Assert.That(trie.TryAdd(prefixes[i].Bits, prefixes[i].Length, i, i * 7), Is.True);
        b?.Commit();
        return trie;
    }

    private static ReferenceTrie<TKey> Reference<TKey>(List<(TKey Bits, int Length)> prefixes, IEnumerable<int> order) where TKey : struct, IBitKey<TKey>
    {
        var reference = new ReferenceTrie<TKey>();
        foreach (var i in order)
            reference.Add(prefixes[i].Bits, prefixes[i].Length, i);
        return reference;
    }

    private static string Format<TKey>(TKey bits, int length) where TKey : struct, IBitKey<TKey> => $"{bits}/{length}";

    public static readonly FrozenBitTrieOptions[] Layouts =
    {
        new(),
        new() { RootBits = 20, TableBits = 4 },
        new() { RootBits = 4, TableBits = 6, ValueOnlyLookups = false },
        new() { RootBits = 1, TableBits = 16 },
        new() { Layout = FrozenLayout.Compressed },
        new() { Layout = FrozenLayout.Compressed, RootBits = 8 },
        new() { Layout = FrozenLayout.Compressed, RootBits = 1, ValueOnlyLookups = false }
    };

    /// <summary>Checks every read-only query of a trie against the reference, at every probe key.</summary>
    private static void AssertQueries<TKey>(IReadOnlyBitTrie<TKey, int, int> trie, ReferenceTrie<TKey> reference, List<(TKey Bits, int Length)> prefixes, List<TKey> keys, string because)
        where TKey : struct, IBitKey<TKey>
    {
        var failures = new List<string>();
        var span = new PrefixEntry<int, int>[TKey.Width + 1];
        foreach (var key in keys)
        {
            var expected = reference.Matches(key, TKey.Width);
            var found = trie.TryGetMatch(in key, out var prefix, out var value);
            if (found != expected.Count > 0 || (found && (prefix != expected[0].Value || value != prefix * 7)))
                failures.Add($"TryGetMatch({key}) gave {(found ? prefix : -1)}, expected {(expected.Count > 0 ? expected[0].Value : -1)}");

            found = trie.TryGetMatchValue(in key, out value);
            if (found != expected.Count > 0 || (found && value != expected[0].Value * 7))
                failures.Add($"TryGetMatchValue({key})");

            var all = trie.GetMatches(in key).Select(e => e.Prefix).ToList();
            if (!all.SequenceEqual(expected.Select(e => e.Value)))
                failures.Add($"GetMatches({key}) gave [{string.Join(", ", all)}], expected [{string.Join(", ", expected.Select(e => e.Value))}]");
            var count = trie.GetMatches(in key, span);
            if (!span.Take(count).Select(e => e.Prefix).SequenceEqual(expected.Select(e => e.Value)))
                failures.Add($"GetMatches({key}, span)");
        }

        // prefix queries, at the stored prefixes and at prefixes one bit longer and shorter
        foreach (var (bits, length) in prefixes)
            foreach (var l in new[] { length - 1, length, length + 1 })
            {
                if (l < 0 || l > TKey.Width) continue;
                var expected = reference.Matches(bits, l);
                var found = trie.TryGetMatch(in bits, l, out var prefix, out var value);
                if (found != expected.Count > 0 || (found && (prefix != expected[0].Value || value != prefix * 7)))
                    failures.Add($"TryGetMatch({bits}/{l}) gave {(found ? prefix : -1)}, expected {(expected.Count > 0 ? expected[0].Value : -1)}");

                var exact = reference.TryGetValue(bits, l, out var expectedValue);
                if (trie.ContainsPrefix(in bits, l) != exact)
                    failures.Add($"ContainsPrefix({bits}/{l})");
                if (trie.TryGetValue(in bits, l, out value) != exact || (exact && value != expectedValue * 7))
                    failures.Add($"TryGetValue({bits}/{l})");
                if (trie.TryGetValue(in bits, l, out prefix, out value) != exact || (exact && (prefix != expectedValue || value != expectedValue * 7)))
                    failures.Add($"TryGetValue({bits}/{l}) with the prefix object");
            }

        Assert.That(trie.Count, Is.EqualTo(reference.Count), because);
        Assert.That(trie.Select(e => e.Prefix), Is.EqualTo(reference.InOrder().Select(e => e.Value)), because + ": enumeration order");
        Assert.That(failures, Is.Empty, $"{because}: {failures.Count} failures, the first: {failures.FirstOrDefault()}");
    }

    private static void AssertAllForms<TKey>(int count, int seed) where TKey : struct, IBitKey<TKey>
    {
        var prefixes = TestKeys.RandomPrefixes<TKey>(count, seed);
        var keys = TestKeys.ProbeKeys(prefixes, seed + 1);
        var all = Enumerable.Range(0, prefixes.Count).ToList();
        var reference = Reference(prefixes, all);

        var trie = Build(prefixes, all);
        AssertQueries(trie, reference, prefixes, keys, "one at a time");
        AssertQueries(Build(prefixes, all, batch: true), reference, prefixes, keys, "batch");
        var built = new BitTrie<TKey, int, int>(all.Select(i => (prefixes[i].Bits, prefixes[i].Length, i, i * 7)));
        AssertQueries(built, reference, prefixes, keys, "built from a sequence");
        Assert.That(built.ToTextTree(Format), Is.EqualTo(trie.ToTextTree(Format)), "built from a sequence: shape");
        foreach (var layout in Layouts)
            AssertQueries(trie.Freeze(layout), reference, prefixes, keys, $"frozen {layout}");

        // remove a third, then check again
        foreach (var i in all.Where(i => i % 3 == 0))
        {
            Assert.That(trie.Remove(prefixes[i].Bits, prefixes[i].Length), Is.True);
            reference.Remove(prefixes[i].Bits, prefixes[i].Length);
        }

        AssertQueries(trie, reference, prefixes, keys, "after removal");
        AssertQueries(trie.Freeze(), reference, prefixes, keys, "frozen after removal");

        // a trie built from a sequence takes later writes as one built by them
        foreach (var i in all.Where(i => i % 3 == 0))
            Assert.That(built.Remove(prefixes[i].Bits, prefixes[i].Length), Is.True);
        AssertQueries(built, reference, prefixes, keys, "built from a sequence, after removal");
        Assert.That(built.ToTextTree(Format), Is.EqualTo(trie.ToTextTree(Format)), "built from a sequence, after removal: shape");
    }

    [Test(Description = "8-bit keys have no shortcut blocks, so lookups start at the root; an 8-bit key holds at most 511 prefixes.")]
    public void Bits8_queries_match_the_reference() => AssertAllForms<Bits8>(300, 8);

    [Test]
    public void Bits16_queries_match_the_reference() => AssertAllForms<Bits16>(3000, 16);

    [Test]
    public void Bits32_queries_match_the_reference() => AssertAllForms<Bits32>(5000, 32);

    [Test]
    public void Bits128_queries_match_the_reference() => AssertAllForms<Bits128>(3000, 128);

    [Test]
    public void Keys_wider_than_128_bits_are_refused() =>
        Assert.Throws<NotSupportedException>(() => _ = new BitTrie<Bits136, int, int>());

    [Test]
    public void Insertion_order_does_not_change_the_trie()
    {
        var prefixes = TestKeys.RandomPrefixes<Bits32>(5000, 1);
        var all = Enumerable.Range(0, prefixes.Count).ToList();
        var random = new Random(7);
        var expected = Build(prefixes, all).ToTextTree(Format);
        Assert.That(Build(prefixes, all.OrderBy(_ => random.Next())).ToTextTree(Format), Is.EqualTo(expected), "shuffled");
        Assert.That(Build(prefixes, Enumerable.Reverse(all)).ToTextTree(Format), Is.EqualTo(expected), "reversed");
        Assert.That(Build(prefixes, all, batch: true).ToTextTree(Format), Is.EqualTo(expected), "batch");

        // built from a sequence in any order: the same shape, and the same nodes and arrays as after a batch
        BitTrie<Bits32, int, int> FromSequence(IEnumerable<int> order) => new(order.Select(i => (prefixes[i].Bits, prefixes[i].Length, i, i * 7)));
        var sorted = all.OrderBy(i => prefixes[i].Bits.Value).ThenBy(i => prefixes[i].Length).ToList();
        foreach (var (order, name) in new[] { (sorted, "sorted"), (all.OrderBy(_ => random.Next()).ToList(), "shuffled"), (Enumerable.Reverse(sorted).ToList(), "reversed") })
        {
            var built = FromSequence(order);
            Assert.That(built.ToTextTree(Format), Is.EqualTo(expected), $"built from a sequence, {name}");
            Assert.That(built.GetStatistics(), Is.EqualTo(Build(prefixes, all, batch: true).GetStatistics()), $"built from a sequence, {name}: statistics");
        }
    }

    [Test]
    public void Building_from_a_sequence_checks_its_prefixes()
    {
        Assert.Throws<ArgumentException>(() => _ = new BitTrie<Bits32, int, int>(new[] { (new Bits32(0x0A000000), 8, 1, 1), (new Bits32(0x0A000000), 8, 2, 2) }), "a duplicate");
        Assert.Throws<ArgumentException>(() => _ = new BitTrie<Bits32, int, int>(new[] { (new Bits32(0x0A010203), 24, 1, 1), (new Bits32(0x0A0102FF), 24, 2, 2) }), "a duplicate with other host bits");
        Assert.Throws<ArgumentException>(() => _ = new BitTrie<Bits128, int, int>(new[] { (new Bits128(5), 128, 1, 1), (new Bits128(7), 64, 2, 2), (new Bits128(5), 128, 3, 3) }), "a duplicate full-width prefix");
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new BitTrie<Bits32, int, int>(new[] { (new Bits32(0), 33, 1, 1) }));

        var empty = new BitTrie<Bits32, int, int>(Array.Empty<(Bits32, int, int, int)>());
        Assert.That(empty.Count, Is.Zero);
        Assert.That(empty.TryAdd(new Bits32(0x0A000000), 8, 1, 1));
        Assert.That(empty.TryGetMatchValue(new Bits32(0x0A010203), out var value) && value == 1);

        var single = new BitTrie<Bits32, int, int>(new[] { (new Bits32(0xC0A80101), 32, 1, 1) });
        Assert.That(single.Count, Is.EqualTo(1));
        Assert.That(single.TryGetMatchValue(new Bits32(0xC0A80101), out value) && value == 1);
        Assert.That(single.TryGetMatchValue(new Bits32(0xC0A80102), out _), Is.False);
    }

    [Test]
    public void Removing_prefixes_gives_the_same_trie_as_never_adding_them()
    {
        var prefixes = TestKeys.RandomPrefixes<Bits128>(5000, 2);
        var all = Enumerable.Range(0, prefixes.Count).ToList();
        var kept = all.Where(i => i % 5 != 0).ToList();
        var trie = Build(prefixes, all);
        foreach (var i in all.Where(i => i % 5 == 0))
            Assert.That(trie.Remove(prefixes[i].Bits, prefixes[i].Length), Is.True);
        Assert.That(trie.ToTextTree(Format), Is.EqualTo(Build(prefixes, kept).ToTextTree(Format)));

        // and in a batch
        trie = Build(prefixes, all);
        using (var batch = trie.BeginBatch())
        {
            foreach (var i in all.Where(i => i % 5 == 0))
                Assert.That(trie.Remove(prefixes[i].Bits, prefixes[i].Length), Is.True);
            batch.Commit();
        }

        Assert.That(trie.ToTextTree(Format), Is.EqualTo(Build(prefixes, kept).ToTextTree(Format)), "batch");

        foreach (var i in kept)
            Assert.That(trie.Remove(prefixes[i].Bits, prefixes[i].Length), Is.True);
        Assert.That(trie.Count, Is.Zero);
        Assert.That(trie.ToTextTree(Format), Is.Empty);
        Assert.That(trie.TryGetMatch(prefixes[0].Bits, out _, out _), Is.False);
    }

    [Test]
    public void The_text_tree_shows_nodes_prefixes_fringes_and_leaves()
    {
        var trie = new BitTrie<Bits32, string, int>();
        foreach (var (bits, length) in new[] { (0x0A000000u, 8), (0x0A010000u, 16), (0x0A010100u, 24), (0x0A010200u, 24), (0x0A000000u, 7), (0xC0A80000u, 16), (0u, 0) })
            trie.Add(new Bits32(bits), length, "", 0);

        var expected = string.Join(Environment.NewLine,
            "prefix 00000000/0",
            "prefix 0a000000/7",
            "fringe 0a000000/8",
            "node 0a000000/8, 3 prefixes",
            "  fringe 0a010000/16",
            "  node 0a010000/16, 2 prefixes",
            "    fringe 0a010100/24",
            "    fringe 0a010200/24",
            "leaf c0a80000/16",
            "");
        Assert.That(trie.ToTextTree(Format), Is.EqualTo(expected));
        Assert.That(trie.ToDotTree(Format), Does.StartWith("digraph trie {"));
    }

    [Test]
    public void Writes_follow_dictionary_rules()
    {
        var trie = new BitTrie<Bits32, string, int>();
        var bits = new Bits32(0x0A000000);
        Assert.That(trie.TryAdd(bits, 8, "a", 1), Is.True);
        Assert.That(trie.TryAdd(bits, 8, "b", 2), Is.False);
        Assert.Throws<ArgumentException>(() => trie.Add(bits, 8, "b", 2));
        Assert.That(trie.TryGetValue(bits, 8, out var value) && value == 1);

        Assert.That(trie.AddOrUpdate(bits, 8, "c", 3), Is.False, "replaced");
        Assert.That(trie.TryGetMatch(new Bits32(0x0A010203), out var prefix, out value) && prefix == "c" && value == 3);
        Assert.That(trie.AddOrUpdate(new Bits32(0x0A0000FF), 9, "d", 4), Is.True, "added; bits after the length are ignored");
        Assert.That(trie.ContainsPrefix(new Bits32(0x0A000000), 9));

        var calls = 0;
        Assert.That(trie.GetOrAdd(bits, 8, "e", _ => { calls++; return 5; }), Is.EqualTo(3));
        Assert.That(trie.GetOrAdd(bits, 16, "f", p => { calls++; return p.Length; }), Is.EqualTo(1));
        Assert.That(calls, Is.EqualTo(1));

        Assert.That(trie.Remove(bits, 24), Is.False);
        Assert.That(trie.Remove(bits, 16), Is.True);
        Assert.That(trie.Count, Is.EqualTo(2));
        trie.Clear();
        Assert.That(trie.Count, Is.Zero);
        Assert.That(trie.TryGetMatch(bits, out _, out _), Is.False);

        Assert.Throws<ArgumentOutOfRangeException>(() => trie.TryAdd(bits, 33, "", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => trie.TryGetMatch(bits, -1, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => trie.Freeze().ContainsPrefix(bits, 33));
    }

    [Test]
    public void Lookups_backtrack_any_number_of_levels()
    {
        // 10.1.2.64/26 and 10.1.2.128/25 make nodes at depths 1, 2 and 3 on the path of 10.1.2.3, which neither contains,
        // so each prefix added after them answers it from one level nearer the deepest node
        var trie = new BitTrie<Bits32, string, int>();
        trie.Add(new Bits32(0x0A010240), 26, "/26", 0);
        trie.Add(new Bits32(0x0A010280), 25, "/25", 0);
        var key = new Bits32(0x0A010203);
        Assert.That(trie.TryGetMatch(key, out _, out _), Is.False, "no match");

        foreach (var (bits, length, where) in new[] { (0x0A000000u, 8, "three levels up"), (0x0A010000u, 16, "two levels up"), (0x0A010200u, 24, "one level up"), (0x0A010200u, 30, "the deepest node") })
        {
            trie.Add(new Bits32(bits), length, $"/{length}", 0);
            Assert.That(trie.TryGetMatch(key, out var prefix, out _) ? prefix : null, Is.EqualTo($"/{length}"), where);
            Assert.That(trie.Freeze().TryGetMatch(key, out prefix, out _) ? prefix : null, Is.EqualTo($"/{length}"), where + ", frozen");
        }
    }

    [Test]
    public void Edge_prefixes_are_found()
    {
        var trie = new BitTrie<Bits128, int, int>();
        trie.Add(default, 0, 0, 0);
        trie.Add(new Bits128(UInt128.MaxValue), 128, 128, 0);
        trie.Add(new Bits128(UInt128.One), 128, 1, 0);
        trie.Add(new Bits128(UInt128.MaxValue << 1), 127, 127, 0);
        foreach (var t in new IReadOnlyBitTrie<Bits128, int, int>[] { trie, trie.Freeze(), trie.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }) })
        {
            Assert.That(t.TryGetMatch(new Bits128(UInt128.MaxValue), out var p, out _) && p == 128);
            Assert.That(t.TryGetMatch(new Bits128(UInt128.MaxValue - 1), out p, out _) && p == 127);
            Assert.That(t.TryGetMatch(new Bits128(UInt128.One), out p, out _) && p == 1);
            Assert.That(t.TryGetMatch(new Bits128(2), out p, out _) && p == 0);
            Assert.That(t.GetMatches(new Bits128(UInt128.MaxValue)).Select(e => e.Prefix), Is.EqualTo(new[] { 128, 127, 0 }));
        }

        var empty = new BitTrie<Bits32, int, int>();
        Assert.That(empty.TryGetMatch(default, out _, out _), Is.False);
        Assert.That(empty.Freeze().TryGetMatch(default, out _, out _), Is.False);
        Assert.That(empty.Freeze().Count, Is.Zero);
    }

    [Test]
    public void A_destination_span_that_is_too_small_throws()
    {
        var trie = new BitTrie<Bits32, int, int>();
        trie.Add(default, 0, 0, 0);
        trie.Add(default, 8, 8, 0);
        Assert.Throws<ArgumentException>(() => trie.GetMatches(default, new PrefixEntry<int, int>[1]));
        Assert.Throws<ArgumentException>(() => trie.Freeze().GetMatches(default, new PrefixEntry<int, int>[1]));
    }

    [Test]
    public void A_key_inside_every_length_has_width_plus_one_matches()
    {
        // more matches than the four the list form makes room for at first
        var trie = new BitTrie<Bits32, int, int>();
        for (var length = 0; length <= 32; length++)
            trie.Add(default, length, length, 0);
        var longestFirst = Enumerable.Range(0, 33).Reverse().ToArray();
        foreach (var form in new IReadOnlyBitTrie<Bits32, int, int>[] { trie, trie.Freeze() })
        {
            Assert.That(form.GetMatches(default).Select(e => e.Prefix), Is.EqualTo(longestFirst));
            var span = new PrefixEntry<int, int>[33];
            Assert.That(form.GetMatches(default, span), Is.EqualTo(33));
            Assert.That(span.Select(e => e.Prefix), Is.EqualTo(longestFirst));
            Assert.Throws<ArgumentException>(() => form.GetMatches(default, new PrefixEntry<int, int>[32]));
        }
    }

    [Test]
    public void Frozen_options_are_checked()
    {
        var trie = new BitTrie<Bits32, int, int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => trie.Freeze(new FrozenBitTrieOptions { RootBits = 25 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => trie.Freeze(new FrozenBitTrieOptions { TableBits = 0 }));
        Assert.Throws<ArgumentException>(() => trie.Freeze(new FrozenBitTrieOptions { Layout = (FrozenLayout)7 }));
        Assert.That(new BitTrie<Bits16, int, int>().Freeze(new FrozenBitTrieOptions { RootBits = 24 }).Options.RootBits, Is.EqualTo(24), "kept as given, used as 16");
    }

    [Test]
    public void Readers_see_nothing_of_a_batch_until_it_commits()
    {
        var prefixes = TestKeys.RandomPrefixes<Bits32>(4000, 3);
        var all = Enumerable.Range(0, prefixes.Count).ToList();
        var first = all.Where(i => i % 2 == 0).ToList();
        var keys = TestKeys.ProbeKeys(prefixes, 4);
        var trie = Build(prefixes, first);
        var before = Reference(prefixes, first);
        var version = trie.Version;

        var batch = trie.BeginBatch();
        foreach (var i in all.Where(i => i % 2 == 1))
            trie.Add(prefixes[i].Bits, prefixes[i].Length, i, i * 7);
        foreach (var i in first.Where(i => i % 3 == 0))
            trie.Remove(prefixes[i].Bits, prefixes[i].Length);

        // checked from another thread, which does not hold the writer lock
        Task.Run(() => AssertQueries(trie, before, prefixes, keys, "during the batch")).Wait();
        Assert.That(trie.Version, Is.EqualTo(version));
        Assert.Throws<InvalidOperationException>(() => trie.BeginBatch());
        batch.Commit();
        Assert.Throws<ObjectDisposedException>(() => batch.Commit());

        var after = all.Where(i => i % 2 == 1 || i % 3 != 0).ToList();
        AssertQueries(trie, Reference(prefixes, after), prefixes, keys, "after the batch");
        Assert.That(trie.ToTextTree(Format), Is.EqualTo(Build(prefixes, after).ToTextTree(Format)));
        Assert.That(trie.Version, Is.EqualTo(version + 1), "one change for the whole batch");
    }

    [Test]
    public void A_discarded_batch_leaves_no_trace()
    {
        var prefixes = TestKeys.RandomPrefixes<Bits32>(4000, 5);
        var all = Enumerable.Range(0, prefixes.Count).ToList();
        var first = all.Where(i => i % 2 == 0).ToList();
        var trie = Build(prefixes, first);
        var expected = trie.ToTextTree(Format);

        using (trie.BeginBatch())
        {
            foreach (var i in all.Where(i => i % 2 == 1))
                trie.Add(prefixes[i].Bits, prefixes[i].Length, i, i * 7);
            foreach (var i in first.Where(i => i % 3 == 0))
                trie.Remove(prefixes[i].Bits, prefixes[i].Length);
            trie.Clear();
            trie.Add(prefixes[1].Bits, prefixes[1].Length, 1, 7);
        }

        Assert.That(trie.ToTextTree(Format), Is.EqualTo(expected));
        Assert.That(trie.Count, Is.EqualTo(first.Count));

        // the published nodes were not changed in place, so the trie still works normally afterwards
        foreach (var i in all.Where(i => i % 2 == 1))
            trie.Add(prefixes[i].Bits, prefixes[i].Length, i, i * 7);
        AssertQueries(trie, Reference(prefixes, all), prefixes, TestKeys.ProbeKeys(prefixes, 6), "after the discarded batch");
    }

    [Test]
    public void Version_changes_with_each_published_change()
    {
        var trie = new BitTrie<Bits32, int, int>();
        var v = trie.Version;
        trie.Add(default, 8, 0, 0);
        Assert.That(trie.Version, Is.GreaterThan(v));
        v = trie.Version;
        trie.TryAdd(default, 8, 0, 0);
        trie.Remove(default, 9);
        Assert.That(trie.Version, Is.EqualTo(v), "no change");
        trie.AddOrUpdate(default, 8, 1, 1);
        Assert.That(trie.Version, Is.GreaterThan(v));
        v = trie.Version;
        trie.Clear();
        Assert.That(trie.Version, Is.GreaterThan(v));
    }

    [Test]
    public void Statistics_count_the_nodes()
    {
        var prefixes = TestKeys.RandomPrefixes<Bits32>(2000, 8);
        var trie = Build(prefixes, Enumerable.Range(0, prefixes.Count));
        var statistics = trie.GetStatistics();
        Assert.That(statistics.Prefixes, Is.EqualTo(prefixes.Count));
        Assert.That(statistics.Nodes, Is.GreaterThan(1));
        Assert.That(statistics.Bytes, Is.GreaterThan(50_000));
        Assert.That(trie.Freeze().Bytes, Is.GreaterThan(0));
    }

    [Test]
    public void Small_tries_stay_small()
    {
        // the shortcut table holds blocks only below first octets with a node at depth 1, and a frozen root grows with
        // the number of prefixes
        var empty = new BitTrie<Bits128, int, int>();
        Assert.That(empty.GetStatistics().Bytes, Is.LessThan(4_000));
        Assert.That(empty.Freeze().Bytes, Is.LessThan(2_000));
        Assert.That(empty.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }).Bytes, Is.LessThan(2_000));

        var small = new BitTrie<Bits32, int, int>();
        foreach (var (bits, length) in TestKeys.RandomPrefixes<Bits32>(20, 9))
            small.Add(bits, length, 0, 0);
        Assert.That(small.GetStatistics().Bytes, Is.LessThan(40_000));
        // leaf-pushed tables give each scattered long prefix its own child tables, about 2 KB each
        Assert.That(small.Freeze().Bytes, Is.LessThan(100_000));
        Assert.That(small.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }).Bytes, Is.LessThan(20_000));

        var large = Build(TestKeys.RandomPrefixes<Bits32>(20_000, 10), Enumerable.Range(0, 20_000));
        Assert.That(large.Freeze().Bytes, Is.GreaterThan(1L << 18), "a full 16-bit root from 32 prefixes");
    }

    [Test]
    public void The_frozen_layout_follows_the_data_unless_it_is_given()
    {
        // /24 prefixes clustered in 79 /16 blocks, as in a routing table: about one child-table entry per prefix
        var clustered = new BitTrie<Bits32, int, int>();
        for (var i = 0; i < 20_000; i++)
            clustered.Add(new Bits32(0x0A000000u | ((uint)i << 8)), 24, i, i);
        Assert.That(clustered.Freeze().Layout, Is.EqualTo(FrozenLayout.LeafPushed));
        Assert.That(clustered.Freeze(new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed }).Layout, Is.EqualTo(FrozenLayout.Compressed), "a layout given is kept");

        // scattered host routes: each needs its own chain of child tables, about 3,500 entries
        var random = new Random(3);
        var scattered = new BitTrie<Bits128, int, int>();
        var keys = new List<Bits128>();
        for (var i = 0; i < 2_000; i++)
        {
            var key = new Bits128(((UInt128)(ulong)random.NextInt64() << 64) | (ulong)random.NextInt64());
            if (scattered.TryAdd(key, 128, i, i)) keys.Add(key);
        }

        var chosen = scattered.Freeze();
        Assert.That(chosen.Layout, Is.EqualTo(FrozenLayout.Compressed));
        Assert.That(chosen.Options.Layout, Is.Null);
        foreach (var key in keys)
            Assert.That(chosen.TryGetMatchValue(key, out var value) && scattered.TryGetMatchValue(key, out var expected) && value == expected);

        // the same scattering in a table small enough to stay in the cache keeps the faster layout
        var small = new BitTrie<Bits128, int, int>(keys.Take(500).Select((k, i) => (k, 128, i, i)));
        Assert.That(small.Freeze().Layout, Is.EqualTo(FrozenLayout.LeafPushed));
    }

    [Test(Description = "The default frozen root follows the number of prefixes; root entries take 4 bytes.")]
    public void Frozen_roots_follow_the_number_of_prefixes()
    {
        static BitTrie<Bits32, int, int> Octets(int count)
        {
            var trie = new BitTrie<Bits32, int, int>();
            for (var i = 0; i < count; i++)
                trie.Add(new Bits32((uint)i << 24), 8, i, i);
            return trie;
        }

        static BitTrie<Bits32, int, int> Random24s(int count)
        {
            var trie = new BitTrie<Bits32, int, int>();
            var random = new Random(41);
            while (trie.Count < count)
                trie.TryAdd(new Bits32((uint)random.Next() << 8), 24, 0, 0);
            return trie;
        }

        // leaf-pushed: an 8-bit root below 32 prefixes, the full 16 bits from 32
        Assert.That(Octets(31).Freeze().Bytes, Is.LessThan(16 << 10));
        Assert.That(Octets(32).Freeze().Bytes, Is.GreaterThanOrEqualTo(4 << 16));

        // compressed: about 16 root entries per prefix, from 8 bits up to 18
        var compressed = new FrozenBitTrieOptions { Layout = FrozenLayout.Compressed };
        Assert.That(Random24s(10).Freeze(compressed).Bytes, Is.LessThan(16 << 10), "8 bits");
        var thousand = Random24s(1000).Freeze(compressed).Bytes;
        Assert.That(thousand, Is.GreaterThanOrEqualTo(4 << 14), "14 bits");
        Assert.That(thousand, Is.LessThan(4 << 18), "less than the full 18 bits");
    }

    [Test(Description = "Readers always find a /32 that is never removed, while a writer adds and removes every prefix on its path.")]
    public void Readers_take_no_locks_and_never_see_a_half_made_change()
    {
        var trie = new BitTrie<Bits32, int, int>();
        var path = new List<(Bits32, int)>();
        for (var length = 0; length < 32; length++)
        {
            path.Add((new Bits32(0), length));
            if (length > 0) path.Add((new Bits32(1u << (32 - length)), length)); // the sibling at each level
        }

        trie.Add(new Bits32(0), 32, -1, -1);
        long lookups = 0, failures = 0;
        var random = new Random(9);
        WithReaders(1, _ =>
        {
            if (!trie.TryGetMatch(new Bits32(0), out var prefix, out _) || prefix != -1)
                Interlocked.Increment(ref failures);
            lookups++;
        }, () =>
        {
            for (var round = 0; round < 300; round++)
            {
                foreach (var (bits, length) in path.OrderBy(_ => random.Next()))
                    trie.Add(bits, length, length, length);
                using (var batch = trie.BeginBatch())
                {
                    foreach (var (bits, length) in path.Where(_ => random.Next(2) == 0))
                        trie.Remove(bits, length);
                    batch.Commit();
                }

                foreach (var (bits, length) in path)
                    trie.Remove(bits, length);
            }
        });

        Assert.That(failures, Is.Zero, $"{lookups:N0} lookups");
        Assert.That(trie.Count, Is.EqualTo(1));
    }

    [Test(Description = "Several readers check every lookup for a valid answer while a writer churns half of the prefixes.")]
    public void Lookups_stay_valid_under_churn()
    {
        var prefixes = TestKeys.RandomPrefixes<Bits32>(20000, 10);
        var stable = Enumerable.Range(0, prefixes.Count).Where(i => i % 2 == 0).ToList();
        var churned = Enumerable.Range(0, prefixes.Count).Where(i => i % 2 == 1).ToList();
        var trie = Build(prefixes, stable);
        var reference = Reference(prefixes, stable);
        var keys = TestKeys.ProbeKeys(prefixes, 11).ToArray();
        var expected = keys.Select(k => reference.Matches(k, 32) is { Count: > 0 } m ? m[0].Length : -1).ToArray();

        long failures = 0;
        var positions = new int[3];
        var random = new Random(12);
        WithReaders(3, r =>
        {
            var at = positions[r] = (positions[r] + 1) % keys.Length;
            // the answer is a stable prefix at least as long as the stable answer, or a churned one longer still, and
            // it must contain the key
            if (trie.TryGetMatch(keys[at], out var prefix, out var value))
            {
                var (bits, length) = prefixes[prefix];
                if (value != prefix * 7 || length < expected[at] || !Bits32.MatchesUnderMask(keys[at], bits, Bits32.MaskFor(length)))
                    Interlocked.Increment(ref failures);
            }
            else if (expected[at] >= 0)
            {
                Interlocked.Increment(ref failures);
            }
        }, () =>
        {
            for (var round = 0; round < 20; round++)
            {
                foreach (var i in churned.OrderBy(_ => random.Next()))
                    trie.Add(prefixes[i].Bits, prefixes[i].Length, i, i * 7);
                foreach (var i in churned.OrderBy(_ => random.Next()))
                    trie.Remove(prefixes[i].Bits, prefixes[i].Length);
            }
        });

        Assert.That(failures, Is.Zero);
        Assert.That(trie.ToTextTree(Format), Is.EqualTo(Build(prefixes, stable).ToTextTree(Format)));
    }

    [Test(Description = "A writer adds a covering prefix before it removes a longer one, and back again. Readers, more than there are processors so that some pause in the middle of a lookup, must find one of the two whichever writes they overlap.")]
    public void A_lookup_that_overlaps_several_writes_sees_one_state()
    {
        // 10.0.0.0/8 is a fringe of the root and 10.1.0.0/16 a fringe of the node below octet 10, which the two other
        // prefixes keep in place, so the writes alternate between two nodes; every state holds one of the two
        var trie = new BitTrie<Bits32, int, int>();
        var (covering, specific) = (new Bits32(0x0A000000), new Bits32(0x0A010000));
        trie.Add(new Bits32(0x0A020000), 16, 2, 2);
        trie.Add(new Bits32(0x0A030000), 16, 3, 3);
        trie.Add(specific, 16, 1, 1);
        var key = new Bits32(0x0A010203);
        var readers = Environment.ProcessorCount * 4;
        var matches = Enumerable.Range(0, readers).Select(_ => new PrefixEntry<int, int>[33]).ToArray();
        long failures = 0;
        WithReaders(readers, r =>
        {
            if (!trie.TryGetMatchValue(key, out _) || !trie.TryGetMatch(key, 24, out _, out _) || trie.GetMatches(key, matches[r]) == 0)
                Interlocked.Increment(ref failures);
        }, () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1000)
            {
                trie.Add(covering, 8, 0, 0);
                trie.Remove(specific, 16);
                trie.Add(specific, 16, 1, 1);
                trie.Remove(covering, 8);
            }
        });

        Assert.That(failures, Is.Zero);
    }

    [Test]
    public void A_commit_that_throws_publishes_nothing_and_ends_the_batch()
    {
        // two /16s below octet 10 make a node at depth 1, whose block the failing commit tries to rebuild
        var trie = new BitTrie<Bits32, int, int>();
        trie.Add(new Bits32(0x0A010000), 16, 1, 1);
        trie.Add(new Bits32(0x0A020000), 16, 2, 2);
        var version = trie.Version;

        var restore = CommitFaults.BreakNextCommit(trie);
        var batch = trie.BeginBatch();
        trie.Add(new Bits32(0x0A030000), 16, 3, 3);
        Assert.Throws<NullReferenceException>(() => batch.Commit());
        restore();

        Assert.That(trie.Count, Is.EqualTo(2), "nothing published");
        Assert.That(trie.ContainsPrefix(new Bits32(0x0A030000), 16), Is.False);
        Assert.That(trie.Version, Is.EqualTo(version));
        Assert.Throws<ObjectDisposedException>(() => batch.Commit());

        // out of batch mode: a single write publishes, and a new batch works
        trie.Add(new Bits32(0x0B000000), 8, 4, 4);
        Assert.That(trie.TryGetMatchValue(new Bits32(0x0B010101), out var value) && value == 4);
        using (var again = trie.BeginBatch())
        {
            trie.Add(new Bits32(0x0A030000), 16, 3, 3);
            again.Commit();
        }

        Assert.That(trie.TryGetMatchValue(new Bits32(0x0A030201), out value) && value == 3);
        Assert.That(trie.Count, Is.EqualTo(4));
    }

    [Test]
    public void A_batch_ended_on_another_thread_is_refused_and_stays_open()
    {
        var trie = new BitTrie<Bits32, int, int>();
        using var owner = new DedicatedThread();
        var batch = owner.Run(() =>
        {
            var started = trie.BeginBatch();
            trie.Add(new Bits32(0x0A000000), 8, 1, 1);
            return started;
        });

        Assert.Throws<InvalidOperationException>(() => batch.Commit());
        Assert.Throws<InvalidOperationException>(() => batch.Dispose());
        Assert.That(trie.Count, Is.Zero, "nothing published");

        owner.Run(() => batch.Commit());
        Assert.That(trie.Count, Is.EqualTo(1));
        trie.Add(new Bits32(0x0B000000), 8, 2, 2);
        Assert.That(trie.Count, Is.EqualTo(2), "the lock was released");
    }

    [Test]
    public void A_batch_in_which_a_write_threw_is_discarded_when_committed()
    {
        var trie = new BitTrie<FaultyBits32, int, int>();
        trie.Add(new FaultyBits32(0x0A000000), 8, 1, 1);
        var version = trie.Version;
        using (var batch = trie.BeginBatch())
        {
            trie.Add(new FaultyBits32(0x0B000000), 8, 2, 2);
            FaultyBits32.Fail = true;
            try
            {
                Assert.Throws<InjectedFault>(() => trie.Add(new FaultyBits32(0x0C000000), 8, 3, 3));
            }
            finally
            {
                FaultyBits32.Fail = false;
            }

            Assert.Throws<InvalidOperationException>(() => batch.Commit());
            Assert.Throws<ObjectDisposedException>(() => batch.Commit());
        }

        Assert.That(trie.Count, Is.EqualTo(1), "nothing published");
        Assert.That(trie.Version, Is.EqualTo(version));
        Assert.That(trie.ContainsPrefix(new FaultyBits32(0x0B000000), 8), Is.False);

        // a write that throws outside a batch changes nothing, and the next batch commits
        FaultyBits32.Fail = true;
        try
        {
            Assert.Throws<InjectedFault>(() => trie.Remove(new FaultyBits32(0x0A000000), 8));
        }
        finally
        {
            FaultyBits32.Fail = false;
        }

        using (var batch = trie.BeginBatch())
        {
            trie.Add(new FaultyBits32(0x0B000000), 8, 2, 2);
            batch.Commit();
        }

        Assert.That(trie.Count, Is.EqualTo(2));
    }

    [Test]
    public void Writes_from_several_threads_are_serialized()
    {
        var prefixes = TestKeys.RandomPrefixes<Bits128>(8000, 13);
        var trie = new BitTrie<Bits128, int, int>();
        Parallel.For(0, prefixes.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => trie.Add(prefixes[i].Bits, prefixes[i].Length, i, i * 7));
        Assert.That(trie.Count, Is.EqualTo(prefixes.Count));
        Assert.That(trie.ToTextTree(Format), Is.EqualTo(Build(prefixes, Enumerable.Range(0, prefixes.Count)).ToTextTree(Format)));
    }

    // Runs read on background threads, each over and over with its own index, until write returns or throws.
    private static void WithReaders(int count, Action<int> read, Action write)
    {
        var stop = false;
        var readers = Enumerable.Range(0, count).Select(r => new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
                read(r);
        }) { IsBackground = true }).ToList();
        readers.ForEach(t => t.Start());
        try
        {
            write();
        }
        finally
        {
            Volatile.Write(ref stop, true);
            readers.ForEach(t => t.Join());
        }
    }
}
