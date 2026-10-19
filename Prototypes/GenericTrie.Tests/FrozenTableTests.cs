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

using GenericTrie;

namespace GenericTrie.Tests;

/// <summary>
///     Tests for the frozen-table options of PooledTrie: leaf-pushed tables and compressed tables in the style of
///     Poptrie. Each shape is checked against an independent reference, at edge cases, after a later insert, and on the
///     real routing tables.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class FrozenTableTests
{
    // subBits 0 selects compressed tables; the narrow shapes force a last table or node that is narrower than the rest
    public static readonly object[] Shapes =
    {
        new object[] { "leaf-pushed 16/8", 16, 8 },
        new object[] { "leaf-pushed 20/4", 20, 4 },
        new object[] { "leaf-pushed 4/6", 4, 6 },
        new object[] { "compressed 16", 16, 0 },
        new object[] { "compressed 18", 18, 0 },
        new object[] { "compressed 20", 20, 0 },
        new object[] { "compressed 8", 8, 0 }
    };

    private static void Freeze<TKey, TPrefix, TValue>(PooledTrie<TKey, TPrefix, TValue> trie, int rootBits, int subBits, int width)
        where TKey : struct, IBitKey<TKey>
    {
        if (subBits == 0) trie.BuildCompressed(rootBits, width);
        else trie.BuildLeafPushed(rootBits, subBits, width);
    }

    private static PooledTrie<TKey, int, int> Build<TKey>(TKey[] prefixes, int[] lengths) where TKey : struct, IBitKey<TKey>
    {
        var trie = new PooledTrie<TKey, int, int>();
        for (var i = 0; i < prefixes.Length; i++)
            trie.TryAdd(in prefixes[i], lengths[i], i, i * 7);
        trie.Relayout(NodeOrder.DepthFirst);
        return trie;
    }

    // the reference: the IBinaryInteger trie from the first prototype
    private static int[] ReferenceMatches(uint[] prefixes, int[] lengths, uint[] keys)
    {
        var reference = new BitTrie<uint, int, int>();
        for (var i = 0; i < prefixes.Length; i++)
            reference.TryAdd(prefixes[i], lengths[i], i, 0);
        return keys.Select(k => reference.TryGetMatch(k, out var p, out _) ? p : -1).ToArray();
    }

    private static void AssertMatches<TKey>(PooledTrie<TKey, int, int> trie, TKey[] keys, int[] expected, string because) where TKey : struct, IBitKey<TKey>
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

    [TestCaseSource(nameof(Shapes))]
    public void Random_IPv4_prefixes_match_the_reference(string name, int rootBits, int subBits)
    {
        var trie = Build(WidthData.Prefix32, WidthData.PrefixLengths);
        Freeze(trie, rootBits, subBits, 32);
        AssertMatches(trie, WidthData.Key32, ReferenceMatches(WidthData.PrefixRef, WidthData.PrefixLengths, WidthData.KeyRef), name);
    }

    [TestCaseSource(nameof(Shapes))]
    public void The_same_prefixes_in_a_128_bit_key_match_the_reference(string name, int rootBits, int subBits)
    {
        // the IPv4 bits sit at the top of the 128-bit key, so the matches are the same as for 32 bits
        var trie = Build(WidthData.Prefix128, WidthData.PrefixLengths);
        Freeze(trie, rootBits, subBits, 128);
        AssertMatches(trie, WidthData.Key128, ReferenceMatches(WidthData.PrefixRef, WidthData.PrefixLengths, WidthData.KeyRef), name);
    }

    [TestCaseSource(nameof(Shapes))]
    public void Edge_cases(string name, int rootBits, int subBits)
    {
        // the default route, host routes, a /31 and prefixes that end exactly on and just off table boundaries
        uint[] prefixes = { 0x00000000, 0x0A000000, 0x0A010000, 0x0A010200, 0x0A010203, 0x0A010204, 0x0A010280, 0x0A0102FF, 0x0A010300, 0x0A010000, 0xFFFFFFFF, 0xC0000000 };
        int[] lengths = { 0, 8, 16, 24, 32, 31, 25, 32, 26, 18, 32, 2 };
        var keys = Enumerable.Range(0, 1024).Select(i => 0x0A010000u + (uint)i) // 10.1.0.0 to 10.1.3.255
            .Concat(new uint[] { 0, 0x0A020000, 0x0B000000, 0x7FFFFFFF, 0xC0000001, 0xFFFFFFFE, 0xFFFFFFFF })
            .ToArray();

        foreach (var withDefault in new[] { true, false })
        {
            var used = Enumerable.Range(0, prefixes.Length).Where(i => withDefault || lengths[i] != 0).ToArray();
            var p = used.Select(i => prefixes[i]).ToArray();
            var l = used.Select(i => lengths[i]).ToArray();
            var trie = Build(p.Select(b => new Bits32(b)).ToArray(), l);
            Freeze(trie, rootBits, subBits, 32);
            AssertMatches(trie, keys.Select(k => new Bits32(k)).ToArray(), ReferenceMatches(p, l, keys), $"{name}, {(withDefault ? "with" : "without")} the default route");
        }
    }

    [TestCaseSource(nameof(Shapes))]
    public void An_empty_trie_matches_nothing(string name, int rootBits, int subBits)
    {
        var trie = new PooledTrie<Bits32, int, int>();
        Freeze(trie, rootBits, subBits, 32);
        foreach (var key in new uint[] { 0, 0x0A000001, 0xFFFFFFFF })
            Assert.That(trie.TryGetMatch(new Bits32(key), out _, out _), Is.False, name);
    }

    [TestCaseSource(nameof(Shapes))]
    public void Adding_a_prefix_after_freezing_drops_the_tables(string name, int rootBits, int subBits)
    {
        var trie = Build(new[] { new Bits32(0x0A000000) }, new[] { 8 });
        Freeze(trie, rootBits, subBits, 32);
        trie.TryAdd(new Bits32(0x0A010200), 24, 1, 7);
        Assert.That(trie.TryGetMatch(new Bits32(0x0A010203), out var prefix, out _) && prefix == 1, name);
    }

    [Test]
    public void Real_IPv4_table_matches_the_node_walk()
    {
        RequireRealData(); // before touching RealData, which throws without the data
        RealTable(RealData.V4, 32);
    }

    [Test]
    public void Real_IPv6_table_matches_the_node_walk()
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

        // the node walk was checked against the current library in SnapshotTrieTests and RealDataChecks
        var trie = Build(table.Bits, table.Lengths);
        var expected = table.Keys.Select(k => trie.GetMatchIndex(in k)).ToArray();
        foreach (var (rootBits, subBits) in new[] { (16, 8), (18, 0) })
        {
            var frozen = Build(table.Bits, table.Lengths);
            Freeze(frozen, rootBits, subBits, width);
            var mismatches = 0;
            for (var i = 0; i < table.Keys.Length; i++)
                if (frozen.GetMatchIndex(in table.Keys[i]) != expected[i])
                    mismatches++;
            Assert.That(mismatches, Is.Zero, subBits == 0 ? $"compressed {rootBits}" : $"leaf-pushed {rootBits}/{subBits}");
        }
    }
}
