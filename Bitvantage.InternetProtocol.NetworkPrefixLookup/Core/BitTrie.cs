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

using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     A read-write set of prefixes over keys of type <typeparamref name="TKey" />, each stored with a prefix object and a
///     value, with fast longest-prefix-match lookups.
/// </summary>
/// <remarks>
///     <para>
///         The trie follows the design of Go's BART: 8-bit strides, where each node keeps four 256-bit sets with
///         popcount-compressed arrays. They hold the prefixes that end inside the node's stride, as Knuth's ART base
///         indexes 1 to 255; fringes, prefixes that end exactly at the end of the stride; child nodes; and leaves, lone
///         longer prefixes stored whole. The shape is canonical: a child node exists at an octet exactly when two or more
///         prefixes lie below it. For keys of 16 bits or more, shortcut blocks, one for each first octet that leads to a
///         node, point at the nodes below the first 16 bits, so most lookups skip the first two levels.
///     </para>
///     <para>
///         <b>Thread safety.</b> Any number of threads may read while one writes. Reads take no locks, and each sees the
///         trie as it was before or after every change: a lookup that overlaps two or more changes looks again. Writes
///         are serialized by an internal lock. A write copies the one node it changes, finishes the copy, and links it in
///         with one reference write; the garbage collector frees replaced nodes, so the trie never pauses for compaction
///         or array growth. Enumeration during writes is safe and sees every entry whole, but is not a snapshot of the
///         whole trie at one instant.
///     </para>
///     <para>
///         <b>Batches.</b> Changes made after <see cref="BeginBatch" /> build privately and stay invisible to readers
///         until <see cref="Batch.Commit" /> publishes them with one reference write. Disposing an uncommitted batch
///         discards them.
///     </para>
/// </remarks>
/// <typeparam name="TKey">The key type, whose width must be a multiple of 8 from 8 to 128 bits.</typeparam>
/// <typeparam name="TPrefix">The type of the prefix object stored with each prefix.</typeparam>
/// <typeparam name="TValue">The type of the value stored with each prefix.</typeparam>
public sealed class BitTrie<TKey, TPrefix, TValue> : IReadOnlyBitTrie<TKey, TPrefix, TValue>
    where TKey : struct, IBitKey<TKey>
{
    private struct Entry
    {
        public TPrefix Prefix;
        public TValue Value;
    }

    private struct Leaf
    {
        public TKey Bits;
        public TKey Mask;
        public int Length;
        public Entry Entry;
    }

    // which of a node's arrays it still shares with the node it was copied from; those must be copied before a change
    private const byte PrefixArray = 1, FringeArray = 2, KidArray = 4, LeafArray = 8, AllArrays = 15;

    private sealed class Node
    {
        // read by lookups; never changed once readers can see the node, except for single child references in KidNodes
        public BitSet256 Kids;
        public BitSet256 Leaves;
        public BitSet256 Fringes;
        public BitSet256 Prefixes;
        public Node[] KidNodes = Array.Empty<Node>();
        public Leaf[] LeafEntries = Array.Empty<Leaf>();
        public Entry[] FringeEntries = Array.Empty<Entry>();
        public Entry[] PrefixEntries = Array.Empty<Entry>();

        // writer only
        public int Count;   // prefixes in this subtree, including fringes and leaves
        public long Stamp;  // the write or batch that created this node, which may change it in place; 64 bits never wrap
        public byte Shared;

        public Node Copy(long stamp)
        {
            var copy = (Node)MemberwiseClone();
            copy.Stamp = stamp;
            copy.Shared = AllArrays;
            return copy;
        }
    }

    // The nodes on a path, one per level: 16 levels of 8 bits for a 128-bit key, plus the root. Writes keep the path in
    // one, and so does the lookup's rare deep backtrack, but not the lookup's fast path: see FindOnce.
    [InlineArray(17)]
    private struct NodeStack
    {
        private Node _element;
    }

    // Shortcuts for the first 16 bits, in one block per first octet that leads to a node at depth 1: the block holds that
    // node and, for each second octet, the node at depth 2 below it, or null. Lookups start at the deepest of them. A
    // block appears with its depth-1 node and goes with it, so a trie holds blocks only where they save work, and no
    // write builds more than one block of 256 entries. A write that replaces a node at depth 1 or 2 updates its block in
    // place. A reader may briefly combine nodes of different versions; since a write changes one prefix, the result is
    // still the answer from before or after it.
    private sealed class Block(Node depth1)
    {
        public Node Depth1 = depth1;
        public Depth2Nodes Depth2;
    }

    [InlineArray(256)]
    private struct Depth2Nodes
    {
        private Node? _element;
    }

    // What readers see: the root and the shortcut blocks. A batch or Clear replaces both with one reference write, so a
    // reader never combines a new root with old blocks. A single change outside a batch updates them in place.
    private sealed class Published(Node root, bool table)
    {
        public Node Root = root;
        public readonly Block?[]? Blocks = table ? new Block?[256] : null;
    }

    private readonly Lock _lock = new();
    private readonly bool _hasTable;

    private Published _published;
    private Node _w;        // what the writer works on; ahead of the published root only during a batch
    private long _stamp = 1; // nodes stamped 0 at construction count as published
    private bool _batch;
    private bool _batchFailed; // a write in the batch threw, so its private nodes may be half changed

    // Twice the number of published changes, plus one while a change is being published. A change that replaces a node
    // stores the copy into the parent's child array in place, and a copy shares that array with the node it was copied
    // from, so a lookup that overlaps two changes can see the second through a node from before the first: a mix that
    // never existed. Lookups read this before and after, and look again if two or more changes overlapped them.
    private long _changes;

    /// <summary>Creates an empty trie.</summary>
    /// <exception cref="NotSupportedException">The key width is not a multiple of 8 from 8 to 128.</exception>
    public BitTrie()
    {
        if (TKey.Width % 8 != 0 || TKey.Width < 8 || TKey.Width > 128)
            throw new NotSupportedException($"Key widths must be a multiple of 8 from 8 to 128 bits; {typeof(TKey).Name} has {TKey.Width}.");
        _hasTable = TKey.Width >= 16;
        _w = new Node();
        _published = new Published(_w, _hasTable);
    }

    /// <summary>Creates a trie holding the given prefixes, built in one pass.</summary>
    /// <param name="items">The prefixes, in any order, each with its bits, length, prefix object and value.</param>
    /// <remarks>
    ///     Faster than adding the prefixes one at a time, and it allocates less: each node is built once, with arrays of
    ///     the exact size. The prefixes are copied into a buffer first, which is garbage afterwards.
    /// </remarks>
    /// <exception cref="ArgumentException">A prefix appears more than once.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A length is outside 0 to the key width.</exception>
    public BitTrie(IEnumerable<(TKey Bits, int Length, TPrefix Prefix, TValue Value)> items) : this()
    {
        ArgumentNullException.ThrowIfNull(items);
        var buffer = new List<(TKey Bits, int Length, TPrefix Prefix, TValue Value)>(items.TryGetNonEnumeratedCount(out var count) ? count : 0);
        buffer.AddRange(items);
        Load(CollectionsMarshal.AsSpan(buffer));
    }

    /// <summary>The number of stored prefixes that readers can see; changes in an uncommitted batch do not count.</summary>
    public int Count => Volatile.Read(ref Root.Count);

    /// <summary>
    ///     A number that changes with every published change, so a caller can cache lookup results and check that they are
    ///     still current with one comparison.
    /// </summary>
    /// <remarks>Read it before the lookups whose results it is to vouch for: a value read after them may already count a change they missed.</remarks>
    public long Version => Volatile.Read(ref _changes) >> 1;

    // the root that readers see
    private Node Root => Volatile.Read(ref Volatile.Read(ref _published).Root);

    // ---- lookups ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(in TKey key, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value)
    {
        ref var entry = ref Find(in key);
        if (Unsafe.IsNullRef(ref entry))
        {
            prefix = default!;
            value = default!;
            return false;
        }

        prefix = entry.Prefix;
        value = entry.Value;
        return true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatchValue(in TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ref var entry = ref Find(in key);
        if (Unsafe.IsNullRef(ref entry))
        {
            value = default!;
            return false;
        }

        value = entry.Value;
        return true;
    }

    // True when at most one change overlapped a lookup that began when _changes was before, so that the lookup saw the
    // trie as it was before or after that change.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Settled(long before)
    {
        Volatile.ReadBarrier();
        return Volatile.Read(ref _changes) - (before & ~1L) <= 2;
    }

    // A loop around a separate search measured faster than checking at the search's own exit: see Architecture.md.
    private ref Entry Find(in TKey key)
    {
        while (true)
        {
            var before = Volatile.Read(ref _changes);
            ref var entry = ref FindOnce(in key);
            if (Settled(before)) return ref entry;
        }
    }

    // The longest match, from one walk of the published trie.
    //
    // The walk descends as far as the key leads and then looks for a match from the deepest node up, so it may need to
    // climb back up the path. Nodes have no parent links, because a copy-on-write node can be shared by several versions
    // of the trie and so has no one parent, which means the walk must remember the path itself. Remembering all of it
    // in a NodeStack is what the walk used to do, and it cost a write barrier call for every node visited: a node is a
    // managed reference, and the JIT cannot prove that a store into the inline array at a variable index goes to the
    // stack, so it calls the checked barrier. Creating the array was cheap; the stores were not. A stackalloc buffer
    // cannot hold managed references at all.
    //
    // So this walk keeps only the node one level up, in a local the JIT keeps in a register. On the real routing tables
    // the deepest node answers 98 to 100% of lookups and its parent nearly all the rest; on the random benchmark table
    // the parent answers 35% and 0.8% need to climb further. Those take FindWithStack, a second walk from the root that
    // keeps the whole path. Measured in one session, this made lookups take 0.85 to 0.93 of the time of the walk with a
    // stack on every table: see "Since the baseline" in Architecture.md.
    private ref Entry FindOnce(in TKey key)
    {
        var published = Volatile.Read(ref _published);
        var n = Volatile.Read(ref published.Root);
        Node? parent = null; // the node one level above n, for a backtrack of one level
        var depth = 0;

        var blocks = published.Blocks;
        if (blocks != null)
        {
            var block = blocks[TKey.GetTopBits(key, 8)];
            if (block != null)
            {
                parent = n;
                n = block.Depth1;
                depth = 1;
                var depth2 = block.Depth2[TKey.GetBits(key, 8, 8)];
                if (depth2 != null)
                {
                    parent = n;
                    n = depth2;
                    depth = 2;
                }
            }
        }

        // descend as far as the key leads; a leaf below the last node ends the search, and otherwise the last node's own
        // fringe or prefix is the longest match
        while (true)
        {
            var octet = TKey.GetBits(key, depth << 3, 8);
            if (n.Kids.Test(octet))
            {
                parent = n;
                n = n.KidNodes[n.Kids.Rank(octet) - 1];
                depth++;
                continue;
            }

            if (n.Leaves.Test(octet))
            {
                ref var leaf = ref n.LeafEntries[n.Leaves.Rank(octet) - 1];
                if (TKey.MatchesUnderMask(key, leaf.Bits, leaf.Mask))
                    return ref leaf.Entry;
            }

            if (n.Fringes.Test(octet))
                return ref n.FringeEntries[n.Fringes.Rank(octet) - 1];
            if (n.Prefixes.AndTop(in ArtTables.Ancestors[ArtTables.OctetToIndex(octet)], out var top))
                return ref n.PrefixEntries[n.Prefixes.Rank(top) - 1];
            break;
        }

        if (depth == 0)
            return ref Unsafe.NullRef<Entry>();

        // one level up, from the local: the deepest node held nothing for the key, so its parent's fringe or prefix at
        // the key's octet is the next longest candidate
        {
            n = parent!;
            depth--;
            var octet = TKey.GetBits(key, depth << 3, 8);
            if (n.Fringes.Test(octet))
                return ref n.FringeEntries[n.Fringes.Rank(octet) - 1];
            if (n.Prefixes.AndTop(in ArtTables.Ancestors[ArtTables.OctetToIndex(octet)], out var top))
                return ref n.PrefixEntries[n.Prefixes.Rank(top) - 1];
        }

        // PERF: further up, a second walk from the root, which keeps the whole path. Rare: 0.8% of lookups on the random
        // table, almost none on the real ones. The retry in Find covers both walks, and the answer comes from the second
        // walk alone, so it is still the trie at one moment even if a write landed between the two.
        return ref depth == 0 ? ref Unsafe.NullRef<Entry>() : ref FindWithStack(in key);
    }

    // The longest match with the whole path in a NodeStack, so that the backtrack can climb any number of levels. Only
    // FindOnce calls it, for the lookups whose answer lies two or more levels above the deepest node.
    private ref Entry FindWithStack(in TKey key)
    {
        var stack = new NodeStack();
        var published = Volatile.Read(ref _published);
        var n = Volatile.Read(ref published.Root);
        var depth = 0;

        var blocks = published.Blocks;
        if (blocks != null)
        {
            var block = blocks[TKey.GetTopBits(key, 8)];
            if (block != null)
            {
                stack[0] = n;
                n = block.Depth1;
                depth = 1;
                var depth2 = block.Depth2[TKey.GetBits(key, 8, 8)];
                if (depth2 != null)
                {
                    stack[1] = n;
                    n = depth2;
                    depth = 2;
                }
            }
        }

        // descend as far as the key leads; a leaf below the last node ends the search
        while (true)
        {
            stack[depth] = n;
            var octet = TKey.GetBits(key, depth << 3, 8);
            if (n.Kids.Test(octet))
            {
                n = n.KidNodes[n.Kids.Rank(octet) - 1];
                depth++;
                continue;
            }

            if (n.Leaves.Test(octet))
            {
                ref var leaf = ref n.LeafEntries[n.Leaves.Rank(octet) - 1];
                if (TKey.MatchesUnderMask(key, leaf.Bits, leaf.Mask))
                    return ref leaf.Entry;
            }

            break;
        }

        // then from the deepest node up: in each node a fringe beats any prefix inside the stride, and the first match
        // found is the longest
        while (true)
        {
            var octet = TKey.GetBits(key, depth << 3, 8);
            if (n.Fringes.Test(octet))
                return ref n.FringeEntries[n.Fringes.Rank(octet) - 1];
            if (n.Prefixes.AndTop(in ArtTables.Ancestors[ArtTables.OctetToIndex(octet)], out var top))
                return ref n.PrefixEntries[n.Prefixes.Rank(top) - 1];
            if (depth == 0)
                return ref Unsafe.NullRef<Entry>();
            n = stack[--depth];
        }
    }

    /// <inheritdoc />
    public bool TryGetMatch(in TKey bits, int length, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value)
    {
        CheckLength(length);
        ref var entry = ref FindForPrefix(in bits, length);
        if (Unsafe.IsNullRef(ref entry))
        {
            prefix = default!;
            value = default!;
            return false;
        }

        prefix = entry.Prefix;
        value = entry.Value;
        return true;
    }

    private ref Entry FindForPrefix(in TKey bits, int length)
    {
        while (true)
        {
            var before = Volatile.Read(ref _changes);
            ref var entry = ref FindForPrefixOnce(in bits, length);
            if (Settled(before)) return ref entry;
        }
    }

    // the longest stored prefix that contains the prefix (bits, length): only prefixes no longer than length qualify
    private ref Entry FindForPrefixOnce(in TKey bits, int length)
    {
        var path = new NodeStack();
        var n = Root;
        var depth = 0;
        while (true)
        {
            path[depth] = n;
            if (length - (depth << 3) <= 8)
                break;

            var octet = TKey.GetBits(bits, depth << 3, 8);
            if (n.Kids.Test(octet))
            {
                n = n.KidNodes[n.Kids.Rank(octet) - 1];
                depth++;
                continue;
            }

            if (n.Leaves.Test(octet))
            {
                ref var leaf = ref n.LeafEntries[n.Leaves.Rank(octet) - 1];
                if (leaf.Length <= length && TKey.MatchesUnderMask(bits, leaf.Bits, leaf.Mask))
                    return ref leaf.Entry;
            }

            break;
        }

        for (; depth >= 0; depth--)
        {
            n = path[depth];
            var octet = TKey.GetBits(bits, depth << 3, 8);
            var relative = length - (depth << 3);
            if (relative >= 8 && n.Fringes.Test(octet))
                return ref n.FringeEntries[n.Fringes.Rank(octet) - 1];
            var index = relative >= 8 ? ArtTables.OctetToIndex(octet) : ArtTables.PrefixToIndex(octet, relative);
            if (n.Prefixes.AndTop(in ArtTables.Ancestors[index], out var top))
                return ref n.PrefixEntries[n.Prefixes.Rank(top) - 1];
        }

        return ref Unsafe.NullRef<Entry>();
    }

    /// <inheritdoc />
    public bool TryGetValue(in TKey bits, int length, [MaybeNullWhen(false)] out TValue value)
    {
        CheckLength(length);
        ref var entry = ref FindPublished(in bits, length);
        if (Unsafe.IsNullRef(ref entry))
        {
            value = default!;
            return false;
        }

        value = entry.Value;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetValue(in TKey bits, int length, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value)
    {
        CheckLength(length);
        ref var entry = ref FindPublished(in bits, length);
        if (Unsafe.IsNullRef(ref entry))
        {
            prefix = default!;
            value = default!;
            return false;
        }

        prefix = entry.Prefix;
        value = entry.Value;
        return true;
    }

    /// <inheritdoc />
    public bool ContainsPrefix(in TKey bits, int length)
    {
        CheckLength(length);
        return !Unsafe.IsNullRef(ref FindPublished(in bits, length));
    }

    private ref Entry FindPublished(in TKey bits, int length)
    {
        while (true)
        {
            var before = Volatile.Read(ref _changes);
            ref var entry = ref FindExact(Root, in bits, length);
            if (Settled(before)) return ref entry;
        }
    }

    private static ref Entry FindExact(Node n, in TKey bits, int length)
    {
        for (var depth = 0; ; depth++)
        {
            var octet = TKey.GetBits(bits, depth << 3, 8);
            var relative = length - (depth << 3);
            if (relative < 8)
            {
                var index = ArtTables.PrefixToIndex(octet, relative);
                return ref n.Prefixes.Test(index) ? ref n.PrefixEntries[n.Prefixes.Rank(index) - 1] : ref Unsafe.NullRef<Entry>();
            }

            if (relative == 8)
                return ref n.Fringes.Test(octet) ? ref n.FringeEntries[n.Fringes.Rank(octet) - 1] : ref Unsafe.NullRef<Entry>();

            if (n.Kids.Test(octet))
            {
                n = n.KidNodes[n.Kids.Rank(octet) - 1];
                continue;
            }

            if (n.Leaves.Test(octet))
            {
                ref var leaf = ref n.LeafEntries[n.Leaves.Rank(octet) - 1];
                if (leaf.Length == length && TKey.MatchesUnderMask(bits, leaf.Bits, leaf.Mask))
                    return ref leaf.Entry;
            }

            return ref Unsafe.NullRef<Entry>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(in TKey key)
    {
        // The matches go straight into the list's own array. Most keys have a few, so it starts with room for four, the
        // array a list allocates on its first Add; a key with more is walked again with room for all of them.
        var results = new List<PrefixEntry<TPrefix, TValue>>(4);
        while (true)
        {
            CollectionsMarshal.SetCount(results, results.Capacity);
            var count = CollectMatches(in key, CollectionsMarshal.AsSpan(results));
            if (count <= results.Count)
            {
                CollectionsMarshal.SetCount(results, count);
                return results;
            }

            results.Capacity = count;
        }
    }

    /// <inheritdoc />
    public int GetMatches(in TKey key, Span<PrefixEntry<TPrefix, TValue>> destination)
    {
        var count = CollectMatches(in key, destination);
        if (count > destination.Length)
            throw new ArgumentException("The destination is too small for the matches.", nameof(destination));
        return count;
    }

    // Every prefix that contains key, longest first, from a walk that no two writes overlapped: writes those that fit
    // into destination and returns how many there are.
    private int CollectMatches(in TKey key, Span<PrefixEntry<TPrefix, TValue>> destination)
    {
        while (true)
        {
            var before = Volatile.Read(ref _changes);
            var count = CollectMatchesOnce(in key, destination);
            if (Settled(before)) return count;
        }
    }

    // counts every match, and stores those that fit
    private int CollectMatchesOnce(in TKey key, Span<PrefixEntry<TPrefix, TValue>> destination)
    {
        var count = 0;

        static void Emit(in Entry entry, Span<PrefixEntry<TPrefix, TValue>> destination, ref int count)
        {
            if (count < destination.Length)
                destination[count] = new PrefixEntry<TPrefix, TValue>(entry.Prefix, entry.Value);
            count++;
        }

        var path = new NodeStack();
        var n = Root;
        var depth = 0;
        while (true)
        {
            path[depth] = n;
            var octet = TKey.GetBits(key, depth << 3, 8);
            if (n.Kids.Test(octet))
            {
                n = n.KidNodes[n.Kids.Rank(octet) - 1];
                depth++;
                continue;
            }

            if (n.Leaves.Test(octet))
            {
                ref var leaf = ref n.LeafEntries[n.Leaves.Rank(octet) - 1];
                if (TKey.MatchesUnderMask(key, leaf.Bits, leaf.Mask))
                    Emit(in leaf.Entry, destination, ref count);
            }

            break;
        }

        for (; depth >= 0; depth--)
        {
            n = path[depth];
            var octet = TKey.GetBits(key, depth << 3, 8);
            if (n.Fringes.Test(octet))
                Emit(in n.FringeEntries[n.Fringes.Rank(octet) - 1], destination, ref count);

            // the prefixes inside the stride on the octet's path, from the longest: index, index / 2, ..., 1
            for (var index = ArtTables.OctetToIndex(octet); index > 0; index >>= 1)
                if (n.Prefixes.Test(index))
                    Emit(in n.PrefixEntries[n.Prefixes.Rank(index) - 1], destination, ref count);
        }

        return count;
    }

    /// <summary>Returns the stored prefixes in address order, a shorter prefix before a longer one at the same address.</summary>
    public IEnumerator<PrefixEntry<TPrefix, TValue>> GetEnumerator()
    {
        foreach (var item in Items(Root))
            yield return new PrefixEntry<TPrefix, TValue>(item.Entry.Prefix, item.Entry.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The stored prefixes with their bits and lengths, in address order. For the frozen trie and the IP layer.</summary>
    internal IEnumerable<(TKey Bits, int Length, TPrefix Prefix, TValue Value)> ItemsWithKeys()
    {
        foreach (var item in Items(Root))
            yield return (item.Bits, item.Length, item.Entry.Prefix, item.Entry.Value);
    }

    private static IEnumerable<(TKey Bits, int Length, Entry Entry)> Items(Node root)
    {
        // Depth-first, with the prefixes of each octet before what lies below it. Only the octets at which something
        // starts are visited: the start octets of the node's prefixes, and its fringes, leaves and kids. Testing all 256
        // octets and 8 lengths in every node made enumeration 2 to 6 times slower on the real tables, and Freeze reads
        // the trie through this walk.
        var stack = new Stack<(Node Node, int Depth, TKey Start, int Octet)>();
        stack.Push((root, 0, default, 0));
        while (stack.TryPop(out var frame))
        {
            var (n, depth, start, octet) = frame;
            var offset = depth << 3;
            var occupied = BitSet256.Or(ArtTables.StartOctets(in n.Prefixes), BitSet256.Or(n.Fringes, BitSet256.Or(n.Leaves, n.Kids)));
            for (octet = occupied.NextSet(octet); octet < 256; octet = occupied.NextSet(octet + 1))
            {
                // prefixes inside the stride that start at this octet, shortest first
                for (var relative = 0; relative < 8; relative++)
                {
                    if ((octet & ((1 << (8 - relative)) - 1)) != 0) continue;
                    var index = ArtTables.PrefixToIndex(octet, relative);
                    if (!n.Prefixes.Test(index)) continue;
                    var bits = relative == 0 ? start : TKey.SetBits(start, offset, relative, octet >> (8 - relative));
                    yield return (bits, offset + relative, n.PrefixEntries[n.Prefixes.Rank(index) - 1]);
                }

                if (n.Fringes.Test(octet))
                    yield return (TKey.SetBits(start, offset, 8, octet), offset + 8, n.FringeEntries[n.Fringes.Rank(octet) - 1]);

                if (n.Leaves.Test(octet))
                {
                    var leaf = n.LeafEntries[n.Leaves.Rank(octet) - 1];
                    yield return (leaf.Bits, leaf.Length, leaf.Entry);
                }

                if (n.Kids.Test(octet))
                {
                    // resume this node after the child's subtree
                    stack.Push((n, depth, start, octet + 1));
                    stack.Push((n.KidNodes[n.Kids.Rank(octet) - 1], depth + 1, TKey.SetBits(start, offset, 8, octet), 0));
                    break;
                }
            }
        }
    }

    // ---- writes ----------------------------------------------------------------------------------------------------

    /// <summary>Adds a prefix. Returns false, and changes nothing, when it is already present.</summary>
    /// <param name="bits">The prefix bits; bits after the length are ignored.</param>
    /// <param name="length">The prefix length.</param>
    /// <param name="prefix">The prefix object to store and return from lookups.</param>
    /// <param name="value">The value to store.</param>
    public bool TryAdd(in TKey bits, int length, TPrefix prefix, TValue value)
    {
        CheckLength(length);
        using (_lock.EnterScope())
            return SetOrFailBatch(in bits, length, prefix, value, update: false) == SetResult.Added;
    }

    /// <summary>Adds a prefix.</summary>
    /// <param name="bits">The prefix bits; bits after the length are ignored.</param>
    /// <param name="length">The prefix length.</param>
    /// <param name="prefix">The prefix object to store and return from lookups.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentException">The prefix is already present.</exception>
    public void Add(in TKey bits, int length, TPrefix prefix, TValue value)
    {
        if (!TryAdd(in bits, length, prefix, value))
            throw new ArgumentException("The prefix is already present.", nameof(bits));
    }

    /// <summary>Adds a prefix, or replaces the prefix object and value of one already present.</summary>
    /// <param name="bits">The prefix bits; bits after the length are ignored.</param>
    /// <param name="length">The prefix length.</param>
    /// <param name="prefix">The prefix object to store and return from lookups.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>True when the prefix was added, false when it was replaced.</returns>
    public bool AddOrUpdate(in TKey bits, int length, TPrefix prefix, TValue value)
    {
        CheckLength(length);
        using (_lock.EnterScope())
            return SetOrFailBatch(in bits, length, prefix, value, update: true) == SetResult.Added;
    }

    /// <summary>Returns the value of a prefix, adding it with a value from <paramref name="valueFactory" /> if absent.</summary>
    /// <param name="bits">The prefix bits; bits after the length are ignored.</param>
    /// <param name="length">The prefix length.</param>
    /// <param name="prefix">The prefix object to store if the prefix is added.</param>
    /// <param name="valueFactory">
    ///     Creates the value from the prefix object, called only when the prefix is absent, while other writers wait. It
    ///     must not write to this trie.
    /// </param>
    public TValue GetOrAdd(in TKey bits, int length, TPrefix prefix, Func<TPrefix, TValue> valueFactory)
    {
        CheckLength(length);
        ArgumentNullException.ThrowIfNull(valueFactory);
        using (_lock.EnterScope())
        {
            ref var entry = ref FindExact(_w, in bits, length);
            if (!Unsafe.IsNullRef(ref entry))
                return entry.Value;

            var value = valueFactory(prefix);
            SetOrFailBatch(in bits, length, prefix, value, update: false);
            return value;
        }
    }

    /// <summary>Removes a prefix. Returns false when it is not present.</summary>
    /// <param name="bits">The prefix bits; bits after the length are ignored.</param>
    /// <param name="length">The prefix length.</param>
    public bool Remove(in TKey bits, int length)
    {
        CheckLength(length);
        using (_lock.EnterScope())
        {
            try
            {
                return Delete(in bits, length);
            }
            catch
            {
                if (_batch) _batchFailed = true;
                throw;
            }
        }
    }

    // A write that throws inside a batch may leave the batch's private nodes half changed, so the batch can then only be
    // discarded. Outside a batch a write that throws has changed nothing readers can reach.
    private SetResult SetOrFailBatch(in TKey bits, int length, TPrefix prefix, TValue value, bool update)
    {
        try
        {
            return Set(in bits, length, prefix, value, update);
        }
        catch
        {
            if (_batch) _batchFailed = true;
            throw;
        }
    }

    /// <summary>Removes every prefix.</summary>
    public void Clear()
    {
        using (_lock.EnterScope())
        {
            _stamp++;
            _w = new Node { Stamp = _stamp };
            if (_batch) return;
            var published = new Published(_w, _hasTable);
            Interlocked.Increment(ref _changes);
            Volatile.Write(ref _published, published);
            Interlocked.Increment(ref _changes);
        }
    }

    /// <summary>
    ///     Starts a batch: the following changes build privately and stay invisible to readers until
    ///     <see cref="Batch.Commit" />. Disposing the batch without committing it discards them.
    /// </summary>
    /// <remarks>
    ///     The batch holds the trie's writer lock until it is committed or disposed, so other threads' writes wait. The
    ///     lock belongs to the thread that started the batch, so the batch must end on that thread and cannot span an
    ///     <c>await</c>; ending it on another thread throws and leaves it open.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A batch is already in progress.</exception>
    public Batch BeginBatch()
    {
        _lock.Enter();
        if (_batch)
        {
            _lock.Exit();
            throw new InvalidOperationException("A batch is already in progress.");
        }

        _stamp++;
        _batch = true;
        return new Batch(this);
    }

    /// <summary>A set of changes that readers see all at once, or not at all. See <see cref="BeginBatch" />.</summary>
    public sealed class Batch : IDisposable
    {
        private BitTrie<TKey, TPrefix, TValue>? _trie;

        internal Batch(BitTrie<TKey, TPrefix, TValue> trie) => _trie = trie;

        /// <summary>Publishes every change in the batch with one reference write.</summary>
        /// <remarks>
        ///     If a write in the batch threw, or publishing runs out of memory, nothing is published, the batch ends as if
        ///     disposed, and Commit throws.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The batch was already committed or disposed.</exception>
        /// <exception cref="InvalidOperationException">
        ///     The batch was ended on a thread other than the one that started it, which leaves it open; or a write in the
        ///     batch threw, and the batch was discarded.
        /// </exception>
        public void Commit()
        {
            var trie = _trie ?? throw new ObjectDisposedException(nameof(Batch));
            trie.CheckBatchThread();
            _trie = null;
            trie.EndBatch(commit: true);
        }

        /// <summary>Discards the batch's changes unless it was committed.</summary>
        /// <exception cref="InvalidOperationException">
        ///     The batch was ended on a thread other than the one that started it, which leaves it open.
        /// </exception>
        public void Dispose()
        {
            var trie = _trie;
            if (trie == null) return;
            trie.CheckBatchThread();
            _trie = null;
            trie.EndBatch(commit: false);
        }

        // for the IP lookups, which end two batches and must check both before ending either
        internal void CheckThread() => _trie?.CheckBatchThread();

        // true when a write in this batch threw, so that committing it will discard it and throw
        internal bool Failed => _trie is { _batchFailed: true };
    }

    // The writer lock belongs to the thread that took it, and only that thread can release it. Ending a batch elsewhere,
    // as after an await, would publish without the lock and then fail to release it, so it is refused before anything
    // changes. The batch stays open until its own thread ends it.
    private void CheckBatchThread()
    {
        if (!_lock.IsHeldByCurrentThread)
            throw new InvalidOperationException(
                "A batch must end on the thread that started it, so it cannot span an await. The batch is still open, and other writers wait until that thread ends it.");
    }

    // A commit that throws, which only running out of memory can make it do, discards the batch like Dispose: either
    // way the trie leaves batch mode and releases the lock.
    private void EndBatch(bool commit)
    {
        var published = false;
        try
        {
            if (commit && _batchFailed)
                throw new InvalidOperationException("A write in the batch threw, so the batch was discarded.");
            if (commit)
            {
                Trim(_w, _stamp);
                var snapshot = new Published(_w, _hasTable);
                if (snapshot.Blocks != null)
                {
                    // A batch copies every node above a change, so a first octet whose depth-1 node is still the same
                    // object has nothing changed below it, and its published block is still right.
                    var old = _published.Blocks!;
                    for (var first = 0; first < 256; first++)
                    {
                        var depth1 = Kid(_w, first);
                        if (depth1 != null)
                            snapshot.Blocks[first] = old[first] is { } block && block.Depth1 == depth1 ? block : NewBlock(depth1);
                    }
                }
                Interlocked.Increment(ref _changes);
                Volatile.Write(ref _published, snapshot);
                Interlocked.Increment(ref _changes);
                published = true;
            }
        }
        finally
        {
            if (!published) _w = _published.Root;
            _batch = false;
            _batchFailed = false;
            _stamp++;
            _lock.Exit();
        }
    }

    private enum SetResult { Added, Updated, Present }

    private SetResult Set(in TKey bits, int length, TPrefix prefix, TValue value, bool update)
    {
        if (!_batch) _stamp++;

        var item = new Leaf { Bits = TKey.Truncate(bits, length), Mask = TKey.MaskFor(length), Length = length, Entry = new Entry { Prefix = prefix, Value = value } };
        var path = new NodeStack();
        Span<int> octets = stackalloc int[17];
        var n = _w;
        var depth = 0;
        Node changed;
        var added = true;

        while (true)
        {
            var octet = TKey.GetBits(item.Bits, depth << 3, 8);
            var relative = length - (depth << 3);

            if (relative < 8)
            {
                var index = ArtTables.PrefixToIndex(octet, relative);
                if (n.Prefixes.Test(index))
                {
                    if (!update) return SetResult.Present;
                    changed = Writable(n);
                    changed.PrefixEntries = ReplaceAt(changed.PrefixEntries, changed.Prefixes.PopCount(), changed.Prefixes.Rank(index) - 1, in item.Entry, ref changed.Shared, PrefixArray);
                    added = false;
                    break;
                }

                changed = Writable(n);
                InsertPrefix(changed, index, in item.Entry);
                break;
            }

            if (relative == 8)
            {
                if (n.Fringes.Test(octet))
                {
                    if (!update) return SetResult.Present;
                    changed = Writable(n);
                    changed.FringeEntries = ReplaceAt(changed.FringeEntries, changed.Fringes.PopCount(), changed.Fringes.Rank(octet) - 1, in item.Entry, ref changed.Shared, FringeArray);
                    added = false;
                    break;
                }

                changed = Writable(n);
                InsertFringe(changed, octet, in item.Entry);
                break;
            }

            if (n.Kids.Test(octet))
            {
                path[depth] = n;
                octets[depth] = octet;
                n = n.KidNodes[n.Kids.Rank(octet) - 1];
                depth++;
                continue;
            }

            if (n.Leaves.Test(octet))
            {
                var existing = n.LeafEntries[n.Leaves.Rank(octet) - 1];
                if (existing.Length == length && TKey.MatchesUnderMask(item.Bits, existing.Bits, existing.Mask))
                {
                    if (!update) return SetResult.Present;
                    changed = Writable(n);
                    changed.LeafEntries = ReplaceAt(changed.LeafEntries, changed.Leaves.PopCount(), changed.Leaves.Rank(octet) - 1, in item, ref changed.Shared, LeafArray);
                    added = false;
                    break;
                }

                // a second prefix below this octet: both move into a new child node
                var child = new Node { Stamp = _stamp };
                AddPrivate(child, depth + 1, in existing);
                AddPrivate(child, depth + 1, in item);
                if (!_batch) Trim(child, _stamp); // published now, so no spare capacity
                changed = Writable(n);
                RemoveLeaf(changed, octet);
                InsertKid(changed, octet, child);
                break;
            }

            changed = Writable(n);
            InsertLeaf(changed, octet, in item);
            break;
        }

        if (added) changed.Count++;

        if (_batch)
        {
            Link(changed, n, ref path, octets, depth);
            if (added) AdjustCounts(in item.Bits, depth, 1);
        }
        else
        {
            Interlocked.Increment(ref _changes);
            try
            {
                Link(changed, n, ref path, octets, depth);
                UpdateTable(in item.Bits, depth);
            }
            finally
            {
                // Link never throws outside a batch, and UpdateTable can only fail to build a new block, which leaves
                // the block out and lookups right; so the counts are adjusted and the counter made even regardless
                if (added) AdjustPublishedCounts(ref path, depth, 1);
                Interlocked.Increment(ref _changes);
            }
        }

        return added ? SetResult.Added : SetResult.Updated;
    }

    private bool Delete(in TKey bits, int length)
    {
        if (!_batch) _stamp++;

        var key = TKey.Truncate(bits, length);
        var path = new NodeStack();
        Span<int> octets = stackalloc int[17];
        var n = _w;
        var depth = 0;
        Node changed;

        while (true)
        {
            var octet = TKey.GetBits(key, depth << 3, 8);
            var relative = length - (depth << 3);

            if (relative < 8)
            {
                var index = ArtTables.PrefixToIndex(octet, relative);
                if (!n.Prefixes.Test(index)) return false;
                changed = Writable(n);
                RemovePrefix(changed, index);
                break;
            }

            if (relative == 8)
            {
                if (!n.Fringes.Test(octet)) return false;
                changed = Writable(n);
                RemoveFringe(changed, octet);
                break;
            }

            if (n.Kids.Test(octet))
            {
                path[depth] = n;
                octets[depth] = octet;
                n = n.KidNodes[n.Kids.Rank(octet) - 1];
                depth++;
                continue;
            }

            if (!n.Leaves.Test(octet)) return false;
            ref var leaf = ref n.LeafEntries[n.Leaves.Rank(octet) - 1];
            if (leaf.Length != length || !TKey.MatchesUnderMask(key, leaf.Bits, leaf.Mask)) return false;
            changed = Writable(n);
            RemoveLeaf(changed, octet);
            break;
        }

        changed.Count--;

        // keep the shape canonical: a child holding one prefix becomes a leaf in its parent, an empty child goes away,
        // and the parent may then need the same
        var original = n;
        while (depth > 0 && changed.Count <= 1)
        {
            var parent = path[depth - 1];
            var octet = octets[depth - 1];
            var parentCopy = Writable(parent);
            parentCopy.Count--;
            RemoveKid(parentCopy, octet);
            if (changed.Count == 1)
            {
                var single = SingleItem(changed, depth, in key);
                InsertLeaf(parentCopy, octet, in single);
            }

            changed = parentCopy;
            original = parent;
            depth--;
        }

        if (_batch)
        {
            Link(changed, original, ref path, octets, depth);
            AdjustCounts(in key, depth, -1);
        }
        else
        {
            Interlocked.Increment(ref _changes);
            try
            {
                Link(changed, original, ref path, octets, depth);
                UpdateTable(in key, depth);
            }
            finally
            {
                // as in Set
                AdjustPublishedCounts(ref path, depth, -1);
                Interlocked.Increment(ref _changes);
            }
        }

        return true;
    }

    // a node the current write may change in place: itself if this write or batch created it, otherwise a copy
    private Node Writable(Node n) => n.Stamp == _stamp ? n : n.Copy(_stamp);

    // publishes a changed node in place of the original with one reference write
    private void Link(Node changed, Node original, ref NodeStack path, Span<int> octets, int depth)
    {
        while (changed != original)
        {
            if (depth == 0)
            {
                _w = changed;
                if (!_batch) Volatile.Write(ref _published.Root, changed);
                return;
            }

            var parent = path[depth - 1];
            var at = parent.Kids.Rank(octets[depth - 1]) - 1;
            if (_batch && parent.Stamp != _stamp)
            {
                // during a batch readers must not see any change yet, so the parents are copied up to the root
                var copy = Writable(parent);
                OwnKidArray(copy);
                copy.KidNodes[at] = changed;
                changed = copy;
                original = parent;
                depth--;
                continue;
            }

            // a parent this write or batch copied may still share its child array with the published original
            if (parent.Stamp == _stamp) OwnKidArray(parent);
            Volatile.Write(ref parent.KidNodes[at], changed);
            return;
        }
    }

    // Outside a batch the counts of the published nodes above a change are adjusted after it is linked in, so that Count
    // changes only once lookups see the change.
    private static void AdjustPublishedCounts(ref NodeStack path, int depth, int delta)
    {
        for (var i = 0; i < depth; i++)
            Volatile.Write(ref path[i].Count, path[i].Count + delta);
    }

    private static void OwnKidArray(Node n)
    {
        if ((n.Shared & KidArray) == 0) return;
        n.KidNodes = (Node[])n.KidNodes.Clone();
        n.Shared &= unchecked((byte)~KidArray);
    }

    // During a batch the counts of the nodes above a change are adjusted only after Link has made them private, so that
    // an aborted batch leaves the published nodes untouched. A private node's parents are always private.
    private void AdjustCounts(in TKey key, int depth, int delta)
    {
        var n = _w;
        for (var i = 0; i < depth; i++)
        {
            n.Count += delta;
            n = n.KidNodes[n.Kids.Rank(TKey.GetBits(key, i << 3, 8)) - 1];
        }
    }

    // adds an item below a node that this write created, changing nodes in place
    private void AddPrivate(Node n, int depth, in Leaf item)
    {
        while (true)
        {
            n.Count++;
            var octet = TKey.GetBits(item.Bits, depth << 3, 8);
            var relative = item.Length - (depth << 3);
            if (relative < 8)
            {
                InsertPrefix(n, ArtTables.PrefixToIndex(octet, relative), in item.Entry);
                return;
            }

            if (relative == 8)
            {
                InsertFringe(n, octet, in item.Entry);
                return;
            }

            if (n.Kids.Test(octet))
            {
                n = n.KidNodes[n.Kids.Rank(octet) - 1];
                depth++;
                continue;
            }

            if (n.Leaves.Test(octet))
            {
                var existing = n.LeafEntries[n.Leaves.Rank(octet) - 1];
                var child = new Node { Stamp = _stamp };
                AddPrivate(child, depth + 1, in existing);
                RemoveLeaf(n, octet);
                InsertKid(n, octet, child);
                n = child;
                depth++;
                continue;
            }

            InsertLeaf(n, octet, in item);
            return;
        }
    }

    // the one prefix left in a node at the given depth, which lies on the path of key, as a leaf
    private static Leaf SingleItem(Node n, int depth, in TKey key)
    {
        if (!n.Leaves.IsEmpty)
            return n.LeafEntries[0];

        var start = TKey.Truncate(key, depth << 3);
        int length;
        TKey bits;
        Entry entry;
        if (!n.Fringes.IsEmpty)
        {
            bits = TKey.SetBits(start, depth << 3, 8, n.Fringes.First());
            length = (depth << 3) + 8;
            entry = n.FringeEntries[0];
        }
        else
        {
            var index = n.Prefixes.First();
            var relative = ArtTables.IndexToLength(index);
            bits = relative == 0 ? start : TKey.SetBits(start, depth << 3, relative, index - (1 << relative));
            length = (depth << 3) + relative;
            entry = n.PrefixEntries[0];
        }

        return new Leaf { Bits = bits, Mask = TKey.MaskFor(length), Length = length, Entry = entry };
    }

    // ---- shortcut table --------------------------------------------------------------------------------------------

    // after a write that replaced the node at the given depth on the path of key
    private void UpdateTable(in TKey key, int depth)
    {
        var blocks = _published.Blocks;
        if (blocks == null || depth > 2) return;
        var first = TKey.GetBits(key, 0, 8);
        var depth1 = Kid(_w, first);
        ref var slot = ref blocks[first];
        var block = slot;
        if (depth1 == null)
        {
            if (block != null) Volatile.Write(ref slot, null);
            return;
        }

        if (block == null)
        {
            Volatile.Write(ref slot, NewBlock(depth1));
            return;
        }

        if (depth == 2)
        {
            var second = TKey.GetBits(key, 8, 8);
            Volatile.Write(ref block.Depth2[second], Kid(depth1, second));
            return;
        }

        // a change at the root leaves the depth-1 node alone; a change at depth 1 replaced it with a copy whose children
        // may differ
        if (block.Depth1 == depth1) return;
        Volatile.Write(ref block.Depth1, depth1);
        for (var second = 0; second < 256; second++)
        {
            var depth2 = Kid(depth1, second);
            if (block.Depth2[second] != depth2) Volatile.Write(ref block.Depth2[second], depth2);
        }
    }

    // a block filled before readers can see it
    private static Block NewBlock(Node depth1)
    {
        var block = new Block(depth1);
        for (var second = 0; second < 256; second++)
            block.Depth2[second] = Kid(depth1, second);
        return block;
    }

    private static Node? Kid(Node n, int octet) => n.Kids.Test(octet) ? n.KidNodes[n.Kids.Rank(octet) - 1] : null;

    // ---- node arrays -----------------------------------------------------------------------------------------------

    private static void InsertPrefix(Node n, int index, in Entry entry)
    {
        n.PrefixEntries = InsertAt(n.PrefixEntries, n.Prefixes.PopCount(), n.Prefixes.Rank(index), in entry, ref n.Shared, PrefixArray);
        n.Prefixes.Set(index);
    }

    private static void RemovePrefix(Node n, int index)
    {
        n.PrefixEntries = RemoveAt(n.PrefixEntries, n.Prefixes.PopCount(), n.Prefixes.Rank(index) - 1, ref n.Shared, PrefixArray);
        n.Prefixes.Clear(index);
    }

    private static void InsertFringe(Node n, int octet, in Entry entry)
    {
        n.FringeEntries = InsertAt(n.FringeEntries, n.Fringes.PopCount(), n.Fringes.Rank(octet), in entry, ref n.Shared, FringeArray);
        n.Fringes.Set(octet);
    }

    private static void RemoveFringe(Node n, int octet)
    {
        n.FringeEntries = RemoveAt(n.FringeEntries, n.Fringes.PopCount(), n.Fringes.Rank(octet) - 1, ref n.Shared, FringeArray);
        n.Fringes.Clear(octet);
    }

    private static void InsertKid(Node n, int octet, Node kid)
    {
        n.KidNodes = InsertAt(n.KidNodes, n.Kids.PopCount(), n.Kids.Rank(octet), in kid, ref n.Shared, KidArray);
        n.Kids.Set(octet);
    }

    private static void RemoveKid(Node n, int octet)
    {
        n.KidNodes = RemoveAt(n.KidNodes, n.Kids.PopCount(), n.Kids.Rank(octet) - 1, ref n.Shared, KidArray);
        n.Kids.Clear(octet);
    }

    private static void InsertLeaf(Node n, int octet, in Leaf leaf)
    {
        n.LeafEntries = InsertAt(n.LeafEntries, n.Leaves.PopCount(), n.Leaves.Rank(octet), in leaf, ref n.Shared, LeafArray);
        n.Leaves.Set(octet);
    }

    private static void RemoveLeaf(Node n, int octet)
    {
        n.LeafEntries = RemoveAt(n.LeafEntries, n.Leaves.PopCount(), n.Leaves.Rank(octet) - 1, ref n.Shared, LeafArray);
        n.Leaves.Clear(octet);
    }

    // The Insert helpers above allocate before they set the bit, so a failed allocation leaves the node as it was.
    //
    // An array still shared with another node, or one readers can see, is replaced by an exact-size copy. An array only
    // this writer can see grows by doubling and changes in place. Array.Empty is shared by every node, so it is never
    // changed in place: with no spare capacity, the first insert always allocates.
    private static T[] InsertAt<T>(T[] array, int count, int at, in T item, ref byte shared, byte which)
    {
        var isShared = (shared & which) != 0;
        if (!isShared && count < array.Length)
        {
            Array.Copy(array, at, array, at + 1, count - at);
            array[at] = item;
            return array;
        }

        var result = new T[isShared ? count + 1 : Math.Max(4, count * 2)];
        Array.Copy(array, 0, result, 0, at);
        result[at] = item;
        Array.Copy(array, at, result, at + 1, count - at);
        shared &= (byte)~which;
        return result;
    }

    private static T[] RemoveAt<T>(T[] array, int count, int at, ref byte shared, byte which)
    {
        if ((shared & which) == 0)
        {
            Array.Copy(array, at + 1, array, at, count - at - 1);
            array[count - 1] = default!;
            return array;
        }

        shared &= (byte)~which;
        if (count == 1)
            return Array.Empty<T>();

        var result = new T[count - 1];
        Array.Copy(array, 0, result, 0, at);
        Array.Copy(array, at + 1, result, at, count - at - 1);
        return result;
    }

    private static T[] ReplaceAt<T>(T[] array, int count, int at, in T item, ref byte shared, byte which)
    {
        if ((shared & which) != 0)
        {
            array = array.AsSpan(0, count).ToArray();
            shared &= (byte)~which;
        }

        array[at] = item;
        return array;
    }

    // after a batch: exact-size arrays for the nodes the batch created, marked shared so later writes copy them
    private static void Trim(Node n, long stamp)
    {
        if (n.Stamp != stamp) return;
        n.PrefixEntries = Exact(n.PrefixEntries, n.Prefixes.PopCount());
        n.FringeEntries = Exact(n.FringeEntries, n.Fringes.PopCount());
        n.KidNodes = Exact(n.KidNodes, n.Kids.PopCount());
        n.LeafEntries = Exact(n.LeafEntries, n.Leaves.PopCount());
        n.Shared = AllArrays;
        foreach (var kid in n.KidNodes) Trim(kid, stamp);
    }

    private static T[] Exact<T>(T[] array, int count) => array.Length == count ? array : count == 0 ? Array.Empty<T>() : array[..count];

    private static void CheckLength(int length)
    {
        if ((uint)length > (uint)TKey.Width)
            throw new ArgumentOutOfRangeException(nameof(length), length, $"Prefix lengths range from 0 to {TKey.Width}.");
    }

    // ---- building from a sequence -----------------------------------------------------------------------------------

    // Fills this trie, which must be empty and outside a batch, with items in any order, and publishes the result as a
    // batch commit would. Adding them one at a time descends from the root for each, grows every array by doubling and
    // copies every array again when the batch commits; this builds each node once, with arrays of the exact size. It
    // reorders the items and truncates their bits in place.
    internal void Load(Span<(TKey Bits, int Length, TPrefix Prefix, TValue Value)> items)
    {
        using (_lock.EnterScope())
        {
            if (_batch || _w.Count != 0)
                throw new InvalidOperationException("Only an empty trie outside a batch can be loaded.");
            foreach (ref var item in items)
            {
                CheckLength(item.Length);
                item.Bits = TKey.Truncate(item.Bits, item.Length);
            }

            var root = Build(items, 0, new int[17 * 3 * 256]);
            var published = new Published(root, _hasTable);
            if (published.Blocks != null)
                for (var first = 0; first < 256; first++)
                    if (Kid(root, first) is { } depth1)
                        published.Blocks[first] = NewBlock(depth1);

            _w = root;
            Interlocked.Increment(ref _changes);
            Volatile.Write(ref _published, published);
            Interlocked.Increment(ref _changes);
        }
    }

    // The node at depth for items whose bits agree above it and whose lengths reach it: a most-significant-digit radix
    // sort by octet that builds the trie as it goes, in place, with no comparisons. The node's own prefixes and fringes
    // go first; the rest are moved into one run per octet, and a run of one becomes a leaf, a longer run a child node, as
    // the canonical shape requires. Two items with the same prefix end up in one node at the same base index or octet.
    // The node is stamped 0 and shares its arrays, as after a batch commit, so later writes copy it. Each depth has its
    // own counts, starts and fill points in scratch, all zero between nodes, and a node visits only the octets it
    // occupies: most nodes of an IPv6 table hold a handful of items, and visiting all 256 octets in each made the build
    // slower than adding the items one at a time.
    private static Node Build(Span<(TKey Bits, int Length, TPrefix Prefix, TValue Value)> items, int depth, int[] scratch)
    {
        var offset = depth << 3;
        var n = new Node { Count = items.Length, Shared = AllArrays };

        var own = 0;
        for (var i = 0; i < items.Length; i++)
            if (items[i].Length - offset <= 8)
            {
                (items[i], items[own]) = (items[own], items[i]);
                own++;
            }

        // the prefixes inside the stride and the fringes: first the bit sets, then each entry at its rank
        foreach (ref var item in items[..own])
        {
            var octet = TKey.GetBits(item.Bits, offset, 8);
            var relative = item.Length - offset;
            ref var set = ref relative < 8 ? ref n.Prefixes : ref n.Fringes;
            var bit = relative < 8 ? ArtTables.PrefixToIndex(octet, relative) : octet;
            if (set.Test(bit))
                throw new ArgumentException($"The prefix {item.Prefix} appears more than once.", nameof(items));
            set.Set(bit);
        }

        n.PrefixEntries = NewArray<Entry>(n.Prefixes.PopCount());
        n.FringeEntries = NewArray<Entry>(n.Fringes.PopCount());
        foreach (ref var item in items[..own])
        {
            var octet = TKey.GetBits(item.Bits, offset, 8);
            var relative = item.Length - offset;
            var entry = new Entry { Prefix = item.Prefix, Value = item.Value };
            if (relative < 8)
                n.PrefixEntries[n.Prefixes.Rank(ArtTables.PrefixToIndex(octet, relative)) - 1] = entry;
            else
                n.FringeEntries[n.Fringes.Rank(octet) - 1] = entry;
        }

        // the longer items, into one run per octet: count them, then move each into its run (American flag sort)
        var below = items[own..];
        var counts = scratch.AsSpan(depth * 768, 256);
        var starts = scratch.AsSpan(depth * 768 + 256, 256);
        var fill = scratch.AsSpan(depth * 768 + 512, 256);
        var occupied = default(BitSet256);
        var grouped = true; // already in octet order, as items in address order are, so no item needs to move
        var previous = 0;
        foreach (ref var item in below)
        {
            var octet = TKey.GetBits(item.Bits, offset, 8);
            if (counts[octet]++ == 0)
                occupied.Set(octet);
            grouped &= octet >= previous;
            previous = octet;
        }

        int kids = 0, leaves = 0, start = 0;
        for (var octet = occupied.First(); octet < 256; octet = occupied.NextSet(octet + 1))
        {
            if (counts[octet] == 1) leaves++;
            else kids++;
            starts[octet] = fill[octet] = start;
            start += counts[octet];
        }

        for (var octet = grouped ? 256 : occupied.First(); octet < 256; octet = occupied.NextSet(octet + 1))
            while (fill[octet] < starts[octet] + counts[octet])
            {
                var target = TKey.GetBits(below[fill[octet]].Bits, offset, 8);
                if (target == octet)
                {
                    fill[octet]++;
                    continue;
                }

                var to = fill[target]++;
                (below[fill[octet]], below[to]) = (below[to], below[fill[octet]]);
            }

        n.KidNodes = NewArray<Node>(kids);
        n.LeafEntries = NewArray<Leaf>(leaves);
        int k = 0, l = 0;
        for (var octet = occupied.First(); octet < 256; octet = occupied.NextSet(octet + 1))
        {
            var run = below.Slice(starts[octet], counts[octet]);
            counts[octet] = 0; // zero again for the next node at this depth; a child uses the next depth's scratch
            if (run.Length == 1)
            {
                ref var item = ref run[0];
                n.Leaves.Set(octet);
                n.LeafEntries[l++] = new Leaf { Bits = item.Bits, Mask = TKey.MaskFor(item.Length), Length = item.Length, Entry = new Entry { Prefix = item.Prefix, Value = item.Value } };
            }
            else
            {
                n.Kids.Set(octet);
                n.KidNodes[k++] = Build(run, depth + 1, scratch);
            }
        }

        return n;
    }

    private static T[] NewArray<T>(int count) => count == 0 ? Array.Empty<T>() : new T[count];

    // ---- freezing, statistics and diagnostics ----------------------------------------------------------------------

    /// <summary>
    ///     Builds a read-only copy with faster lookups, from the prefixes readers can see. Later changes to this trie do
    ///     not affect it. Writes wait while the prefixes are copied out, but not while the copy's tables are built; reads
    ///     never wait.
    /// </summary>
    /// <param name="options">The layout of the copy's lookup tables; by default the layout is chosen from the data.</param>
    public FrozenBitTrie<TKey, TPrefix, TValue> Freeze(FrozenBitTrieOptions? options = null)
    {
        FrozenBitTrie<TKey, TPrefix, TValue>.Contents contents;
        using (_lock.EnterScope())
            contents = CopyContents();
        return new FrozenBitTrie<TKey, TPrefix, TValue>(contents, options ?? new FrozenBitTrieOptions());
    }

    // The prefixes readers can see, for a frozen copy. The caller holds the writer lock: a single write stores child
    // references into published nodes, so a walk without the lock could see parts of two versions.
    internal FrozenBitTrie<TKey, TPrefix, TValue>.Contents CopyContents() => new(ItemsWithKeys(), Count);

    // the writer lock, for the IP lookup's Freeze, which holds both families' locks so that no batch commits in between
    internal Lock WriterLock => _lock;

    /// <summary>Counts the nodes and estimates the memory the trie holds.</summary>
    public BitTrieStatistics GetStatistics()
    {
        var published = Volatile.Read(ref _published);
        long nodes = 0, bytes = 0;
        if (published.Blocks != null)
        {
            bytes += 24L + 8L * published.Blocks.Length;
            foreach (var block in published.Blocks)
                if (block != null) bytes += 24L + 8L * 256;
        }
        var stack = new Stack<Node>();
        stack.Push(Volatile.Read(ref published.Root));
        while (stack.TryPop(out var n))
        {
            nodes++;
            bytes += 16 + 4 * 32 + 4 * 8 + 12;
            bytes += ArrayBytes(n.PrefixEntries.Length, Unsafe.SizeOf<Entry>()) + ArrayBytes(n.FringeEntries.Length, Unsafe.SizeOf<Entry>())
                   + ArrayBytes(n.KidNodes.Length, IntPtr.Size) + ArrayBytes(n.LeafEntries.Length, Unsafe.SizeOf<Leaf>());
            for (var i = n.Kids.PopCount() - 1; i >= 0; i--) stack.Push(n.KidNodes[i]);
        }

        return new BitTrieStatistics(published.Root.Count, (int)nodes, bytes);
    }

    private static long ArrayBytes(int length, int elementSize) => length == 0 ? 0 : 24 + (long)length * elementSize;

    /// <summary>
    ///     The structure of the trie as indented text: one line per node, prefix, fringe and leaf. The output is meant for
    ///     people and tests; equal text means equal shape.
    /// </summary>
    /// <param name="format">Formats a prefix given its bits and length.</param>
    public string ToTextTree(Func<TKey, int, string> format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var text = new StringBuilder();
        WriteText(Root, 0, default, format, text);
        return text.ToString();
    }

    private static void WriteText(Node n, int depth, TKey start, Func<TKey, int, string> format, StringBuilder text)
    {
        var indent = new string(' ', depth * 2);
        var offset = depth << 3;
        for (var index = 1; index < 256; index++)
        {
            if (!n.Prefixes.Test(index)) continue;
            var relative = ArtTables.IndexToLength(index);
            var bits = relative == 0 ? start : TKey.SetBits(start, offset, relative, index - (1 << relative));
            text.Append(indent).Append("prefix ").Append(format(bits, offset + relative)).AppendLine();
        }

        for (var octet = 0; octet < 256; octet++)
        {
            var below = TKey.SetBits(start, offset, 8, octet);
            if (n.Fringes.Test(octet))
                text.Append(indent).Append("fringe ").Append(format(below, offset + 8)).AppendLine();
            if (n.Leaves.Test(octet))
            {
                var leaf = n.LeafEntries[n.Leaves.Rank(octet) - 1];
                text.Append(indent).Append("leaf ").Append(format(leaf.Bits, leaf.Length)).AppendLine();
            }

            if (n.Kids.Test(octet))
            {
                var kid = n.KidNodes[n.Kids.Rank(octet) - 1];
                text.Append(indent).Append("node ").Append(format(below, offset + 8)).Append(", ").Append(kid.Count).Append(kid.Count == 1 ? " prefix" : " prefixes").AppendLine();
                WriteText(kid, depth + 1, below, format, text);
            }
        }
    }

    /// <summary>The structure of the trie in Graphviz DOT format, for rendering with <c>dot</c>.</summary>
    /// <param name="format">Formats a prefix given its bits and length.</param>
    public string ToDotTree(Func<TKey, int, string> format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var text = new StringBuilder();
        text.AppendLine("digraph trie {");
        text.AppendLine("  node [shape=box, fontname=\"monospace\"];");
        var id = 0;
        WriteDot(Root, 0, default, format, text, ref id, "root");
        text.AppendLine("}");
        return text.ToString();
    }

    private static int WriteDot(Node n, int depth, TKey start, Func<TKey, int, string> format, StringBuilder text, ref int id, string title)
    {
        var self = id++;
        var offset = depth << 3;
        var label = new StringBuilder(Escape(title));
        for (var index = 1; index < 256; index++)
        {
            if (!n.Prefixes.Test(index)) continue;
            var relative = ArtTables.IndexToLength(index);
            var bits = relative == 0 ? start : TKey.SetBits(start, offset, relative, index - (1 << relative));
            label.Append("\\l").Append(Escape(format(bits, offset + relative)));
        }

        for (var octet = 0; octet < 256; octet++)
            if (n.Fringes.Test(octet))
                label.Append("\\l").Append(Escape(format(TKey.SetBits(start, offset, 8, octet), offset + 8)));
        text.Append("  n").Append(self).Append(" [label=\"").Append(label).AppendLine("\\l\"];");

        for (var octet = 0; octet < 256; octet++)
        {
            if (n.Leaves.Test(octet))
            {
                var leaf = n.LeafEntries[n.Leaves.Rank(octet) - 1];
                var leafId = id++;
                text.Append("  n").Append(leafId).Append(" [shape=ellipse, label=\"").Append(Escape(format(leaf.Bits, leaf.Length))).AppendLine("\"];");
                text.Append("  n").Append(self).Append(" -> n").Append(leafId).AppendLine(";");
            }

            if (n.Kids.Test(octet))
            {
                var below = TKey.SetBits(start, offset, 8, octet);
                var kid = WriteDot(n.KidNodes[n.Kids.Rank(octet) - 1], depth + 1, below, format, text, ref id, format(below, offset + 8));
                text.Append("  n").Append(self).Append(" -> n").Append(kid).AppendLine(";");
            }
        }

        return self;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>The size of a <see cref="BitTrie{TKey,TPrefix,TValue}" />.</summary>
/// <param name="Prefixes">The number of stored prefixes.</param>
/// <param name="Nodes">The number of trie nodes.</param>
/// <param name="Bytes">An estimate of the memory held by the nodes, their arrays and the shortcut table.</param>
public readonly record struct BitTrieStatistics(int Prefixes, int Nodes, long Bytes);
