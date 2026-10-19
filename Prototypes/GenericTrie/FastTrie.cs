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
using System.Runtime.Intrinsics.X86;

namespace GenericTrie;

/// <summary>
///     Optimization step 1. Same algorithm and class-per-node layout as <see cref="CachedKeyTrie{TKey,TPrefix,TValue}" />,
///     but the two children are fields of the node instead of a separate array, and the walk chooses the child and
///     tracks the best match with conditional moves instead of branches.
/// </summary>
public sealed class InlineChildTrie<TKey, TPrefix, TValue> where TKey : struct, IBitKey<TKey>
{
    private readonly Node _root = new(default, 0);

    public bool TryAdd(in TKey key, int length, TPrefix prefix, TValue value)
    {
        var bits = TKey.Truncate(key, length);

        var parent = _root;
        while (true)
        {
            var next = TKey.IsSet(bits, parent.Split) ? parent.Child1 : parent.Child0;
            if (next == null || next.Length > length || !TKey.MatchesUnderMask(bits, next.Bits, next.Mask))
                break;
            parent = next;
        }

        if (parent.Length == length)
        {
            if (parent.HasValue)
                return false;

            parent.Prefix = prefix;
            parent.Value = value;
            parent.HasValue = true;
            return true;
        }

        var slot = TKey.IsSet(bits, parent.Split);
        var existing = slot ? parent.Child1 : parent.Child0;
        var node = new Node(bits, length) { Prefix = prefix, Value = value, HasValue = true };

        if (existing == null)
        {
            parent.SetChild(slot, node);
            return true;
        }

        if (length < existing.Length && TKey.MatchesUnderMask(existing.Bits, bits, node.Mask))
        {
            node.SetChild(TKey.IsSet(existing.Bits, node.Split), existing);
            parent.SetChild(slot, node);
            return true;
        }

        var common = TKey.CommonPrefixLength(bits, existing.Bits, Math.Min(length, existing.Length));
        var routing = new Node(TKey.Truncate(bits, common), common);
        routing.SetChild(TKey.IsSet(bits, routing.Split), node);
        routing.SetChild(TKey.IsSet(existing.Bits, routing.Split), existing);
        parent.SetChild(slot, routing);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value)
    {
        Node? best = null;
        var node = _root;

        while (true)
        {
            best = node.HasValue ? node : best;
            var next = TKey.IsSet(key, node.Split) ? node.Child1 : node.Child0;
            if (next == null || !TKey.MatchesUnderMask(key, next.Bits, next.Mask))
                break;
            node = next;
        }

        if (best != null)
        {
            prefix = best.Prefix;
            value = best.Value;
            return true;
        }

        prefix = default!;
        value = default!;
        return false;
    }

    private sealed class Node
    {
        public readonly TKey Bits;
        public readonly TKey Mask;
        public readonly TKey Split;
        public Node? Child0;
        public Node? Child1;
        public readonly int Length;
        public bool HasValue;
        public TPrefix Prefix = default!;
        public TValue Value = default!;

        public Node(TKey bits, int length)
        {
            Bits = bits;
            Length = length;
            Mask = TKey.MaskFor(length);
            Split = TKey.SplitFor(length);
        }

        public void SetChild(bool slot, Node child)
        {
            if (slot) Child1 = child;
            else Child0 = child;
        }
    }
}

/// <summary>Node visiting orders for <see cref="PooledTrie{TKey,TPrefix,TValue}.Relayout" />.</summary>
public enum NodeOrder
{
    BreadthFirst,
    DepthFirst
}

/// <summary>
///     Optimization steps 2 to 5. Nodes live in one array of structs and refer to their children by index, so a step of
///     the walk costs one memory load instead of two, and there are no per-node objects for the garbage collector.
///     Prefixes and values live in separate arrays that the walk never touches. On top of that:
///     <see cref="Relayout" /> reorders the node array for locality, <see cref="BuildIndex" /> adds a direct-indexed
///     table over the first bits of the key, and <see cref="GetMatches" /> interleaves many lookups.
///     Insert-only; concurrency rules for the production version are discussed in the optimization log.
/// </summary>
public sealed class PooledTrie<TKey, TPrefix, TValue> where TKey : struct, IBitKey<TKey>
{
    // the hot part of a node: everything the lookup walk reads
    [StructLayout(LayoutKind.Sequential)]
    private struct Node
    {
        public TKey Bits;
        public TKey Mask;
        public TKey Split;
        public int Child0; // 0 means none: node 0 is a sentinel whose mask is zero and bits are not, so no key matches it
        public int Child1;
        public int Value;  // index into the prefix and value arrays, or -1 for a routing node
    }

    private Node[] _nodes = new Node[1024];
    private int[] _lengths = new int[1024]; // cold: only inserts need the prefix length
    private int _nodeCount;

    // a prefix and its value side by side, so returning a match reads one place in memory
    private struct Entry
    {
        public TPrefix Prefix;
        public TValue Value;
    }

    private Entry[] _entries = new Entry[1024];
    private int _valueCount;

    // direct-indexed table: entry = (deepest node fully decided by the top bits, best value index on the way there)
    private ulong[]? _index;
    private int _indexBits;

    // candidate index: entry = (best value index so far, next node to test when the next key bit is 0, and when it is 1)
    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateEntry
    {
        public int Best;
        public int Next0;
        public int Next1;
        private int _padding; // 16 bytes, so an entry never straddles a cache line
    }

    private CandidateEntry[]? _candidateIndex;
    private TKey _candidateSplit; // selects key bit number _indexBits

    private const int Root = 1;

    public PooledTrie()
    {
        // node 0: the sentinel. (key & 0) == bits is false for every key because bits are not zero.
        NewNode(default, 0);
        _nodes[0].Mask = default;
        _nodes[0].Bits = TKey.FromTopBits(1, 1);
        _nodes[0].Value = -1;

        NewNode(default, 0); // node 1: the root, the zero-length prefix
    }

    public int NodeCount => _nodeCount;

    public int NodeSizeInBytes => Unsafe.SizeOf<Node>();

    private int NewNode(in TKey bits, int length)
    {
        if (_nodeCount == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
            Array.Resize(ref _lengths, _lengths.Length * 2);
        }

        var index = _nodeCount++;
        _nodes[index] = new Node { Bits = bits, Mask = TKey.MaskFor(length), Split = TKey.SplitFor(length), Value = -1 };
        _lengths[index] = length;
        return index;
    }

    private int NewValue(TPrefix prefix, TValue value)
    {
        if (_valueCount == _entries.Length)
            Array.Resize(ref _entries, _entries.Length * 2);

        _entries[_valueCount] = new Entry { Prefix = prefix, Value = value };
        return _valueCount++;
    }

    private void SetChild(int parent, bool slot, int child)
    {
        if (slot) _nodes[parent].Child1 = child;
        else _nodes[parent].Child0 = child;
    }

    public bool TryAdd(in TKey key, int length, TPrefix prefix, TValue value)
    {
        _index = null; // the prototype rebuilds the index explicitly after changes
        _candidateIndex = null;

        var bits = TKey.Truncate(key, length);

        // indexes are used instead of refs because NewNode can replace the array
        var parent = Root;
        while (true)
        {
            var next = TKey.IsSet(bits, _nodes[parent].Split) ? _nodes[parent].Child1 : _nodes[parent].Child0;
            if (next == 0 || _lengths[next] > length || !TKey.MatchesUnderMask(bits, _nodes[next].Bits, _nodes[next].Mask))
                break;
            parent = next;
        }

        if (_lengths[parent] == length)
        {
            if (_nodes[parent].Value >= 0)
                return false;

            _nodes[parent].Value = NewValue(prefix, value);
            return true;
        }

        var slot = TKey.IsSet(bits, _nodes[parent].Split);
        var existing = slot ? _nodes[parent].Child1 : _nodes[parent].Child0;
        var node = NewNode(bits, length);
        _nodes[node].Value = NewValue(prefix, value);

        if (existing == 0)
        {
            SetChild(parent, slot, node);
            return true;
        }

        if (length < _lengths[existing] && TKey.MatchesUnderMask(_nodes[existing].Bits, bits, _nodes[node].Mask))
        {
            SetChild(node, TKey.IsSet(_nodes[existing].Bits, _nodes[node].Split), existing);
            SetChild(parent, slot, node);
            return true;
        }

        var common = TKey.CommonPrefixLength(bits, _nodes[existing].Bits, Math.Min(length, _lengths[existing]));
        var routing = NewNode(TKey.Truncate(bits, common), common);
        SetChild(routing, TKey.IsSet(bits, _nodes[routing].Split), node);
        SetChild(routing, TKey.IsSet(_nodes[existing].Bits, _nodes[routing].Split), existing);
        SetChild(parent, slot, routing);
        return true;
    }

    /// <summary>
    ///     Rewrites the node array in the given order, so that nodes visited together sit together in memory. The trie's
    ///     shape and contents do not change.
    /// </summary>
    public void Relayout(NodeOrder order)
    {
        var newIndex = new int[_nodeCount];
        var visit = new int[_nodeCount];
        var count = 0;
        visit[count++] = 0; // the sentinel stays at index 0

        if (order == NodeOrder.BreadthFirst)
        {
            visit[count++] = Root;
            for (var head = 0; head < count; head++)
            {
                ref var n = ref _nodes[visit[head]];
                if (n.Child0 != 0) visit[count++] = n.Child0;
                if (n.Child1 != 0) visit[count++] = n.Child1;
            }
        }
        else
        {
            var stack = new Stack<int>();
            stack.Push(Root);
            while (stack.TryPop(out var i))
            {
                visit[count++] = i;
                // push child 1 first so child 0 is placed right after its parent
                if (_nodes[i].Child1 != 0) stack.Push(_nodes[i].Child1);
                if (_nodes[i].Child0 != 0) stack.Push(_nodes[i].Child0);
            }
        }

        for (var i = 0; i < count; i++)
            newIndex[visit[i]] = i;

        var nodes = new Node[_nodes.Length];
        var lengths = new int[_lengths.Length];
        for (var i = 0; i < count; i++)
        {
            var n = _nodes[visit[i]];
            n.Child0 = newIndex[n.Child0]; // the sentinel maps to itself, so "none" stays 0
            n.Child1 = newIndex[n.Child1];
            nodes[i] = n;
            lengths[i] = _lengths[visit[i]];
        }

        _nodes = nodes;
        _lengths = lengths;
        _index = null;
        _candidateIndex = null;
    }

    /// <summary>
    ///     Builds a candidate index over the first <paramref name="bits" /> bits. Unlike <see cref="BuildIndex" />, an entry
    ///     does not name a node that is already known to match; it names the next node to test, for both values of the
    ///     following key bit. A lookup therefore skips loading the deepest decided node, which <see cref="BuildIndex" />
    ///     always loads.
    /// </summary>
    public void BuildCandidateIndex(int bits)
    {
        var index = new CandidateEntry[1 << bits];
        for (var top = 0; top < index.Length; top++)
        {
            var key = TKey.FromTopBits(top, bits);
            var i = Root;
            var best = -1;
            while (true)
            {
                best = SelectBest(_nodes[i].Value, best);
                if (_lengths[i] >= bits)
                    break;

                var next = ChildAt(ref _nodes[i], TKey.IsSet(key, _nodes[i].Split));
                if (_lengths[next] > bits || !TKey.MatchesUnderMask(key, _nodes[next].Bits, _nodes[next].Mask))
                    break;
                i = next;
            }

            // node i matches and is decided by the top bits. Its child slot is decided by key bit _lengths[i]: when that
            // bit is one of the indexed bits, both candidates are the same child; when it is the next bit, they differ.
            ref var entry = ref index[top];
            entry.Best = best;
            if (_lengths[i] < bits)
            {
                var child = ChildAt(ref _nodes[i], TKey.IsSet(key, _nodes[i].Split));
                // a child no longer than the indexed bits is fully decided too; if it did match, the walk above would have
                // gone into it, so it cannot match and the sentinel stands in for it
                if (_lengths[child] <= bits)
                    child = 0;
                entry.Next0 = child;
                entry.Next1 = child;
            }
            else
            {
                entry.Next0 = _nodes[i].Child0;
                entry.Next1 = _nodes[i].Child1;
            }
        }

        _indexBits = bits;
        _candidateSplit = TKey.SplitFor(bits);
        _candidateIndex = index;
        _index = null;
    }

    /// <summary>
    ///     Builds a table with one entry for every value of the first <paramref name="bits" /> bits of a key. Each entry
    ///     holds the deepest node whose position is decided by those bits alone, and the best value found on the way to
    ///     it, so a lookup can start there instead of at the root.
    /// </summary>
    public void BuildIndex(int bits, bool skipFinal = true)
    {
        var index = new ulong[1 << bits];
        var final = 0;
        for (var top = 0; top < index.Length; top++)
        {
            var key = TKey.FromTopBits(top, bits);
            var i = Root;
            var best = -1;
            while (true)
            {
                best = _nodes[i].Value >= 0 ? _nodes[i].Value : best;
                if (_lengths[i] >= bits)
                    break; // the next child depends on a bit past the indexed ones

                var next = TKey.IsSet(key, _nodes[i].Split) ? _nodes[i].Child1 : _nodes[i].Child0;
                if (next == 0 || _lengths[next] > bits || !TKey.MatchesUnderMask(key, _nodes[next].Bits, _nodes[next].Mask))
                    break;
                i = next;
            }

            // When nothing below node i can match any key in this block, the answer is already known. Pointing the entry
            // at the sentinel ends the walk at once without loading node i or its children; the sentinel is always cached.
            bool isFinal;
            if (_lengths[i] < bits)
            {
                // the child slot is decided by the indexed bits; a child no longer than them did not match above
                var child = ChildAt(ref _nodes[i], TKey.IsSet(key, _nodes[i].Split));
                isFinal = child == 0 || _lengths[child] <= bits;
            }
            else
            {
                isFinal = _nodes[i].Child0 == 0 && _nodes[i].Child1 == 0;
            }

            if (isFinal && skipFinal)
            {
                i = 0;
                final++;
            }

            index[top] = (ulong)(uint)best << 32 | (uint)i;
        }

        _indexBits = bits;
        _index = index;
        FinalEntryFraction = final / (double)index.Length;
    }

    /// <summary>The share of index entries that point at the sentinel because nothing below them can match.</summary>
    public double FinalEntryFraction { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindValueIndex(in TKey key)
    {
        ref var nodes = ref MemoryMarshal.GetArrayDataReference(_nodes);

        var candidates = _candidateIndex;
        if (candidates != null)
        {
            ref var entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(candidates), TKey.GetTopBits(key, _indexBits));
            var found = entry.Best;
            var next = Unsafe.Add(ref entry.Next0, TKey.IsSet(key, _candidateSplit) ? 1 : 0);

            // test-then-descend: one branch per step, and a missing child is the sentinel, which never matches
            ref var c = ref Unsafe.Add(ref nodes, next);
            while (TKey.MatchesUnderMask(key, c.Bits, c.Mask))
            {
                found = SelectBest(c.Value, found);
                c = ref Unsafe.Add(ref nodes, ChildAt(ref c, TKey.IsSet(key, c.Split)));
            }

            return found;
        }

        int i, best;
        var index = _index;
        if (index != null)
        {
            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(index), TKey.GetTopBits(key, _indexBits));
            i = (int)(uint)entry;
            best = (int)(entry >> 32);
        }
        else
        {
            i = Root;
            best = -1;
        }

        // one branch per step: a missing child is the sentinel, which never matches
        while (true)
        {
            ref var n = ref Unsafe.Add(ref nodes, i);
            best = SelectBest(n.Value, best);
            var next = ChildAt(ref n, TKey.IsSet(key, n.Split));
            ref var c = ref Unsafe.Add(ref nodes, next);
            if (!TKey.MatchesUnderMask(key, c.Bits, c.Mask))
                break;
            i = next;
        }

        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value)
    {
        var best = FindValueIndex(key);
        if (best >= 0)
        {
            ref var entry = ref _entries[best];
            prefix = entry.Prefix;
            value = entry.Value;
            return true;
        }

        prefix = default!;
        value = default!;
        return false;
    }

    /// <summary>Counts the nodes a lookup visits, including the starting node. For measurements only.</summary>
    public int CountVisited(in TKey key)
    {
        var i = Root;
        if (_index != null)
            i = (int)(uint)_index[TKey.GetTopBits(key, _indexBits)];

        var visited = 1;
        while (true)
        {
            ref var n = ref _nodes[i];
            var next = ChildAt(ref n, TKey.IsSet(key, n.Split));
            if (!TKey.MatchesUnderMask(key, _nodes[next].Bits, _nodes[next].Mask))
                return visited;
            i = next;
            visited++;
        }
    }

    /// <summary>The prefix stored at a value index returned by <see cref="GetMatches" />.</summary>
    public TPrefix GetPrefix(int valueIndex) => _entries[valueIndex].Prefix;

    /// <summary>The value stored at a value index returned by <see cref="GetMatches" />.</summary>
    public TValue GetValue(int valueIndex) => _entries[valueIndex].Value;

    /// <summary>
    ///     Looks up many keys at once. <paramref name="lanes" /> lookups are kept in flight and advanced one node at a
    ///     time in turn, so the memory loads of independent lookups overlap instead of waiting for each other. When a
    ///     lookup finishes, its lane starts the next key. Writes the value index of each match, or -1.
    /// </summary>
    public void GetMatches(ReadOnlySpan<TKey> keys, Span<int> results, int lanes, bool prefetch)
    {
        ref var nodes = ref MemoryMarshal.GetArrayDataReference(_nodes);
        var index = _index;
        var indexBits = _indexBits;

        Span<int> laneNode = stackalloc int[lanes];
        Span<int> laneBest = stackalloc int[lanes];
        Span<int> laneKey = stackalloc int[lanes];

        var nextKey = 0;
        var active = 0;
        for (var lane = 0; lane < lanes; lane++)
        {
            if (nextKey < keys.Length)
            {
                laneKey[lane] = nextKey;
                StartLane(ref laneNode[lane], ref laneBest[lane], keys[nextKey++], index, indexBits, ref nodes, prefetch);
                active++;
            }
            else
            {
                laneKey[lane] = -1;
            }
        }

        while (active > 0)
        {
            for (var lane = 0; lane < lanes; lane++)
            {
                var k = laneKey[lane];
                if (k < 0)
                    continue;

                ref readonly var key = ref keys[k];
                ref var n = ref Unsafe.Add(ref nodes, laneNode[lane]);
                var best = SelectBest(n.Value, laneBest[lane]);
                var next = ChildAt(ref n, TKey.IsSet(key, n.Split));

                ref var c = ref Unsafe.Add(ref nodes, next);
                if (TKey.MatchesUnderMask(key, c.Bits, c.Mask))
                {
                    laneNode[lane] = next;
                    laneBest[lane] = best;
                    if (prefetch)
                        PrefetchChildren(ref c, ref nodes);
                    continue;
                }

                results[k] = best;
                if (nextKey < keys.Length)
                {
                    laneKey[lane] = nextKey;
                    StartLane(ref laneNode[lane], ref laneBest[lane], keys[nextKey++], index, indexBits, ref nodes, prefetch);
                }
                else
                {
                    laneKey[lane] = -1;
                    active--;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StartLane(ref int node, ref int best, in TKey key, ulong[]? index, int indexBits, ref Node nodes, bool prefetch)
    {
        if (index != null)
        {
            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(index), TKey.GetTopBits(key, indexBits));
            node = (int)(uint)entry;
            best = (int)(entry >> 32);
        }
        else
        {
            node = Root;
            best = -1;
        }

        if (prefetch)
            PrefetchChildren(ref Unsafe.Add(ref nodes, node), ref nodes);
    }

    // The JIT compiles "condition ? a : b" here as a branch. Both conditions below depend on the key's bits or on whether
    // a node holds a value, so the branch would mispredict about half the time. These helpers avoid branches entirely.

    /// <summary>Returns the child in slot 0 or 1 by indexing from Child0, which is laid out right before Child1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChildAt(ref Node node, bool slot) => Unsafe.Add(ref node.Child0, slot ? 1 : 0);

    /// <summary>Returns <paramref name="value" /> when it is a value index (zero or more), otherwise <paramref name="best" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SelectBest(int value, int best)
    {
        var routing = value >> 31; // all ones for -1 (routing node), zero for a value index
        return (best & routing) | (value & ~routing);
    }

    // the next step reads one of this node's children, so fetch both before the lane comes round again
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void PrefetchChildren(ref Node node, ref Node nodes)
    {
        if (!Sse.IsSupported)
            return;
        Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref nodes, node.Child0)));
        Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref nodes, node.Child1)));
    }
}
