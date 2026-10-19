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
using GenericTrie;

namespace GenericTrie.Tests;

/// <summary>
///     Tests for BartTrie, the BART-style read-write core: correctness against an independent reference, canonical
///     shape under removal and insertion order, bulk loading, edge cases, churn, real routing tables, and lock-free
///     readers alongside a writer.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class BartTrieTests
{
    private static BartTrie<Bits32, int, int> Build(IEnumerable<int> indexes, bool bulk = false)
    {
        var trie = new BartTrie<Bits32, int, int>(32);
        if (bulk) trie.BeginBulkLoad();
        foreach (var i in indexes)
            Assert.That(trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7), Is.True);
        if (bulk) trie.EndBulkLoad();
        return trie;
    }

    private static string Format(Bits32 bits, int length) => $"{bits.Value:x8}/{length}";

    // the reference: the IBinaryInteger trie from the first prototype
    private static int[] ReferenceMatches(IEnumerable<int> indexes, Bits32[] keys)
    {
        var reference = new BitTrie<uint, int, int>();
        foreach (var i in indexes)
            reference.TryAdd(WidthData.Prefix32[i].Value, WidthData.PrefixLengths[i], i, 0);
        return keys.Select(k => reference.TryGetMatch(k.Value, out var p, out _) ? p : -1).ToArray();
    }

    private static void AssertMatches<TKey>(BartTrie<TKey, int, int> trie, int[] expected, TKey[] keys, string because) where TKey : struct, IBitKey<TKey>
    {
        var mismatches = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            var found = trie.TryGetMatch(in keys[i], out var prefix, out var value);
            var actual = found ? prefix : -1;
            if (actual != expected[i] || (found && value != prefix * 7))
                mismatches++;
        }
        Assert.That(mismatches, Is.Zero, because);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Lookups_match_the_reference(bool bulk)
    {
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var trie = Build(all, bulk);
        Assert.That(trie.Count, Is.EqualTo(all.Length));
        AssertMatches(trie, ReferenceMatches(all, WidthData.Key32), WidthData.Key32, bulk ? "bulk load" : "one at a time");
    }

    [Test]
    public void The_same_prefixes_in_a_128_bit_key_match_the_reference()
    {
        var trie = new BartTrie<Bits128, int, int>(128);
        for (var i = 0; i < WidthData.Prefix128.Length; i++)
            trie.TryAdd(in WidthData.Prefix128[i], WidthData.PrefixLengths[i], i, i * 7);
        AssertMatches(trie, ReferenceMatches(Enumerable.Range(0, WidthData.Prefix32.Length), WidthData.Key32), WidthData.Key128, "128-bit keys");
    }

    [Test]
    public void Removing_prefixes_gives_the_same_trie_as_never_adding_them()
    {
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var kept = all.Where(i => i % 5 != 0).ToArray();
        var trie = Build(all);
        foreach (var i in all.Where(i => i % 5 == 0))
            Assert.That(trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]), Is.True);

        Assert.That(trie.Count, Is.EqualTo(kept.Length));
        Assert.That(trie.Dump(Format), Is.EqualTo(Build(kept).Dump(Format)), "shape after removal");
        AssertMatches(trie, ReferenceMatches(kept, WidthData.Key32), WidthData.Key32, "after removal");

        foreach (var i in kept)
            Assert.That(trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]), Is.True);
        Assert.That(trie.Count, Is.Zero);
        Assert.That(trie.Dump(Format), Is.Empty, "everything removed");
        Assert.That(trie.TryGetMatch(in WidthData.Key32[0], out _, out _), Is.False);
    }

    [Test]
    public void Insertion_order_does_not_change_the_trie()
    {
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var random = new Random(7);
        var expected = Build(all).Dump(Format);
        Assert.That(Build(all.OrderBy(_ => random.Next()).ToArray()).Dump(Format), Is.EqualTo(expected), "shuffled");
        Assert.That(Build(all.Reverse().ToArray()).Dump(Format), Is.EqualTo(expected), "reversed");
        Assert.That(Build(all, bulk: true).Dump(Format), Is.EqualTo(expected), "bulk load");
    }

    [Test]
    public void Readers_see_nothing_of_a_bulk_load_until_it_ends()
    {
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var first = all.Where(i => i % 2 == 0).ToArray();
        var keys = WidthData.Key32.Take(100_000).ToArray();
        var trie = Build(first);
        var before = ReferenceMatches(first, keys);

        trie.BeginBulkLoad();
        foreach (var i in all.Where(i => i % 2 == 1))
            trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7);
        foreach (var i in first.Where(i => i % 3 == 0))
            trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]);
        AssertMatches(trie, before, keys, "during the bulk load");
        trie.EndBulkLoad();

        var after = all.Where(i => i % 2 == 1 || i % 3 != 0).ToArray();
        AssertMatches(trie, ReferenceMatches(after, keys), keys, "after the bulk load");
        Assert.That(trie.Dump(Format), Is.EqualTo(Build(after).Dump(Format)));
    }

    [Test]
    public void Edge_cases()
    {
        var trie = new BartTrie<Bits32, string, int>(32);
        var key = new Bits32(0x0A000000); // 10.0.0.0

        Assert.That(trie.TryGetMatch(in key, out _, out _), Is.False, "empty trie");
        Assert.That(trie.TryRemove(in key, 8), Is.False, "remove from empty trie");
        Assert.That(trie.TryAdd(in key, 8, "10.0.0.0/8", 1), Is.True);
        Assert.That(trie.TryAdd(in key, 8, "duplicate", 2), Is.False, "duplicate add");

        // fringes at /16 and /24, prefixes inside strides, host routes and leaves of every length
        (uint Bits, int Length, string Name)[] prefixes =
        {
            (0x00000000, 0, "0/0"), (0x0A010000, 16, "10.1/16"), (0x0A010200, 24, "10.1.2/24"), (0x0A010280, 25, "10.1.2.128/25"),
            (0x0A010203, 32, "10.1.2.3/32"), (0x0A010204, 31, "10.1.2.4/31"), (0x0A800000, 9, "10.128/9"), (0xC0A80000, 13, "192.168/13"),
            (0xFFFFFFFF, 32, "255.255.255.255/32"), (0x80000000, 1, "128/1")
        };
        foreach (var (bits, length, name) in prefixes)
            Assert.That(trie.TryAdd(new Bits32(bits), length, name, length), Is.True, name);
        Assert.That(trie.Count, Is.EqualTo(prefixes.Length + 1));

        string? Match(uint k) => trie.TryGetMatch(new Bits32(k), out var p, out _) ? p : null;
        Assert.That(Match(0x0A010203), Is.EqualTo("10.1.2.3/32"));
        Assert.That(Match(0x0A010205), Is.EqualTo("10.1.2.4/31"));
        Assert.That(Match(0x0A010206), Is.EqualTo("10.1.2/24"));
        Assert.That(Match(0x0A0102FF), Is.EqualTo("10.1.2.128/25"));
        Assert.That(Match(0x0A010300), Is.EqualTo("10.1/16"));
        Assert.That(Match(0x0A020000), Is.EqualTo("10.0.0.0/8"));
        Assert.That(Match(0x0A900000), Is.EqualTo("10.128/9"));
        Assert.That(Match(0xC0AF0000), Is.EqualTo("192.168/13"));
        Assert.That(Match(0xFFFFFFFF), Is.EqualTo("255.255.255.255/32"));
        Assert.That(Match(0xFFFFFFFE), Is.EqualTo("128/1"));
        Assert.That(Match(0x0B000000), Is.EqualTo("0/0"));

        // removing the /24 fringe leaves the prefixes below it; removing the default route leaves nothing for 11/8
        Assert.That(trie.TryRemove(new Bits32(0x0A010200), 24), Is.True);
        Assert.That(Match(0x0A010206), Is.EqualTo("10.1/16"));
        Assert.That(Match(0x0A010203), Is.EqualTo("10.1.2.3/32"));
        Assert.That(trie.TryRemove(new Bits32(0), 0), Is.True);
        Assert.That(Match(0x0B000000), Is.Null);
        Assert.That(trie.TryRemove(new Bits32(0x0A010200), 23), Is.False, "never added");
        Assert.That(trie.Count, Is.EqualTo(prefixes.Length - 1));
    }

    [Test]
    public void Lookups_stay_correct_while_prefixes_come_and_go()
    {
        var random = new Random(11);
        var present = new HashSet<int>();
        var trie = new BartTrie<Bits32, int, int>(32);
        var keys = WidthData.Key32.Take(50_000).ToArray();
        for (var step = 1; step <= 60_000; step++)
        {
            var i = random.Next(WidthData.Prefix32.Length);
            if (present.Remove(i)) Assert.That(trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]), Is.True);
            else { present.Add(i); Assert.That(trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7), Is.True); }
            if (step % 15_000 == 0)
            {
                AssertMatches(trie, ReferenceMatches(present, keys), keys, $"after {step} changes");
                Assert.That(trie.Dump(Format), Is.EqualTo(Build(present).Dump(Format)), $"shape after {step} changes");
            }
        }
    }

    // ---- real routing tables ---------------------------------------------------------------------------------------

    [Test]
    public void Real_IPv4_table_matches_the_current_library()
    {
        RequireRealData(); // before touching RealData, which throws without the data
        RealTable(RealData.V4, 32);
    }

    [Test]
    public void Real_IPv6_table_matches_the_current_library()
    {
        RequireRealData(); // before touching RealData, which throws without the data
        RealTable(RealData.V6, 128);
    }

    private static void RequireRealData()
    {
        if (Environment.GetEnvironmentVariable("ROUTEVIEWS_DIR") == null)
            Assert.Ignore("Set ROUTEVIEWS_DIR to run the real-data tests; see Prototypes/GenericTrie/download-routeviews.sh.");
    }

    private static void RealTable<TKey>(RealData.Table<TKey> table, int width) where TKey : struct, IBitKey<TKey>
    {

        var current = new NetworkPrefixLookup<int>();
        for (var i = 0; i < table.Networks.Length; i++)
            current.TryAdd(new NetworkPrefix(table.Networks[i].BaseAddress, table.Networks[i].PrefixLength), i);

        var trie = new BartTrie<TKey, int, int>(width);
        trie.BeginBulkLoad();
        for (var i = 0; i < table.Bits.Length; i++)
            trie.TryAdd(in table.Bits[i], table.Lengths[i], i, table.Asns[i]);
        trie.EndBulkLoad();

        void Compare(string because)
        {
            var mismatches = 0;
            for (var k = 0; k < table.Keys.Length; k++)
            {
                var expected = current.TryGetMatch(table.Addresses[k], out var r) ? r.Value : -1;
                var actual = trie.TryGetMatch(in table.Keys[k], out var prefix, out _) ? prefix : -1;
                if (expected != actual) mismatches++;
            }
            Assert.That(mismatches, Is.Zero, because);
        }

        Compare("full table");
        for (var i = 0; i < table.Bits.Length; i += 10)
        {
            Assert.That(trie.TryRemove(in table.Bits[i], table.Lengths[i]), Is.True);
            current.Remove(new NetworkPrefix(table.Networks[i].BaseAddress, table.Networks[i].PrefixLength));
        }
        Compare("after removing every tenth prefix, one at a time");
    }

    // ---- concurrency -----------------------------------------------------------------------------------------------

    [Test]
    public void Readers_always_see_a_valid_trie_while_one_writer_changes_it()
    {
        // half the prefixes are permanent; the writer keeps adding and removing the other half
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var permanent = all.Where(i => i % 2 == 0).ToArray();
        var churn = all.Where(i => i % 2 == 1).ToArray();
        var keys = WidthData.Key32.Take(200_000).ToArray();

        // a reader must never return something less specific than the best permanent prefix for its key
        var permanentOnly = Build(permanent);
        var minimumLength = keys.Select(k => permanentOnly.TryGetMatch(in k, out var p, out _) ? WidthData.PrefixLengths[p] : -1).ToArray();

        var trie = Build(permanent);
        var stop = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        long lookups = 0, writes = 0;

        var writer = new Thread(() =>
        {
            var random = new Random(3);
            var present = new HashSet<int>();
            while (Volatile.Read(ref stop) == 0)
            {
                var i = churn[random.Next(churn.Length)];
                if (present.Remove(i)) trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]);
                else { present.Add(i); trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7); }
                writes++;
            }
        });

        var readers = Enumerable.Range(0, 3).Select(r => new Thread(() =>
        {
            var random = new Random(100 + r);
            while (Volatile.Read(ref stop) == 0)
            {
                var k = random.Next(keys.Length);
                var key = keys[k];
                if (trie.TryGetMatch(in key, out var prefix, out var value))
                {
                    var length = WidthData.PrefixLengths[prefix];
                    var bits = WidthData.Prefix32[prefix].Value;
                    var mask = length == 0 ? 0u : uint.MaxValue << (32 - length);
                    if ((key.Value & mask) != bits) failures.Enqueue($"{prefix} does not contain key {k}");
                    if (value != prefix * 7) failures.Enqueue($"value {value} does not belong to prefix {prefix}");
                    if (length < minimumLength[k]) failures.Enqueue($"key {k}: /{length} is less specific than a permanent /{minimumLength[k]}");
                }
                else if (minimumLength[k] >= 0)
                {
                    failures.Enqueue($"key {k}: no match although a permanent prefix contains it");
                }
                Interlocked.Increment(ref lookups);
            }
        })).ToArray();

        writer.Start();
        foreach (var t in readers) t.Start();
        Thread.Sleep(TimeSpan.FromSeconds(15));
        Volatile.Write(ref stop, 1);
        writer.Join();
        foreach (var t in readers) t.Join();

        TestContext.WriteLine($"{writes:N0} writes, {lookups:N0} lookups, {failures.Count} failures");
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(10)));
    }
}
