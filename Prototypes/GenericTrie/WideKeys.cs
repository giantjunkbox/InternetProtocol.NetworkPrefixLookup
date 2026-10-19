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

namespace GenericTrie;

/// <summary>
///     The minimal set of operations the trie needs from a key, as an alternative to <see cref="IBinaryInteger{TSelf}" />.
///     Bits are left-aligned: index 0 is the most significant bit. Any struct can implement this, so a key can be any
///     width, including widths chosen at run time.
/// </summary>
public interface IBitKey<TSelf> where TSelf : struct, IBitKey<TSelf>
{
    /// <summary>Returns a copy with every bit at index <paramref name="length" /> or later cleared.</summary>
    static abstract TSelf Truncate(in TSelf value, int length);

    /// <summary>Returns the bit at <paramref name="index" />. Must tolerate an index equal to the key width.</summary>
    static abstract bool GetBit(in TSelf value, int index);

    /// <summary>True when the first <paramref name="length" /> bits of <paramref name="key" /> equal those of <paramref name="prefix" />.</summary>
    static abstract bool StartsWith(in TSelf key, in TSelf prefix, int length);

    /// <summary>Number of leading bits the two values share, capped at <paramref name="maxLength" />.</summary>
    static abstract int CommonPrefixLength(in TSelf a, in TSelf b, int maxLength);
}

/// <summary>32-bit key.</summary>
public readonly struct Bits32 : IBitKey<Bits32>
{
    public readonly uint Value;
    public Bits32(uint value) => Value = value;

    // (ulong)uint.MaxValue << (32 - length) yields 0 for length 0 and all ones for length 32, without a branch
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mask(int length) => (uint)((ulong)uint.MaxValue << (32 - length));

    public static Bits32 Truncate(in Bits32 value, int length) => new(value.Value & Mask(length));

    // index 32 shifts by 0 (shift counts are masked) and returns bit 0; harmless because full-length nodes have no children
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetBit(in Bits32 value, int index) => (value.Value << index & 0x8000_0000u) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool StartsWith(in Bits32 key, in Bits32 prefix, int length) => ((key.Value ^ prefix.Value) & Mask(length)) == 0;

    public static int CommonPrefixLength(in Bits32 a, in Bits32 b, int maxLength) => Math.Min(BitOperations.LeadingZeroCount(a.Value ^ b.Value), maxLength);
}

/// <summary>128-bit key.</summary>
public readonly struct Bits128 : IBitKey<Bits128>
{
    public readonly UInt128 Value;
    public Bits128(UInt128 value) => Value = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 Mask(int length) => length == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - length);

    public static Bits128 Truncate(in Bits128 value, int length) => new(value.Value & Mask(length));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetBit(in Bits128 value, int index) => (value.Value << index >> 127) != UInt128.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool StartsWith(in Bits128 key, in Bits128 prefix, int length) => ((key.Value ^ prefix.Value) & Mask(length)) == UInt128.Zero;

    public static int CommonPrefixLength(in Bits128 a, in Bits128 b, int maxLength) => Math.Min((int)UInt128.LeadingZeroCount(a.Value ^ b.Value), maxLength);
}

[InlineArray(4)]
public struct Words4
{
    private ulong _element0;
}

/// <summary>256-bit key stored inline as four 64-bit words. Comparisons stop at the last word the length covers.</summary>
public struct Bits256 : IBitKey<Bits256>
{
    public Words4 Words;

    public static Bits256 Truncate(in Bits256 value, int length)
    {
        var result = value;
        for (var i = 0; i < 4; i++)
        {
            var start = i * 64;
            if (length <= start) result.Words[i] = 0;
            else if (length < start + 64) result.Words[i] &= ulong.MaxValue << (64 - (length - start));
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetBit(in Bits256 value, int index) => index < 256 && (value.Words[index >> 6] << (index & 63) >> 63) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool StartsWith(in Bits256 key, in Bits256 prefix, int length)
    {
        var whole = length >> 6;
        for (var i = 0; i < whole; i++)
            if (key.Words[i] != prefix.Words[i])
                return false;

        var rest = length & 63;
        return rest == 0 || (key.Words[whole] ^ prefix.Words[whole]) >> (64 - rest) == 0;
    }

    public static int CommonPrefixLength(in Bits256 a, in Bits256 b, int maxLength)
    {
        for (var i = 0; i < 4; i++)
        {
            var diff = a.Words[i] ^ b.Words[i];
            if (diff != 0)
                return Math.Min(i * 64 + BitOperations.LeadingZeroCount(diff), maxLength);
        }
        return Math.Min(256, maxLength);
    }
}

/// <summary>
///     Variable-width key: any number of 64-bit words, chosen at run time and held in an array. This is the "truly
///     arbitrary" option. Every node then points to a separate array object.
/// </summary>
public readonly struct BitsVar : IBitKey<BitsVar>
{
    public readonly ulong[] Words;
    public BitsVar(ulong[] words) => Words = words;

    public static BitsVar Truncate(in BitsVar value, int length)
    {
        var words = new ulong[value.Words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            var start = i * 64;
            if (length <= start) words[i] = 0;
            else if (length < start + 64) words[i] = value.Words[i] & ulong.MaxValue << (64 - (length - start));
            else words[i] = value.Words[i];
        }
        return new BitsVar(words);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetBit(in BitsVar value, int index) => (uint)(index >> 6) < (uint)value.Words.Length && (value.Words[index >> 6] << (index & 63) >> 63) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool StartsWith(in BitsVar key, in BitsVar prefix, int length)
    {
        var k = key.Words;
        var p = prefix.Words;
        var whole = length >> 6;
        for (var i = 0; i < whole; i++)
            if (k[i] != p[i])
                return false;

        var rest = length & 63;
        return rest == 0 || (k[whole] ^ p[whole]) >> (64 - rest) == 0;
    }

    public static int CommonPrefixLength(in BitsVar a, in BitsVar b, int maxLength)
    {
        for (var i = 0; i < a.Words.Length; i++)
        {
            var diff = a.Words[i] ^ b.Words[i];
            if (diff != 0)
                return Math.Min(i * 64 + BitOperations.LeadingZeroCount(diff), maxLength);
        }
        return Math.Min(a.Words.Length * 64, maxLength);
    }
}

/// <summary>
///     The same trie as <see cref="BitTrie{TBits,TPrefix,TValue}" />, constrained on <see cref="IBitKey{TSelf}" /> instead
///     of <see cref="IBinaryInteger{TSelf}" />. Nodes store only the bits and the length; masks are computed by the key
///     type instead of being cached, because caching three copies of a wide key would triple the node size.
/// </summary>
public sealed class KeyTrie<TKey, TPrefix, TValue> where TKey : struct, IBitKey<TKey>
{
    private readonly Node _root = new(default, 0);

    public long Count { get; private set; }

    public bool TryAdd(in TKey key, int length, TPrefix prefix, TValue value)
    {
        var bits = TKey.Truncate(key, length);

        var parent = _root;
        while (true)
        {
            var next = parent.Children[TKey.GetBit(bits, parent.Length) ? 1 : 0];
            if (next == null || next.Length > length || !TKey.StartsWith(bits, next.Bits, next.Length))
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
            Count++;
            return true;
        }

        var slot = TKey.GetBit(bits, parent.Length) ? 1 : 0;
        var existing = parent.Children[slot];
        var node = new Node(bits, length) { Prefix = prefix, Value = value, HasValue = true };

        if (existing == null)
        {
            parent.Children[slot] = node;
            Count++;
            return true;
        }

        if (length < existing.Length && TKey.StartsWith(existing.Bits, bits, length))
        {
            node.Children[TKey.GetBit(existing.Bits, length) ? 1 : 0] = existing;
            parent.Children[slot] = node;
            Count++;
            return true;
        }

        var common = TKey.CommonPrefixLength(bits, existing.Bits, Math.Min(length, existing.Length));
        var routing = new Node(TKey.Truncate(bits, common), common);
        routing.Children[TKey.GetBit(bits, common) ? 1 : 0] = node;
        routing.Children[TKey.GetBit(existing.Bits, common) ? 1 : 0] = existing;
        parent.Children[slot] = routing;
        Count++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value)
    {
        Node? best = null;
        Node? node = _root;

        do
        {
            if (node.HasValue)
                best = node;

            node = node.Children[TKey.GetBit(key, node.Length) ? 1 : 0];
        } while (node != null && TKey.StartsWith(key, node.Bits, node.Length));

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

    public (int Value, int Routing) CountNodes()
    {
        int value = 0, routing = 0;
        var stack = new Stack<Node>();
        stack.Push(_root);
        while (stack.TryPop(out var n))
        {
            if (n.HasValue) value++; else routing++;
            foreach (var c in n.Children)
                if (c != null) stack.Push(c);
        }
        return (value, routing);
    }

    private sealed class Node
    {
        public readonly TKey Bits;
        public readonly int Length;
        public readonly Node?[] Children = new Node?[2];
        public bool HasValue;
        public TPrefix Prefix = default!;
        public TValue Value = default!;

        public Node(TKey bits, int length)
        {
            Bits = bits;
            Length = length;
        }
    }
}
