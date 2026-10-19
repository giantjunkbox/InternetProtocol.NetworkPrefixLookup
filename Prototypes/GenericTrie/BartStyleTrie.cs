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
using System.Runtime.InteropServices;

namespace GenericTrie;

/// <summary>
///     The design of the current Go BART (github.com/gaissmai/bart), written for this prototype to measure it in .NET:
///     a multibit trie with 8-bit strides. Each node keeps the prefixes that end inside its stride as a complete binary
///     tree (Knuth's ART base index, indexes 1 to 255) in a 256-bit set with a popcount-compressed value array, and its
///     children in a second 256-bit set and array. The longest match inside a node is one AND with a precomputed set of
///     ancestors and a leading-zero count, without backtracking. A prefix that ends exactly on a stride boundary is
///     stored as a fringe child, and a lone longer prefix as a leaf child, so paths are compressed. Insert only.
/// </summary>
public sealed class BartStyleTrie<TKey> where TKey : struct, IBitKey<TKey>
{
    private abstract class Kid;

    private sealed class Node : Kid
    {
        public BitSet256 Prefixes;
        public int[] PrefixValues = Array.Empty<int>();
        public BitSet256 Children;
        public Kid[] ChildArray = Array.Empty<Kid>();
    }

    // a prefix that ends exactly on the stride boundary: it covers every address below its octet
    private sealed class Fringe(int value) : Kid
    {
        public int Value = value;
    }

    // a lone prefix longer than the stride, stored where its subtree would start
    private sealed class Leaf(TKey bits, int length, int value) : Kid
    {
        public readonly TKey Bits = bits;
        public readonly TKey Mask = TKey.MaskFor(length);
        public readonly int Length = length;
        public int Value = value;
    }

    [InlineArray(32)]
    private struct NodeStack
    {
        private Node _element;
    }

    private readonly Node _root = new();
    private readonly int _width;

    public BartStyleTrie(int width) => _width = width;

    public int NodeCount { get; private set; } = 1;

    /// <summary>Adds or replaces a prefix with a value, which must be zero or more.</summary>
    public void Insert(in TKey bits, int length, int value)
    {
        var key = TKey.Truncate(bits, length);
        var n = _root;
        for (var depth = 0; ; depth++)
        {
            var octet = TKey.GetBits(key, depth * 8, 8);
            var relative = length - depth * 8;
            if (relative < 8)
            {
                SetPrefix(n, PrefixToIndex(octet, relative), value);
                return;
            }

            if (!n.Children.Test(octet))
            {
                InsertChild(n, octet, relative == 8 ? new Fringe(value) : new Leaf(key, length, value));
                return;
            }

            var at = n.Children.Rank(octet) - 1;
            switch (n.ChildArray[at])
            {
                case Node child:
                    n = child;
                    continue;

                case Fringe fringe when relative == 8:
                    fringe.Value = value;
                    return;

                case Fringe fringe:
                {
                    // push the fringe down: it becomes the default route of a new node
                    var child = new Node();
                    NodeCount++;
                    SetPrefix(child, 1, fringe.Value);
                    n.ChildArray[at] = child;
                    n = child;
                    continue;
                }

                case Leaf leaf when leaf.Length == length && TKey.MatchesUnderMask(key, leaf.Bits, leaf.Mask):
                    leaf.Value = value;
                    return;

                case Leaf leaf:
                {
                    // push the leaf down into a new node, then keep inserting below
                    var child = new Node();
                    NodeCount++;
                    n.ChildArray[at] = child;
                    InsertBelow(child, depth + 1, leaf.Bits, leaf.Length, leaf.Value);
                    n = child;
                    continue;
                }
            }
        }
    }

    // inserts into the subtree rooted at node n, which sits at the given depth
    private void InsertBelow(Node n, int depth, TKey key, int length, int value)
    {
        var octet = TKey.GetBits(key, depth * 8, 8);
        var relative = length - depth * 8;
        if (relative < 8) SetPrefix(n, PrefixToIndex(octet, relative), value);
        else InsertChild(n, octet, relative == 8 ? new Fringe(value) : new Leaf(key, length, value));
    }

    /// <summary>The value of the longest stored prefix that contains the key, or -1.</summary>
    public int Lookup(in TKey key)
    {
        var stack = new NodeStack();
        var n = _root;
        var depth = 0;
        var last = _width / 8 - 1;
        for (; ; depth++)
        {
            stack[depth] = n;
            var octet = TKey.GetBits(key, depth * 8, 8);
            if (!n.Children.Test(octet))
                break;

            var kid = n.ChildArray[n.Children.Rank(octet) - 1];
            if (kid is Node child)
            {
                n = child;
                if (depth == last) break; // cannot happen with fringes, kept for safety
                continue;
            }

            if (kid is Fringe fringe)
                return fringe.Value;

            var leaf = Unsafe.As<Leaf>(kid);
            if (TKey.MatchesUnderMask(key, leaf.Bits, leaf.Mask))
                return leaf.Value;
            break;
        }

        // the deepest node first: the first match found is the longest
        for (; depth >= 0; depth--)
        {
            n = stack[depth];
            if (n.PrefixValues.Length == 0)
                continue;
            var index = (TKey.GetBits(key, depth * 8, 8) >> 1) + 128; // the octet's index at relative length 7
            if (n.Prefixes.AndTop(in AncestorTable[index], out var top))
                return n.PrefixValues[n.Prefixes.Rank(top) - 1];
        }

        return -1;
    }

    /// <summary>Bytes held by the nodes, leaves, fringes and arrays, estimated from their sizes.</summary>
    public long EstimateBytes()
    {
        long bytes = 0;
        var stack = new Stack<Node>();
        stack.Push(_root);
        while (stack.TryPop(out var n))
        {
            bytes += 16 + 32 + 8 + 32 + 8; // object header, two sets, two array references
            bytes += 24 + 4L * n.PrefixValues.Length + 24 + 8L * n.ChildArray.Length;
            foreach (var kid in n.ChildArray)
            {
                if (kid is Node child) stack.Push(child);
                else if (kid is Fringe) bytes += 24;
                else bytes += 16 + 3 * Unsafe.SizeOf<TKey>() + 8;
            }
        }
        return bytes;
    }

    private static int PrefixToIndex(int octet, int relativeLength) => (octet >> (8 - relativeLength)) + (1 << relativeLength);

    private static void SetPrefix(Node n, int index, int value)
    {
        if (n.Prefixes.Test(index))
        {
            n.PrefixValues[n.Prefixes.Rank(index) - 1] = value;
            return;
        }

        n.Prefixes.Set(index);
        n.PrefixValues = InsertAt(n.PrefixValues, n.Prefixes.Rank(index) - 1, value);
    }

    private void InsertChild(Node n, int octet, Kid kid)
    {
        n.Children.Set(octet);
        n.ChildArray = InsertAt(n.ChildArray, n.Children.Rank(octet) - 1, kid);
    }

    private static T[] InsertAt<T>(T[] array, int at, T item)
    {
        var result = new T[array.Length + 1];
        Array.Copy(array, 0, result, 0, at);
        result[at] = item;
        Array.Copy(array, at, result, at + 1, array.Length - at);
        return result;
    }

    // AncestorTable[i] holds index i and every index above it in the complete binary tree: i, i/2, i/4, ..., 1
    private static readonly BitSet256[] AncestorTable = BuildAncestorTable();

    private static BitSet256[] BuildAncestorTable()
    {
        var table = new BitSet256[256];
        for (var i = 1; i < 256; i++)
            for (var j = i; j > 0; j >>= 1)
                table[i].Set(j);
        return table;
    }
}

/// <summary>A fixed set of 256 bits, with rank and highest-common-bit operations.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BitSet256
{
    public ulong W0, W1, W2, W3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Test(int bit) => ((Unsafe.Add(ref Unsafe.AsRef(in W0), bit >> 6) >> (bit & 63)) & 1) != 0;

    public void Set(int bit) => Unsafe.Add(ref W0, bit >> 6) |= 1UL << (bit & 63);

    public void Clear(int bit) => Unsafe.Add(ref W0, bit >> 6) &= ~(1UL << (bit & 63));

    public readonly bool IsEmpty => (W0 | W1 | W2 | W3) == 0;

    public readonly int PopCount() => BitOperations.PopCount(W0) + BitOperations.PopCount(W1) + BitOperations.PopCount(W2) + BitOperations.PopCount(W3);

    /// <summary>The lowest set bit, or 256 when the set is empty.</summary>
    public readonly int First() =>
        W0 != 0 ? BitOperations.TrailingZeroCount(W0) : W1 != 0 ? 64 + BitOperations.TrailingZeroCount(W1)
        : W2 != 0 ? 128 + BitOperations.TrailingZeroCount(W2) : W3 != 0 ? 192 + BitOperations.TrailingZeroCount(W3) : 256;

    /// <summary>The number of set bits at or below <paramref name="bit" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Rank(int bit)
    {
        ref var m = ref RankMasks[bit * 4];
        return BitOperations.PopCount(W0 & m) + BitOperations.PopCount(W1 & Unsafe.Add(ref m, 1))
             + BitOperations.PopCount(W2 & Unsafe.Add(ref m, 2)) + BitOperations.PopCount(W3 & Unsafe.Add(ref m, 3));
    }

    /// <summary>The highest bit set in both sets, if any.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AndTop(in BitSet256 other, out int top)
    {
        ulong a;
        if ((a = W3 & other.W3) != 0) { top = 255 - BitOperations.LeadingZeroCount(a); return true; }
        if ((a = W2 & other.W2) != 0) { top = 191 - BitOperations.LeadingZeroCount(a); return true; }
        if ((a = W1 & other.W1) != 0) { top = 127 - BitOperations.LeadingZeroCount(a); return true; }
        if ((a = W0 & other.W0) != 0) { top = 63 - BitOperations.LeadingZeroCount(a); return true; }
        top = 0;
        return false;
    }

    // RankMasks[bit * 4 + w]: the bits of word w at or below bit
    private static readonly ulong[] RankMasks = BuildRankMasks();

    private static ulong[] BuildRankMasks()
    {
        var masks = new ulong[256 * 4];
        for (var bit = 0; bit < 256; bit++)
            for (var w = 0; w < 4; w++)
                masks[bit * 4 + w] = w < bit >> 6 ? ulong.MaxValue : w == bit >> 6 ? ulong.MaxValue >> (63 - (bit & 63)) : 0;
        return masks;
    }
}
