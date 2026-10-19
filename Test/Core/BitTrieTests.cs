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
    }

    [Test]
    public void Bits16_queries_match_the_reference() => AssertAllForms<Bits16>(3000, 16);

    [Test]
    public void Bits32_queries_match_the_reference() => AssertAllForms<Bits32>(5000, 32);

    [Test]
    public void Bits64_queries_match_the_reference() => AssertAllForms<Bits64>(3000, 64);

    [Test]
    public void Bits128_queries_match_the_reference() => AssertAllForms<Bits128>(3000, 128);

    [Test]
    public void Bits192_queries_match_the_reference() => AssertAllForms<Bits192>(2000, 192);

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
        Assert.That(statistics.Bytes, Is.GreaterThan(1L << 20), "includes the 1 MB shortcut table");
        Assert.That(trie.Freeze().Bytes, Is.GreaterThan(0));
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
        var stop = false;
        long lookups = 0, failures = 0;
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (!trie.TryGetMatch(new Bits32(0), out var prefix, out _) || prefix != -1)
                    Interlocked.Increment(ref failures);
                lookups++;
            }
        });
        reader.Start();

        var random = new Random(9);
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

        Volatile.Write(ref stop, true);
        reader.Join();
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

        var stop = false;
        long failures = 0;
        var readers = Enumerable.Range(0, 3).Select(r => new Thread(() =>
        {
            var at = r;
            while (!Volatile.Read(ref stop))
            {
                at = (at + 1) % keys.Length;
                // the answer is a stable prefix at least as long as the stable answer, or a churned one longer still,
                // and it must contain the key
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
            }
        })).ToList();
        readers.ForEach(t => t.Start());

        var random = new Random(12);
        for (var round = 0; round < 20; round++)
        {
            foreach (var i in churned.OrderBy(_ => random.Next()))
                trie.Add(prefixes[i].Bits, prefixes[i].Length, i, i * 7);
            foreach (var i in churned.OrderBy(_ => random.Next()))
                trie.Remove(prefixes[i].Bits, prefixes[i].Length);
        }

        Volatile.Write(ref stop, true);
        readers.ForEach(t => t.Join());
        Assert.That(failures, Is.Zero);
        Assert.That(trie.ToTextTree(Format), Is.EqualTo(Build(prefixes, stable).ToTextTree(Format)));
    }

    [Test]
    public void Writes_from_several_threads_are_serialized()
    {
        var prefixes = TestKeys.RandomPrefixes<Bits64>(8000, 13);
        var trie = new BitTrie<Bits64, int, int>();
        Parallel.For(0, prefixes.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => trie.Add(prefixes[i].Bits, prefixes[i].Length, i, i * 7));
        Assert.That(trie.Count, Is.EqualTo(prefixes.Count));
        Assert.That(trie.ToTextTree(Format), Is.EqualTo(Build(prefixes, Enumerable.Range(0, prefixes.Count)).ToTextTree(Format)));
    }
}
