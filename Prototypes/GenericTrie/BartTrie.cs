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

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace GenericTrie;

/// <summary>
///     A read-write core in the style of Go's BART, generic over <see cref="IBitKey{TSelf}" />, to decide the
///     redesign's storage. See Documentation/Bart-Comparison.md.
/// </summary>
/// <remarks>
///     <para>
///         The trie has 8-bit strides. A node keeps four 256-bit sets, each with a popcount-compressed array: prefixes
///         that end inside its stride, as Knuth's ART base indexes 1 to 255 for relative lengths /0 to /7; fringes,
///         prefixes that end exactly at the end of the stride, by octet; child nodes, by octet; and leaves, lone longer
///         prefixes stored whole, by octet.
///     </para>
///     <para>
///         The shape is canonical: a child node exists at an octet exactly when two or more prefixes lie below it,
///         and a leaf when one does. Fringes stay in their node whatever happens below them.
///     </para>
///     <para>
///         Readers take no locks. A write copies the one node it changes, finishes the copy, and links it in with one
///         reference write into the parent's child array, or into the root field. Readers see the old node or the new
///         one. Replaced nodes become garbage that the .NET garbage collector frees once no reader holds them, so
///         there is no compaction and no array that grows in place while readers can see it. Nodes created within one
///         write, or during a bulk load, are private and changed in place. Only one writer at a time is supported.
///     </para>
/// </remarks>
public sealed class BartTrie<TKey, TPrefix, TValue> where TKey : struct, IBitKey<TKey>
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
        // read by lookups
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
        public int Stamp;   // the write, or bulk load, that created this node; it may change it in place
        public byte Shared;

        public Node Copy(int stamp)
        {
            var copy = (Node)MemberwiseClone();
            copy.Stamp = stamp;
            copy.Shared = AllArrays;
            return copy;
        }
    }

    // one slot per level of a 128-bit key and the root; wider keys need a larger stack (experiment)
    [InlineArray(17)]
    private struct NodeStack
    {
        private Node _element;
    }

    // Shortcuts for the first 16 bits: for each /16 block, the node at depth 1 and the node at depth 2 on its path,
    // or null. Lookups start there instead of at the root. A write that replaces a node at depth 1 or 2 updates the
    // slots that point at it. A reader may briefly combine nodes of different versions; since a write changes one
    // prefix, the result is still the answer from before or after that write.
    private struct Slot
    {
        public Node? Depth1;
        public Node? Depth2;
    }

    private readonly Slot[]? _table;

    private Node _root;      // what readers see
    private Node _w;         // what the writer works on; ahead of _root only during a bulk load
    private readonly int _width;
    private int _stamp = 1;  // nodes stamped 0 at construction count as published
    private bool _bulk;
    private long _work;      // array elements copied and nodes copied, for measuring work per write

    /// <summary>Creates an empty trie for keys of <paramref name="width" /> bits, a multiple of 8.</summary>
    public BartTrie(int width)
    {
        if (width % 8 != 0 || width > 256) throw new ArgumentOutOfRangeException(nameof(width));
        _width = width;
        _root = _w = new Node();
        if (width >= 16) _table = new Slot[1 << 16];
    }

    /// <summary>Number of stored prefixes.</summary>
    public long Count { get; private set; }

    /// <summary>Returns the array elements and nodes copied since the last call. For measurements only.</summary>
    public long TakeWork()
    {
        var work = _work;
        _work = 0;
        return work;
    }

    // ---- reading ---------------------------------------------------------------------------------------------------

    /// <summary>Finds the longest stored prefix that contains <paramref name="key" />.</summary>
    public bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value)
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

    private ref Entry Find(in TKey key)
    {
        var stack = new NodeStack();
        var n = Volatile.Read(ref _root);
        var depth = 0;

        var table = _table;
        if (table != null)
        {
            ref var slot = ref table[TKey.GetTopBits(key, 16)];
            var depth1 = slot.Depth1;
            var depth2 = slot.Depth2;
            if (depth1 != null)
            {
                stack[0] = n;
                n = depth1;
                depth = 1;
                if (depth2 != null)
                {
                    stack[1] = n;
                    n = depth2;
                    depth = 2;
                }
            }
        }

        // descend as far as the key leads; a fringe or leaf below the last node ends the search
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
            if (n.Prefixes.AndTop(in ArtTables.Ancestors[(octet >> 1) + 128], out var top))
                return ref n.PrefixEntries[n.Prefixes.Rank(top) - 1];
            if (depth == 0)
                return ref Unsafe.NullRef<Entry>();
            n = stack[--depth];
        }
    }

    // ---- writing ---------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Builds the following changes privately, and publishes them all at once in <see cref="EndBulkLoad" />.
    ///     Readers keep seeing the table as it was until then.
    /// </summary>
    public void BeginBulkLoad()
    {
        _stamp++;
        _bulk = true;
    }

    /// <summary>Trims the arrays of the nodes the bulk load created, then publishes them with one write.</summary>
    public void EndBulkLoad()
    {
        Trim(_w, _stamp);
        _bulk = false;
        _stamp++;
        Volatile.Write(ref _root, _w);
        if (_table != null)
            for (var first = 0; first < 256; first++)
                RefreshTable(first);
    }

    /// <summary>Adds a prefix. Returns false, and changes nothing, when it is already present.</summary>
    public bool TryAdd(in TKey bits, int length, TPrefix prefix, TValue value)
    {
        if ((uint)length > (uint)_width) throw new ArgumentOutOfRangeException(nameof(length));
        if (!_bulk) _stamp++;

        var item = new Leaf { Bits = TKey.Truncate(bits, length), Mask = TKey.MaskFor(length), Length = length, Entry = new Entry { Prefix = prefix, Value = value } };
        var path = new NodeStack();
        Span<int> octets = stackalloc int[32];
        var n = _w;
        var depth = 0;
        Node changed;

        while (true)
        {
            var octet = TKey.GetBits(item.Bits, depth << 3, 8);
            var relative = length - (depth << 3);

            if (relative < 8)
            {
                var index = ArtTables.PrefixToIndex(octet, relative);
                if (n.Prefixes.Test(index)) return false;
                changed = Writable(n);
                InsertPrefix(changed, index, item.Entry);
                break;
            }

            if (relative == 8)
            {
                if (n.Fringes.Test(octet)) return false;
                changed = Writable(n);
                InsertFringe(changed, octet, item.Entry);
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
                if (existing.Length == length && TKey.MatchesUnderMask(item.Bits, existing.Bits, existing.Mask)) return false;

                // a second prefix below this octet: both move into a new child node
                var child = new Node { Stamp = _stamp };
                AddPrivate(child, depth + 1, in existing);
                AddPrivate(child, depth + 1, in item);
                changed = Writable(n);
                RemoveLeaf(changed, octet);
                InsertKid(changed, octet, child);
                break;
            }

            changed = Writable(n);
            InsertLeaf(changed, octet, in item);
            break;
        }

        changed.Count++;
        for (var i = 0; i < depth; i++) path[i].Count++;
        Link(changed, n, ref path, octets, depth);
        UpdateTable(in item.Bits, depth);
        Count++;
        return true;
    }

    /// <summary>Removes a prefix. Returns false when it is not present.</summary>
    public bool TryRemove(in TKey bits, int length)
    {
        if ((uint)length > (uint)_width) throw new ArgumentOutOfRangeException(nameof(length));
        if (!_bulk) _stamp++;

        var key = TKey.Truncate(bits, length);
        var path = new NodeStack();
        Span<int> octets = stackalloc int[32];
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

        for (var i = 0; i < depth; i++) path[i].Count--;
        Link(changed, original, ref path, octets, depth);
        UpdateTable(in key, depth);
        Count--;
        return true;
    }

    // after a write that replaced the node at the given depth on the path of key
    private void UpdateTable(in TKey key, int depth)
    {
        if (_table == null || _bulk || depth > 2) return;
        var first = TKey.GetBits(key, 0, 8);
        if (depth == 2) RefreshSlot(first, TKey.GetBits(key, 8, 8), Kid(_w, first));
        else RefreshTable(first);
    }

    private void RefreshTable(int first)
    {
        var depth1 = Kid(_w, first);
        for (var second = 0; second < 256; second++)
            RefreshSlot(first, second, depth1);
    }

    private void RefreshSlot(int first, int second, Node? depth1)
    {
        ref var slot = ref _table![(first << 8) | second];
        var depth2 = depth1 == null ? null : Kid(depth1, second);

        // a reader that finds a node at depth 2 must also find one at depth 1, so set depth 1 first and clear it last
        if (depth2 == null)
        {
            Volatile.Write(ref slot.Depth2, null);
            Volatile.Write(ref slot.Depth1, depth1);
        }
        else
        {
            Volatile.Write(ref slot.Depth1, depth1);
            Volatile.Write(ref slot.Depth2, depth2);
        }
    }

    private static Node? Kid(Node n, int octet) => n.Kids.Test(octet) ? n.KidNodes[n.Kids.Rank(octet) - 1] : null;

    // a node the current write may change in place: itself if this write created it, otherwise a copy
    private Node Writable(Node n)
    {
        if (n.Stamp == _stamp) return n;
        _work++;
        return n.Copy(_stamp);
    }

    // publishes a changed node in place of the original with one reference write
    private void Link(Node changed, Node original, ref NodeStack path, Span<int> octets, int depth)
    {
        while (changed != original)
        {
            if (depth == 0)
            {
                _w = changed;
                if (!_bulk) Volatile.Write(ref _root, changed);
                return;
            }

            var parent = path[depth - 1];
            var at = parent.Kids.Rank(octets[depth - 1]) - 1;
            if (_bulk && parent.Stamp != _stamp)
            {
                // during a bulk load readers must not see any change yet, so the parents are copied up to the root
                var copy = Writable(parent);
                OwnKidArray(copy);
                copy.KidNodes[at] = changed;
                changed = copy;
                original = parent;
                depth--;
                continue;
            }

            // a parent this write or bulk load copied may still share its child array with the published original
            if (parent.Stamp == _stamp) OwnKidArray(parent);
            Volatile.Write(ref parent.KidNodes[at], changed);
            return;
        }
    }

    private void OwnKidArray(Node n)
    {
        if ((n.Shared & KidArray) == 0) return;
        n.KidNodes = (Node[])n.KidNodes.Clone();
        n.Shared &= unchecked((byte)~KidArray);
        _work += n.KidNodes.Length;
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
                InsertPrefix(n, ArtTables.PrefixToIndex(octet, relative), item.Entry);
                return;
            }

            if (relative == 8)
            {
                InsertFringe(n, octet, item.Entry);
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
            var relative = 31 - BitOperations.LeadingZeroCount((uint)index);
            bits = relative == 0 ? start : TKey.SetBits(start, depth << 3, relative, index - (1 << relative));
            length = (depth << 3) + relative;
            entry = n.PrefixEntries[0];
        }

        return new Leaf { Bits = bits, Mask = TKey.MaskFor(length), Length = length, Entry = entry };
    }

    // ---- node arrays -----------------------------------------------------------------------------------------------

    private void InsertPrefix(Node n, int index, in Entry entry)
    {
        var count = n.Prefixes.PopCount();
        n.Prefixes.Set(index);
        n.PrefixEntries = InsertAt(n.PrefixEntries, count, n.Prefixes.Rank(index) - 1, entry, ref n.Shared, PrefixArray);
    }

    private void RemovePrefix(Node n, int index)
    {
        var at = n.Prefixes.Rank(index) - 1;
        n.PrefixEntries = RemoveAt(n.PrefixEntries, n.Prefixes.PopCount(), at, ref n.Shared, PrefixArray);
        n.Prefixes.Clear(index);
    }

    private void InsertFringe(Node n, int octet, in Entry entry)
    {
        var count = n.Fringes.PopCount();
        n.Fringes.Set(octet);
        n.FringeEntries = InsertAt(n.FringeEntries, count, n.Fringes.Rank(octet) - 1, entry, ref n.Shared, FringeArray);
    }

    private void RemoveFringe(Node n, int octet)
    {
        var at = n.Fringes.Rank(octet) - 1;
        n.FringeEntries = RemoveAt(n.FringeEntries, n.Fringes.PopCount(), at, ref n.Shared, FringeArray);
        n.Fringes.Clear(octet);
    }

    private void InsertKid(Node n, int octet, Node kid)
    {
        var count = n.Kids.PopCount();
        n.Kids.Set(octet);
        n.KidNodes = InsertAt(n.KidNodes, count, n.Kids.Rank(octet) - 1, kid, ref n.Shared, KidArray);
    }

    private void RemoveKid(Node n, int octet)
    {
        var at = n.Kids.Rank(octet) - 1;
        n.KidNodes = RemoveAt(n.KidNodes, n.Kids.PopCount(), at, ref n.Shared, KidArray);
        n.Kids.Clear(octet);
    }

    private void InsertLeaf(Node n, int octet, in Leaf leaf)
    {
        var count = n.Leaves.PopCount();
        n.Leaves.Set(octet);
        n.LeafEntries = InsertAt(n.LeafEntries, count, n.Leaves.Rank(octet) - 1, leaf, ref n.Shared, LeafArray);
    }

    private void RemoveLeaf(Node n, int octet)
    {
        var at = n.Leaves.Rank(octet) - 1;
        n.LeafEntries = RemoveAt(n.LeafEntries, n.Leaves.PopCount(), at, ref n.Shared, LeafArray);
        n.Leaves.Clear(octet);
    }

    // An array still shared with another node, or one that readers can see, is replaced by an exact-size copy. An array
    // that only this writer can see grows by doubling and changes in place.
    private T[] InsertAt<T>(T[] array, int count, int at, in T item, ref byte shared, byte which)
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
        _work += count + 1;
        return result;
    }

    private T[] RemoveAt<T>(T[] array, int count, int at, ref byte shared, byte which)
    {
        if ((shared & which) == 0)
        {
            Array.Copy(array, at + 1, array, at, count - at - 1);
            array[count - 1] = default!;
            return array;
        }

        if (count == 1)
        {
            shared &= (byte)~which;
            return Array.Empty<T>();
        }

        var result = new T[count - 1];
        Array.Copy(array, 0, result, 0, at);
        Array.Copy(array, at + 1, result, at, count - at - 1);
        shared &= (byte)~which;
        _work += count - 1;
        return result;
    }

    // An empty array is shared by every node, so a node that holds one must never change it in place. Marking it
    // shared makes the next insert allocate.
    private static void Trim(Node n, int stamp)
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

    // ---- diagnostics -----------------------------------------------------------------------------------------------

    /// <summary>The shape of the trie as text: equal text means equal shape. For tests.</summary>
    public string Dump(Func<TKey, int, string> format)
    {
        var text = new StringBuilder();
        Dump(_w, 0, default, format, text);
        return text.ToString();
    }

    private static void Dump(Node n, int depth, TKey start, Func<TKey, int, string> format, StringBuilder text)
    {
        var indent = new string(' ', depth);
        for (var index = 1; index < 256; index++)
            if (n.Prefixes.Test(index))
            {
                var relative = 31 - BitOperations.LeadingZeroCount((uint)index);
                var bits = relative == 0 ? start : TKey.SetBits(start, depth << 3, relative, index - (1 << relative));
                text.Append(indent).Append("p ").AppendLine(format(bits, (depth << 3) + relative));
            }

        for (var octet = 0; octet < 256; octet++)
        {
            var below = TKey.SetBits(start, depth << 3, 8, octet);
            if (n.Fringes.Test(octet))
                text.Append(indent).Append("f ").AppendLine(format(below, (depth << 3) + 8));
            if (n.Leaves.Test(octet))
            {
                var leaf = n.LeafEntries[n.Leaves.Rank(octet) - 1];
                text.Append(indent).Append("l ").AppendLine(format(leaf.Bits, leaf.Length));
            }

            if (n.Kids.Test(octet))
            {
                var kid = n.KidNodes[n.Kids.Rank(octet) - 1];
                text.Append(indent).Append("n ").Append(format(below, (depth << 3) + 8)).Append(' ').Append(kid.Count).AppendLine();
                Dump(kid, depth + 1, below, format, text);
            }
        }
    }
}

/// <summary>Tables for ART base indexes within one 8-bit stride.</summary>
public static class ArtTables
{
    /// <summary>The base index of a prefix of <paramref name="relativeLength" /> bits, 0 to 7, within a stride.</summary>
    public static int PrefixToIndex(int octet, int relativeLength) => (octet >> (8 - relativeLength)) + (1 << relativeLength);

    /// <summary>Ancestors[i] holds index i and every index above it: i, i/2, i/4, ..., 1.</summary>
    public static readonly BitSet256[] Ancestors = Build();

    private static BitSet256[] Build()
    {
        var table = new BitSet256[256];
        for (var i = 1; i < 256; i++)
            for (var j = i; j > 0; j >>= 1)
                table[i].Set(j);
        return table;
    }
}
