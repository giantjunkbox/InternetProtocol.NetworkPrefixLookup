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
using GenericTrie;

namespace GenericTrie.Tests;

/// <summary>
///     Tests for SnapshotTrie, the production-style version of the optimized core: correctness against independent
///     references, removal, canonical shape, index maintenance under churn, edge cases, real routing tables, and
///     lock-free readers alongside a writer.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class SnapshotTrieTests
{
    // configurations: no index, a flat 16-bit index, and the multi-level index
    public static readonly object[] Configurations =
    {
        new object[] { "no index", 0, 8, 8 },
        new object[] { "flat 16-bit", 16, 8, int.MaxValue },
        new object[] { "multi-level 16/8/8", 16, 8, 8 },
        new object[] { "multi-level 8/4/2", 8, 4, 2 }
    };

    private static SnapshotTrie<Bits32, int, int> Build(int rootBits, int subBits, int threshold, IEnumerable<int> indexes)
    {
        var trie = new SnapshotTrie<Bits32, int, int>(32, rootBits, subBits, threshold);
        foreach (var i in indexes)
            Assert.That(trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7), Is.True);
        return trie;
    }

    private static string Format(Bits32 bits, int length) => $"{bits.Value:x8}/{length}";

    // the reference: the IBinaryInteger trie from the first prototype, built from the given prefixes
    private static int[] ReferenceMatches(IEnumerable<int> indexes, Bits32[] keys)
    {
        var reference = new BitTrie<uint, int, int>();
        foreach (var i in indexes)
            reference.TryAdd(WidthData.Prefix32[i].Value, WidthData.PrefixLengths[i], i, 0);
        return keys.Select(k => reference.TryGetMatch(k.Value, out var p, out _) ? p : -1).ToArray();
    }

    private static void AssertMatches(SnapshotTrie<Bits32, int, int> trie, int[] expected, Bits32[] keys, string because)
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

    [TestCaseSource(nameof(Configurations))]
    public void Lookups_match_the_reference(string name, int rootBits, int subBits, int threshold)
    {
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var trie = Build(rootBits, subBits, threshold, all);
        Assert.That(trie.Count, Is.EqualTo(all.Length));
        AssertMatches(trie, ReferenceMatches(all, WidthData.Key32), WidthData.Key32, name);
    }

    [TestCaseSource(nameof(Configurations))]
    public void Removing_prefixes_gives_the_same_trie_as_never_adding_them(string name, int rootBits, int subBits, int threshold)
    {
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var removed = all.Where(i => i % 5 == 0).ToArray();
        var kept = all.Where(i => i % 5 != 0).ToArray();

        var trie = Build(rootBits, subBits, threshold, all);
        foreach (var i in removed)
            Assert.That(trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]), Is.True);

        var fresh = Build(rootBits, subBits, threshold, kept);
        Assert.That(trie.Count, Is.EqualTo(kept.Length));
        Assert.That(trie.Dump(Format), Is.EqualTo(fresh.Dump(Format)), "shape after removal");

        var expected = ReferenceMatches(kept, WidthData.Key32);
        AssertMatches(trie, expected, WidthData.Key32, $"{name}, before compaction");

        trie.Compact();
        Assert.That(trie.Garbage, Is.EqualTo((0, 0, 0)));
        Assert.That(trie.Dump(Format), Is.EqualTo(fresh.Dump(Format)), "shape after compaction");
        AssertMatches(trie, expected, WidthData.Key32, $"{name}, after compaction");
    }

    [Test]
    public void Bulk_load_gives_the_same_results_as_adding_one_at_a_time()
    {
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var trie = new SnapshotTrie<Bits32, int, int>(32);
        trie.BeginBulkLoad();
        var keys = WidthData.Key32.Take(100_000).ToArray();
        var half = all.Length / 2;
        foreach (var i in all.Take(half))
            trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7);
        AssertMatches(trie, ReferenceMatches(all.Take(half), keys), keys, "during the bulk load, without an index");
        foreach (var i in all.Skip(half))
            trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7);
        trie.EndBulkLoad();

        Assert.That(trie.Dump(Format), Is.EqualTo(Build(16, 8, 8, all).Dump(Format)));
        AssertMatches(trie, ReferenceMatches(all, WidthData.Key32), WidthData.Key32, "after the bulk load");

        // the index is maintained again afterwards
        var removed = all.Where(i => i % 3 == 0).ToArray();
        foreach (var i in removed)
            trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]);
        AssertMatches(trie, ReferenceMatches(all.Where(i => i % 3 != 0), WidthData.Key32), WidthData.Key32, "after removals");
    }

    [Test]
    public void Insertion_order_does_not_change_the_trie()
    {
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var random = new Random(7);
        var shuffled = all.OrderBy(_ => random.Next()).ToArray();
        var reversed = all.Reverse().ToArray();

        var a = Build(16, 8, 8, all).Dump(Format);
        Assert.That(Build(16, 8, 8, shuffled).Dump(Format), Is.EqualTo(a));
        Assert.That(Build(16, 8, 8, reversed).Dump(Format), Is.EqualTo(a));
    }

    [TestCase(16, 8, 8)]
    [TestCase(16, 8, int.MaxValue)]
    [TestCase(8, 4, 2)]
    public void Index_stays_correct_while_prefixes_come_and_go(int rootBits, int subBits, int threshold)
    {
        var random = new Random(11);
        var pool = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var present = new HashSet<int>(pool.Where(i => i % 2 == 0));
        var trie = Build(rootBits, subBits, threshold, present);
        var keys = WidthData.Key32.Take(200_000).ToArray();

        for (var round = 0; round < 10; round++)
        {
            for (var op = 0; op < 5_000; op++)
            {
                var i = pool[random.Next(pool.Length)];
                if (present.Remove(i))
                    Assert.That(trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]), Is.True);
                else
                {
                    present.Add(i);
                    Assert.That(trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7), Is.True);
                }
            }

            Assert.That(trie.Count, Is.EqualTo(present.Count));
            AssertMatches(trie, ReferenceMatches(present, keys), keys, $"round {round}");
            if (round == 5)
                trie.Compact();
        }
    }

    [Test]
    public void Edge_cases()
    {
        var trie = new SnapshotTrie<Bits32, string, int>(32);
        var key = new Bits32(0x0A000000); // 10.0.0.0

        Assert.That(trie.TryGetMatch(in key, out _, out _), Is.False, "empty trie");
        Assert.That(trie.TryRemove(in key, 8), Is.False, "remove from empty trie");

        Assert.That(trie.TryAdd(in key, 8, "10.0.0.0/8", 1), Is.True);
        Assert.That(trie.TryAdd(in key, 8, "duplicate", 2), Is.False, "duplicate add");
        Assert.That(trie.Count, Is.EqualTo(1));

        // two siblings create a routing node at 10.0.0.0/23; removing that prefix must fail, unlike the v2 library
        var a = new Bits32(0x0A000000);
        var b = new Bits32(0x0A000100);
        trie.TryAdd(in a, 24, "10.0.0.0/24", 3);
        trie.TryAdd(in b, 24, "10.0.1.0/24", 4);
        Assert.That(trie.TryRemove(new Bits32(0x0A000000), 23), Is.False, "routing node prefix");
        Assert.That(trie.Count, Is.EqualTo(3));

        // the default route and a host route
        trie.TryAdd(new Bits32(0), 0, "0.0.0.0/0", 5);
        var host = new Bits32(0x0A000105);
        trie.TryAdd(in host, 32, "10.0.1.5/32", 6);
        Assert.That(trie.TryGetMatch(new Bits32(0x0A000105), out var p, out _) && p == "10.0.1.5/32");
        Assert.That(trie.TryGetMatch(new Bits32(0x0A000106), out p, out _) && p == "10.0.1.0/24");
        Assert.That(trie.TryGetMatch(new Bits32(0x0B000000), out p, out _) && p == "0.0.0.0/0");
        Assert.That(trie.TryRemove(new Bits32(0), 0), Is.True);
        Assert.That(trie.TryGetMatch(new Bits32(0x0B000000), out _, out _), Is.False, "default route removed");
        Assert.That(trie.Count, Is.EqualTo(4));
    }

    // ---- real routing tables ---------------------------------------------------------------------------------------

    private static void RequireRealData()
    {
        if (Environment.GetEnvironmentVariable("ROUTEVIEWS_DIR") == null)
            Assert.Ignore("Set ROUTEVIEWS_DIR to run the real-data tests; see Prototypes/GenericTrie/download-routeviews.sh.");
    }

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

    private static void RealTable<TKey>(RealData.Table<TKey> table, int width) where TKey : struct, IBitKey<TKey>
    {
        RequireRealData();
        var current = new NetworkPrefixLookup<int>();
        for (var i = 0; i < table.Networks.Length; i++)
            current.TryAdd(new NetworkPrefix(table.Networks[i].BaseAddress, table.Networks[i].PrefixLength), i);

        var trie = new SnapshotTrie<TKey, int, int>(width);
        for (var i = 0; i < table.Bits.Length; i++)
            trie.TryAdd(in table.Bits[i], table.Lengths[i], i, table.Asns[i]);

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

        // remove every tenth prefix from both and compare again, then compact
        for (var i = 0; i < table.Bits.Length; i += 10)
        {
            Assert.That(trie.TryRemove(in table.Bits[i], table.Lengths[i]), Is.True);
            current.Remove(new NetworkPrefix(table.Networks[i].BaseAddress, table.Networks[i].PrefixLength));
        }
        Compare("after removing every tenth prefix");
        trie.Compact();
        Compare("after compaction");
    }

    // ---- concurrency -----------------------------------------------------------------------------------------------

    [Test]
    public void Readers_always_see_a_valid_trie_while_one_writer_changes_it()
    {
        // half the prefixes are permanent; the writer keeps adding and removing the other half and compacts now and then
        var all = Enumerable.Range(0, WidthData.Prefix32.Length).ToArray();
        var permanent = all.Where(i => i % 2 == 0).ToArray();
        var churn = all.Where(i => i % 2 == 1).ToArray();
        var keys = WidthData.Key32.Take(200_000).ToArray();

        // a reader must never return something less specific than the best permanent prefix for its key
        var permanentOnly = Build(16, 8, 8, permanent);
        var minimumLength = keys.Select(k => permanentOnly.TryGetMatch(in k, out var p, out _) ? WidthData.PrefixLengths[p] : -1).ToArray();

        var trie = Build(16, 8, 8, permanent);
        var stop = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        long lookups = 0, writes = 0, compactions = 0;

        var writer = new Thread(() =>
        {
            var random = new Random(3);
            var present = new HashSet<int>();
            while (Volatile.Read(ref stop) == 0)
            {
                var i = churn[random.Next(churn.Length)];
                if (present.Remove(i)) trie.TryRemove(in WidthData.Prefix32[i], WidthData.PrefixLengths[i]);
                else { present.Add(i); trie.TryAdd(in WidthData.Prefix32[i], WidthData.PrefixLengths[i], i, i * 7); }
                if (++writes % 20_000 == 0) { trie.Compact(); compactions++; }
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

        TestContext.WriteLine($"{writes:N0} writes, {compactions} compactions, {lookups:N0} lookups, {failures.Count} failures");
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(10)));
        Assert.That(compactions, Is.GreaterThan(0));
    }
}
