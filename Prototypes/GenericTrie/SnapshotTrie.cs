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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GenericTrie;

/// <summary>
///     A production-style version of the optimized core, to test the behaviors the experimental classes skip: removal,
///     index maintenance on every change, and lock-free readers alongside one writer.
/// </summary>
/// <remarks>
///     <para>
///         Everything a lookup reads lives in one <see cref="State" /> snapshot: the node array, prefix lengths, the
///         prefix-and-value array and the multi-level index. A reader reads the snapshot reference once and uses only
///         that snapshot, so it never mixes arrays from two versions.
///     </para>
///     <para>
///         The writer changes the current snapshot in place, but only in ways a reader can tolerate: a new node, value or
///         sub-table is written completely while nothing refers to it, then linked with a single atomic write. When any
///         array is full, the writer copies every array into a larger snapshot and publishes it with one reference write;
///         readers on the old snapshot keep a consistent, slightly older view. Removed nodes, values and sub-tables are
///         never reused in place, because a reader may still be on them; <see cref="Compact" /> reclaims them by building
///         a fresh snapshot.
///     </para>
///     <para>
///         Only one thread may write at a time. Any number of threads may read at any time.
///     </para>
/// </remarks>
public sealed class SnapshotTrie<TKey, TPrefix, TValue> where TKey : struct, IBitKey<TKey>
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Node
    {
        public TKey Bits;
        public TKey Mask;
        public TKey Split;
        public int Child0; // 0 is the sentinel, which never matches
        public int Child1;
        public int Value;  // index into Entries, or -1 for a routing node
    }

    private struct Entry
    {
        public TPrefix Prefix;
        public TValue Value;
    }

    private sealed class State
    {
        public Node[] Nodes = null!;
        public ushort[] Lengths = null!; // cold: prefix length per node, for writers
        public Entry[] Entries = null!;
        public ulong[] Root = null!;     // multi-level index root; empty when there is no index
        public ulong[] Subs = null!;     // sub-tables, concatenated
        public int NodeCount;
        public int ValueCount;
        public int SubCount;             // entries used in Subs
    }

    private const int Sentinel = 0;
    private const int RootNode = 1;
    private const uint SubTableFlag = 0x8000_0000;

    private readonly int _rootBits;
    private readonly int _subBits;
    private readonly int _threshold;
    private readonly int _keyWidth;

    private State _state;    // what readers see
    private State _w;        // what the writer works on; the same object as _state except while compacting
    private bool _publishing = true;
    private bool _maintainIndex = true;
    private readonly Stack<int> _scratch = new(); // reused by HasMoreNodesBelow

    /// <summary>Creates an empty trie.</summary>
    /// <param name="keyWidth">The key width in bits, such as 32 for IPv4.</param>
    /// <param name="rootBits">Bits covered by the index root table, or 0 for no index.</param>
    /// <param name="subBits">Bits covered by each sub-table.</param>
    /// <param name="threshold">A block gets a sub-table when more than this many nodes lie below its entry.</param>
    public SnapshotTrie(int keyWidth, int rootBits = 16, int subBits = 8, int threshold = 8)
    {
        _keyWidth = keyWidth;
        _rootBits = rootBits;
        _subBits = subBits;
        _threshold = threshold;
        _w = NewState(64, 64, rootBits > 0 ? 1 << subBits : 0);
        InitializeFixedNodes(_w);
        if (rootBits > 0)
            RebuildIndex();
        _state = _w;
    }

    /// <summary>Number of stored prefixes.</summary>
    public long Count { get; private set; }

    /// <summary>Nodes, values and sub-table entries that removals or index updates left unreachable.</summary>
    public (int Nodes, int Values, int SubEntries) Garbage => (_garbageNodes, _garbageValues, _garbageSubs);

    private int _garbageNodes;
    private int _garbageValues;
    private int _garbageSubs;

    // ---- reading ---------------------------------------------------------------------------------------------------

    /// <summary>Finds the longest stored prefix that contains <paramref name="key" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value)
    {
        var s = Volatile.Read(ref _state);
        var found = Find(s, key);
        if (found >= 0)
        {
            ref var entry = ref s.Entries[found];
            prefix = entry.Prefix;
            value = entry.Value;
            return true;
        }

        prefix = default!;
        value = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Find(State s, in TKey key)
    {
        ref var nodes = ref MemoryMarshal.GetArrayDataReference(s.Nodes);
        int at, found;

        var root = s.Root;
        if (root.Length > 0)
        {
            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(root), TKey.GetTopBits(key, _rootBits));
            var offset = _rootBits;
            ref var subs = ref MemoryMarshal.GetArrayDataReference(s.Subs);
            while (((uint)entry & SubTableFlag) != 0)
            {
                entry = Unsafe.Add(ref subs, (int)((uint)entry & ~SubTableFlag) + TKey.GetBits(key, offset, _subBits));
                offset += _subBits;
            }

            at = (int)(uint)entry;
            found = (int)(entry >> 32);
        }
        else
        {
            at = RootNode;
            found = -1;
        }

        while (true)
        {
            ref var n = ref Unsafe.Add(ref nodes, at);
            found = SelectBest(n.Value, found);
            var next = ChildAt(ref n, TKey.IsSet(key, n.Split));
            ref var c = ref Unsafe.Add(ref nodes, next);
            if (!TKey.MatchesUnderMask(key, c.Bits, c.Mask))
                return found;
            at = next;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChildAt(ref Node node, bool slot) => Unsafe.Add(ref node.Child0, slot ? 1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SelectBest(int value, int best)
    {
        var routing = value >> 31;
        return (best & routing) | (value & ~routing);
    }

    // ---- snapshot management ---------------------------------------------------------------------------------------

    private static State NewState(int nodeCapacity, int valueCapacity, int subCapacity) => new()
    {
        Nodes = new Node[nodeCapacity],
        Lengths = new ushort[nodeCapacity],
        Entries = new Entry[valueCapacity],
        Root = Array.Empty<ulong>(),
        Subs = new ulong[subCapacity]
    };

    private static void InitializeFixedNodes(State s)
    {
        // node 0: the sentinel. (key & 0) == bits is false for every key because its bits are not zero.
        s.Nodes[Sentinel] = new Node { Bits = TKey.FromTopBits(1, 1), Value = -1 };
        // node 1: the root, the zero-length prefix
        s.Nodes[RootNode] = new Node { Mask = TKey.MaskFor(0), Split = TKey.SplitFor(0), Value = -1 };
        s.NodeCount = 2;
    }

    // Makes room for the given number of additional nodes, values and sub-table entries, and returns the snapshot to
    // keep writing to. A larger array means a new snapshot. Which other arrays must be copied follows from what refers to
    // what: the index (root and sub-tables) refers to sub-tables and nodes, and nodes refer to nodes and values. An array
    // that refers to a grown array must be copied too; otherwise the writer would put references to new slots into an
    // array that readers of the old snapshot share, and those slots do not exist in their copy.
    //  - Sub-tables full: copy the root and the sub-tables. Nodes, lengths and values are shared.
    //  - Nodes or values full: copy everything except, when they did not grow, the values, which refer to nothing.
    private State Reserve(int nodes, int values, int subEntries)
    {
        var s = _w;
        var nodesFull = s.NodeCount + nodes > s.Nodes.Length;
        var valuesFull = s.ValueCount + values > s.Entries.Length;
        var subsFull = s.SubCount + subEntries > s.Subs.Length;
        if (!nodesFull && !valuesFull && !subsFull)
            return s;

        static int Grow(int capacity, int needed) { capacity = Math.Max(capacity, 1); while (capacity < needed) capacity *= 2; return capacity; }

        var next = new State
        {
            NodeCount = s.NodeCount,
            ValueCount = s.ValueCount,
            SubCount = s.SubCount,
            Root = (ulong[])s.Root.Clone()
        };

        next.Subs = new ulong[subsFull ? Grow(s.Subs.Length, s.SubCount + subEntries) : s.Subs.Length];
        Array.Copy(s.Subs, next.Subs, s.SubCount);

        if (nodesFull || valuesFull)
        {
            next.Nodes = new Node[nodesFull ? Grow(s.Nodes.Length, s.NodeCount + nodes) : s.Nodes.Length];
            next.Lengths = new ushort[next.Nodes.Length];
            Array.Copy(s.Nodes, next.Nodes, s.NodeCount);
            Array.Copy(s.Lengths, next.Lengths, s.NodeCount);
        }
        else
        {
            next.Nodes = s.Nodes;
            next.Lengths = s.Lengths;
        }

        if (valuesFull)
        {
            next.Entries = new Entry[Grow(s.Entries.Length, s.ValueCount + values)];
            Array.Copy(s.Entries, next.Entries, s.ValueCount);
        }
        else
        {
            next.Entries = s.Entries;
        }

        _w = next;
        if (_publishing)
            Volatile.Write(ref _state, next);
        return next;
    }

    // ---- writing ---------------------------------------------------------------------------------------------------

    /// <summary>Adds a prefix. Returns false when the prefix is already stored.</summary>
    public bool TryAdd(in TKey key, int length, TPrefix prefix, TValue value)
    {
        // at most two new nodes (the value node and a routing node) and one value
        var s = Reserve(2, 1, 0);
        var bits = TKey.Truncate(key, length);

        var parent = RootNode;
        while (true)
        {
            var next = ChildAt(ref s.Nodes[parent], TKey.IsSet(bits, s.Nodes[parent].Split));
            if (next == Sentinel || s.Lengths[next] > length || !TKey.MatchesUnderMask(bits, s.Nodes[next].Bits, s.Nodes[next].Mask))
                break;
            parent = next;
        }

        int changed; // the shortest node whose prefix changed; the index is refreshed over its range

        if (s.Lengths[parent] == length)
        {
            if (s.Nodes[parent].Value >= 0)
                return false;

            // routing node becomes a value node: write the value first, then publish its index with one write
            var v = NewValue(s, prefix, value);
            Volatile.Write(ref s.Nodes[parent].Value, v);
            changed = parent;
        }
        else
        {
            var slot = TKey.IsSet(bits, s.Nodes[parent].Split);
            var existing = ChildAt(ref s.Nodes[parent], slot);
            var node = NewNode(s, bits, length);
            s.Nodes[node].Value = NewValue(s, prefix, value);

            if (existing == Sentinel)
            {
                Link(s, parent, slot, node);
                changed = node;
            }
            else if (length < s.Lengths[existing] && TKey.MatchesUnderMask(s.Nodes[existing].Bits, bits, s.Nodes[node].Mask))
            {
                SetChildUnpublished(s, node, TKey.IsSet(s.Nodes[existing].Bits, s.Nodes[node].Split), existing);
                Link(s, parent, slot, node);
                changed = node;
            }
            else
            {
                var common = TKey.CommonPrefixLength(bits, s.Nodes[existing].Bits, Math.Min(length, s.Lengths[existing]));
                var routing = NewNode(s, TKey.Truncate(bits, common), common);
                SetChildUnpublished(s, routing, TKey.IsSet(bits, s.Nodes[routing].Split), node);
                SetChildUnpublished(s, routing, TKey.IsSet(s.Nodes[existing].Bits, s.Nodes[routing].Split), existing);
                Link(s, parent, slot, routing);
                changed = routing;
            }
        }

        Count++;
        RefreshIndex(changed);
        return true;
    }

    /// <summary>Removes exactly <paramref name="length" /> bits of <paramref name="key" />. Returns false when that prefix is not stored.</summary>
    public bool TryRemove(in TKey key, int length)
    {
        var s = _w;
        var bits = TKey.Truncate(key, length);

        // record the path, with the slot each node occupies in its parent
        Span<int> path = stackalloc int[_keyWidth + 2];
        Span<bool> slots = stackalloc bool[_keyWidth + 2];
        var depth = 0;
        path[depth++] = RootNode;
        while (true)
        {
            var current = path[depth - 1];
            var slot = TKey.IsSet(bits, s.Nodes[current].Split);
            var next = ChildAt(ref s.Nodes[current], slot);
            if (next == Sentinel || s.Lengths[next] > length || !TKey.MatchesUnderMask(bits, s.Nodes[next].Bits, s.Nodes[next].Mask))
                break;
            slots[depth] = slot;
            path[depth++] = next;
        }

        var target = path[depth - 1];
        if (s.Lengths[target] != length || s.Nodes[target].Value < 0)
            return false; // not stored, or only a routing node

        // demote to a routing node with one write; the old value stays in place for readers that already found it
        Volatile.Write(ref s.Nodes[target].Value, -1);
        _garbageValues++;
        var changed = target;

        // prune routing nodes that are no longer needed, walking back towards the root (which is never pruned)
        for (var d = depth - 1; d >= 1; d--)
        {
            var n = path[d];
            if (s.Nodes[n].Value >= 0)
                break;

            var c0 = s.Nodes[n].Child0;
            var c1 = s.Nodes[n].Child1;
            var parent = path[d - 1];
            if (c0 == Sentinel && c1 == Sentinel)
            {
                Link(s, parent, slots[d], Sentinel); // unlink a childless routing node, then look at its parent
                _garbageNodes++;
                changed = n;
                continue;
            }

            if (c0 == Sentinel || c1 == Sentinel)
            {
                Link(s, parent, slots[d], c0 == Sentinel ? c1 : c0); // splice the only child into this node's place
                _garbageNodes++;
                changed = n;
            }

            break;
        }

        Count--;
        RefreshIndex(changed);
        return true;
    }

    private int NewNode(State s, in TKey bits, int length)
    {
        var index = s.NodeCount++;
        s.Nodes[index] = new Node { Bits = bits, Mask = TKey.MaskFor(length), Split = TKey.SplitFor(length), Value = -1 };
        s.Lengths[index] = (ushort)length;
        return index;
    }

    private static int NewValue(State s, TPrefix prefix, TValue value)
    {
        var index = s.ValueCount++;
        s.Entries[index] = new Entry { Prefix = prefix, Value = value };
        return index;
    }

    // for nodes that readers cannot reach yet
    private static void SetChildUnpublished(State s, int parent, bool slot, int child)
    {
        if (slot) s.Nodes[parent].Child1 = child;
        else s.Nodes[parent].Child0 = child;
    }

    // for nodes that readers can reach: one atomic write, ordered after everything written before it
    private static void Link(State s, int parent, bool slot, int child)
    {
        if (slot) Volatile.Write(ref s.Nodes[parent].Child1, child);
        else Volatile.Write(ref s.Nodes[parent].Child0, child);
    }

    // ---- index -----------------------------------------------------------------------------------------------------

    // Refreshes the index after a change. Only keys inside the changed node's prefix can see a different result, so only
    // index entries whose blocks overlap that prefix can change. The writer walks down the index along the prefix to the
    // deepest entry whose block contains the whole prefix and rebuilds just that entry, or, when the prefix is split
    // across a table's entries, just the entries it covers. Rebuilt entries get fresh sub-tables; the old ones are left
    // for readers that may still be on them and counted as garbage.
    private void RefreshIndex(int changed)
    {
        if (_rootBits == 0 || !_maintainIndex)
            return;

        var s = _w;
        var bits = s.Nodes[changed].Bits; // bits past the node's length are zero
        int length = s.Lengths[changed];

        // a short prefix spans several root entries
        if (length < _rootBits)
        {
            var firstTop = TKey.GetTopBits(bits, _rootBits);
            for (var top = firstTop; top < firstTop + (1 << (_rootBits - length)); top++)
                RecomputeEntry(inRoot: true, 0, top, TKey.FromTopBits(top, _rootBits), _rootBits);
            return;
        }

        // descend while the next table's blocks still contain the whole prefix
        var inRoot = true;
        var tableStart = 0;
        var slot = TKey.GetTopBits(bits, _rootBits);
        var entry = s.Root[slot];
        var decided = _rootBits;
        while (((uint)entry & SubTableFlag) != 0 && decided + _subBits <= length)
        {
            inRoot = false;
            tableStart = (int)((uint)entry & ~SubTableFlag);
            slot = TKey.GetBits(bits, decided, _subBits);
            entry = s.Subs[tableStart + slot];
            decided += _subBits;
        }

        if (((uint)entry & SubTableFlag) == 0)
        {
            // a plain entry covers the whole prefix: rebuild it, possibly with new sub-tables
            RecomputeEntry(inRoot, tableStart, slot, TKey.Truncate(bits, decided), decided);
            return;
        }

        // the entry points to a sub-table whose entries split the prefix: rebuild the entries the prefix covers
        var subStart = (int)((uint)entry & ~SubTableFlag);
        var block = TKey.Truncate(bits, decided);
        var first = TKey.GetBits(bits, decided, _subBits);
        for (var w = first; w < first + (1 << (decided + _subBits - length)); w++)
            RecomputeEntry(inRoot: false, subStart, w, TKey.SetBits(block, decided, _subBits, w), decided + _subBits);
    }

    private void RecomputeEntry(bool inRoot, int tableStart, int slot, TKey blockKey, int decided)
    {
        var (node, best) = Descend(_w, RootNode, -1, blockKey, decided);
        var entry = MakeEntry(node, best, blockKey, decided);
        var s = _w; // MakeEntry may have moved to a larger snapshot
        ref var target = ref inRoot ? ref s.Root[slot] : ref s.Subs[tableStart + slot];
        _garbageSubs += SubEntriesBelow(s, target);
        Volatile.Write(ref target, entry);
    }

    private int SubEntriesBelow(State s, ulong entry)
    {
        if (((uint)entry & SubTableFlag) == 0)
            return 0;
        var start = (int)((uint)entry & ~SubTableFlag);
        var total = 1 << _subBits;
        for (var w = 0; w < 1 << _subBits; w++)
            total += SubEntriesBelow(s, s.Subs[start + w]);
        return total;
    }

    // builds the whole index into _w; only called while _w is not visible to readers
    private void RebuildIndex()
    {
        _w.Root = new ulong[1 << _rootBits];
        for (var top = 0; top < _w.Root.Length; top++)
        {
            var key = TKey.FromTopBits(top, _rootBits);
            var (node, best) = Descend(_w, RootNode, -1, key, _rootBits);
            var entry = MakeEntry(node, best, key, _rootBits);
            _w.Root[top] = entry; // _w may be a larger copy by now; Reserve copies the root table
        }
    }

    private ulong MakeEntry(int node, int best, TKey key, int decided)
    {
        var s = _w;
        if (decided + _subBits > _keyWidth || !HasMoreNodesBelow(s, node, key, decided))
            return (ulong)(uint)best << 32 | (uint)node;

        var size = 1 << _subBits;
        s = Reserve(0, 0, size);
        var start = s.SubCount;
        s.SubCount += size;
        for (var w = 0; w < size; w++)
        {
            var subKey = TKey.SetBits(key, decided, _subBits, w);
            var (subNode, subBest) = Descend(_w, node, best, subKey, decided + _subBits);
            var subEntry = MakeEntry(subNode, subBest, subKey, decided + _subBits);
            _w.Subs[start + w] = subEntry; // not reachable until the parent entry is written
        }

        return (ulong)(uint)best << 32 | SubTableFlag | (uint)start;
    }

    private static (int Node, int Best) Descend(State s, int i, int best, in TKey key, int bits)
    {
        while (true)
        {
            best = SelectBest(s.Nodes[i].Value, best);
            if (s.Lengths[i] >= bits)
                return (i, best);

            var next = ChildAt(ref s.Nodes[i], TKey.IsSet(key, s.Nodes[i].Split));
            if (s.Lengths[next] > bits || !TKey.MatchesUnderMask(key, s.Nodes[next].Bits, s.Nodes[next].Mask))
                return (i, best);
            i = next;
        }
    }

    private bool HasMoreNodesBelow(State s, int node, in TKey key, int decided)
    {
        var block = TKey.Truncate(key, decided);
        var mask = TKey.MaskFor(decided);
        var count = 0;
        var stack = _scratch;
        stack.Clear();
        if (s.Nodes[node].Child0 != Sentinel) stack.Push(s.Nodes[node].Child0);
        if (s.Nodes[node].Child1 != Sentinel) stack.Push(s.Nodes[node].Child1);
        while (stack.TryPop(out var c))
        {
            if (!TKey.MatchesUnderMask(s.Nodes[c].Bits, block, mask))
                continue;
            if (++count > _threshold)
                return true;
            if (s.Nodes[c].Child0 != Sentinel) stack.Push(s.Nodes[c].Child0);
            if (s.Nodes[c].Child1 != Sentinel) stack.Push(s.Nodes[c].Child1);
        }

        return false;
    }

    // ---- bulk loading ----------------------------------------------------------------------------------------------

    /// <summary>
    ///     Stops maintaining the index until <see cref="EndBulkLoad" />. Readers fall back to walking from the root,
    ///     which is slower but always correct, while many prefixes are added quickly.
    /// </summary>
    public void BeginBulkLoad()
    {
        _maintainIndex = false;
        Volatile.Write(ref _w.Root, Array.Empty<ulong>());
    }

    /// <summary>Compacts and builds the index once, then resumes maintaining it on every change.</summary>
    public void EndBulkLoad()
    {
        Compact();
        _maintainIndex = true;
    }

    // ---- compaction ------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Builds a fresh snapshot with only reachable nodes and live values, laid out depth-first, with a rebuilt
    ///     index, and publishes it with one write.
    /// </summary>
    public void Compact()
    {
        var s = _w;
        var order = new List<int>(s.NodeCount) { Sentinel };
        var stack = new Stack<int>();
        stack.Push(RootNode);
        while (stack.TryPop(out var i))
        {
            order.Add(i);
            if (s.Nodes[i].Child1 != Sentinel) stack.Push(s.Nodes[i].Child1);
            if (s.Nodes[i].Child0 != Sentinel) stack.Push(s.Nodes[i].Child0);
        }

        var newIndex = new int[s.NodeCount];
        for (var i = 0; i < order.Count; i++)
            newIndex[order[i]] = i;

        var liveValues = 0;
        foreach (var i in order)
            if (s.Nodes[i].Value >= 0) liveValues++;

        var liveSubEntries = s.Root.Length > 0 ? Math.Max(s.SubCount - _garbageSubs, 0) : order.Count * 2;
        var next = NewState(Math.Max(order.Count, 2), Math.Max(liveValues, 1), _rootBits > 0 ? Math.Max(1 << _subBits, liveSubEntries + liveSubEntries / 8) : 0);
        var valueCount = 0;
        for (var i = 0; i < order.Count; i++)
        {
            var n = s.Nodes[order[i]];
            n.Child0 = newIndex[n.Child0];
            n.Child1 = newIndex[n.Child1];
            if (n.Value >= 0)
            {
                next.Entries[valueCount] = s.Entries[n.Value];
                n.Value = valueCount++;
            }
            next.Nodes[i] = n;
            next.Lengths[i] = s.Lengths[order[i]];
        }
        next.NodeCount = order.Count;
        next.ValueCount = valueCount;

        // build the index into the new snapshot privately, then publish it with one write
        _publishing = false;
        _w = next;
        if (_rootBits > 0)
        {
            RebuildIndex(); // also during a bulk load: EndBulkLoad compacts to build the index
            // the sub-table array grew by doubling; keep an eighth as headroom and give the rest back
            var keep = _w.SubCount + _w.SubCount / 8 + (1 << _subBits);
            if (_w.Subs.Length > keep)
                Array.Resize(ref _w.Subs, keep);
        }
        _publishing = true;
        Volatile.Write(ref _state, _w);

        _garbageNodes = 0;
        _garbageValues = 0;
        _garbageSubs = 0;
    }

    // ---- inspection ------------------------------------------------------------------------------------------------

    /// <summary>
    ///     A depth-first text listing of every reachable node: its prefix length, its bits and whether it holds a value.
    ///     Two tries with the same prefixes give the same listing, whatever order the prefixes were added or removed in.
    /// </summary>
    public string Dump(Func<TKey, int, string> format)
    {
        var s = _w;
        var text = new System.Text.StringBuilder();
        var stack = new Stack<(int Node, int Depth)>();
        stack.Push((RootNode, 0));
        while (stack.TryPop(out var item))
        {
            var n = s.Nodes[item.Node];
            text.Append(' ', item.Depth).Append(n.Value >= 0 ? "" : "@").AppendLine(format(n.Bits, s.Lengths[item.Node]));
            if (n.Child1 != Sentinel) stack.Push((n.Child1, item.Depth + 1));
            if (n.Child0 != Sentinel) stack.Push((n.Child0, item.Depth + 1));
        }
        return text.ToString();
    }

    /// <summary>Bytes held by the current snapshot's arrays.</summary>
    public long MemoryInBytes()
    {
        var s = _w;
        return (long)s.Nodes.Length * Unsafe.SizeOf<Node>() + (long)s.Lengths.Length * sizeof(ushort)
             + (long)s.Entries.Length * Unsafe.SizeOf<Entry>() + (s.Root.LongLength + s.Subs.LongLength) * sizeof(ulong);
    }
}
