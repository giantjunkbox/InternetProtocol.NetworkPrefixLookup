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
using System.Runtime.Intrinsics;

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

    // Operations that let a node cache its mask and split bit, as BitTrie does. Used by CachedKeyTrie.

    /// <summary>A value with the first <paramref name="length" /> bits set.</summary>
    static abstract TSelf MaskFor(int length);

    /// <summary>A value with only bit <paramref name="length" /> set, or zero when the length is the full width.</summary>
    static abstract TSelf SplitFor(int length);

    /// <summary>True when <c>key &amp; split</c> is non-zero.</summary>
    static abstract bool IsSet(in TSelf key, in TSelf split);

    /// <summary>True when <c>(key &amp; mask) == bits</c>.</summary>
    static abstract bool MatchesUnderMask(in TSelf key, in TSelf bits, in TSelf mask);

    /// <summary>The first <paramref name="count" /> bits as an integer, for indexing a table. 1 to 24 bits.</summary>
    static abstract int GetTopBits(in TSelf key, int count);

    /// <summary>A key whose first <paramref name="count" /> bits are <paramref name="top" /> and whose other bits are zero.</summary>
    static abstract TSelf FromTopBits(int top, int count);
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

    public static Bits32 MaskFor(int length) => new(Mask(length));
    public static Bits32 SplitFor(int length) => new(length >= 32 ? 0 : 0x8000_0000u >> length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(in Bits32 key, in Bits32 split) => (key.Value & split.Value) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesUnderMask(in Bits32 key, in Bits32 bits, in Bits32 mask) => (key.Value & mask.Value) == bits.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTopBits(in Bits32 key, int count) => (int)(key.Value >> (32 - count));

    public static Bits32 FromTopBits(int top, int count) => new((uint)top << (32 - count));
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

    public static Bits128 MaskFor(int length) => new(Mask(length));
    public static Bits128 SplitFor(int length) => new(length >= 128 ? UInt128.Zero : UInt128.One << (127 - length));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(in Bits128 key, in Bits128 split) => (key.Value & split.Value) != UInt128.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesUnderMask(in Bits128 key, in Bits128 bits, in Bits128 mask) => (key.Value & mask.Value) == bits.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTopBits(in Bits128 key, int count) => (int)(ulong)(key.Value >> (128 - count));

    public static Bits128 FromTopBits(int top, int count) => new((UInt128)(uint)top << (128 - count));
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

    public static Bits256 MaskFor(int length) => Truncate(new Bits256 { Words = AllOnes() }, length);

    public static Bits256 SplitFor(int length)
    {
        var result = new Bits256();
        if (length < 256)
            result.Words[length >> 6] = 1UL << (63 - (length & 63));
        return result;
    }

    // the four words are compared as one 256-bit vector: one AND and one test or compare instruction each
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(in Bits256 key, in Bits256 split) => (AsVector(key) & AsVector(split)) != Vector256<ulong>.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesUnderMask(in Bits256 key, in Bits256 bits, in Bits256 mask) => (AsVector(key) & AsVector(mask)) == AsVector(bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTopBits(in Bits256 key, int count) => (int)(key.Words[0] >> (64 - count));

    public static Bits256 FromTopBits(int top, int count) { var k = new Bits256(); k.Words[0] = (ulong)(uint)top << (64 - count); return k; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> AsVector(in Bits256 value) => Unsafe.As<Words4, Vector256<ulong>>(ref Unsafe.AsRef(in value.Words));

    private static Words4 AllOnes() { var w = new Words4(); for (var i = 0; i < 4; i++) w[i] = ulong.MaxValue; return w; }
}

/// <summary>
///     192-bit key stored inline as three 64-bit words, for example an IPv6 address plus up to 64 bits of context.
///     Three words do not fill a 256-bit vector, so comparisons use branch-free scalar operations on all three words.
///     Each cached copy is 24 bytes instead of the 32 bytes of <see cref="Bits256" />.
/// </summary>
public readonly struct Bits192 : IBitKey<Bits192>
{
    public readonly ulong W0, W1, W2;

    public Bits192(ulong w0, ulong w1, ulong w2)
    {
        W0 = w0;
        W1 = w1;
        W2 = w2;
    }

    // mask for one 64-bit word that starts at bit index start
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong WordMask(int length, int start) =>
        length <= start ? 0 : length >= start + 64 ? ulong.MaxValue : ulong.MaxValue << (64 - (length - start));

    public static Bits192 Truncate(in Bits192 value, int length) =>
        new(value.W0 & WordMask(length, 0), value.W1 & WordMask(length, 64), value.W2 & WordMask(length, 128));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetBit(in Bits192 value, int index)
    {
        var word = index < 64 ? value.W0 : index < 128 ? value.W1 : index < 192 ? value.W2 : 0;
        return (word << (index & 63) >> 63) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool StartsWith(in Bits192 key, in Bits192 prefix, int length) =>
        (((key.W0 ^ prefix.W0) & WordMask(length, 0)) | ((key.W1 ^ prefix.W1) & WordMask(length, 64)) | ((key.W2 ^ prefix.W2) & WordMask(length, 128))) == 0;

    public static int CommonPrefixLength(in Bits192 a, in Bits192 b, int maxLength)
    {
        var d0 = a.W0 ^ b.W0;
        if (d0 != 0) return Math.Min(BitOperations.LeadingZeroCount(d0), maxLength);
        var d1 = a.W1 ^ b.W1;
        if (d1 != 0) return Math.Min(64 + BitOperations.LeadingZeroCount(d1), maxLength);
        var d2 = a.W2 ^ b.W2;
        if (d2 != 0) return Math.Min(128 + BitOperations.LeadingZeroCount(d2), maxLength);
        return Math.Min(192, maxLength);
    }

    public static Bits192 MaskFor(int length) => new(WordMask(length, 0), WordMask(length, 64), WordMask(length, 128));

    public static Bits192 SplitFor(int length) =>
        length >= 192 ? default :
        length < 64 ? new(1UL << (63 - length), 0, 0) :
        length < 128 ? new(0, 1UL << (127 - length), 0) :
        new(0, 0, 1UL << (191 - length));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(in Bits192 key, in Bits192 split) =>
        ((key.W0 & split.W0) | (key.W1 & split.W1) | (key.W2 & split.W2)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesUnderMask(in Bits192 key, in Bits192 bits, in Bits192 mask) =>
        (((key.W0 & mask.W0) ^ bits.W0) | ((key.W1 & mask.W1) ^ bits.W1) | ((key.W2 & mask.W2) ^ bits.W2)) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTopBits(in Bits192 key, int count) => (int)(key.W0 >> (64 - count));

    public static Bits192 FromTopBits(int top, int count) => new((ulong)(uint)top << (64 - count), 0, 0);
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

    // the variable-width type cannot know the width here, so the study data's 256 bits are assumed
    public static BitsVar MaskFor(int length) => Truncate(new BitsVar(new[] { ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue }), length);

    public static BitsVar SplitFor(int length)
    {
        var words = new ulong[4];
        if (length < 256)
            words[length >> 6] = 1UL << (63 - (length & 63));
        return new BitsVar(words);
    }

    public static bool IsSet(in BitsVar key, in BitsVar split)
    {
        for (var i = 0; i < key.Words.Length; i++)
            if ((key.Words[i] & split.Words[i]) != 0)
                return true;
        return false;
    }

    public static bool MatchesUnderMask(in BitsVar key, in BitsVar bits, in BitsVar mask)
    {
        for (var i = 0; i < key.Words.Length; i++)
            if ((key.Words[i] & mask.Words[i]) != bits.Words[i])
                return false;
        return true;
    }

    public static int GetTopBits(in BitsVar key, int count) => (int)(key.Words[0] >> (64 - count));

    public static BitsVar FromTopBits(int top, int count) => new(new[] { (ulong)(uint)top << (64 - count), 0UL, 0UL, 0UL });
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

/// <summary>
///     <see cref="KeyTrie{TKey,TPrefix,TValue}" /> with the mask and split bit cached in every node, exactly as
///     <see cref="BitTrie{TBits,TPrefix,TValue}" /> does, but through <see cref="IBitKey{TSelf}" /> so the key can be
///     any width. Only the lookup path differs from KeyTrie; insert reuses the same steps.
/// </summary>
public sealed class CachedKeyTrie<TKey, TPrefix, TValue> where TKey : struct, IBitKey<TKey>
{
    private readonly Node _root = new(default, 0);

    public bool TryAdd(in TKey key, int length, TPrefix prefix, TValue value)
    {
        var bits = TKey.Truncate(key, length);

        var parent = _root;
        while (true)
        {
            var next = parent.Children[TKey.IsSet(bits, parent.Split) ? 1 : 0];
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

        var slot = TKey.IsSet(bits, parent.Split) ? 1 : 0;
        var existing = parent.Children[slot];
        var node = new Node(bits, length) { Prefix = prefix, Value = value, HasValue = true };

        if (existing == null)
        {
            parent.Children[slot] = node;
            return true;
        }

        if (length < existing.Length && TKey.MatchesUnderMask(existing.Bits, bits, node.Mask))
        {
            node.Children[TKey.IsSet(existing.Bits, node.Split) ? 1 : 0] = existing;
            parent.Children[slot] = node;
            return true;
        }

        var common = TKey.CommonPrefixLength(bits, existing.Bits, Math.Min(length, existing.Length));
        var routing = new Node(TKey.Truncate(bits, common), common);
        routing.Children[TKey.IsSet(bits, routing.Split) ? 1 : 0] = node;
        routing.Children[TKey.IsSet(existing.Bits, routing.Split) ? 1 : 0] = existing;
        parent.Children[slot] = routing;
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

            node = node.Children[TKey.IsSet(key, node.Split) ? 1 : 0];
        } while (node != null && TKey.MatchesUnderMask(key, node.Bits, node.Mask));

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
        public readonly int Length;
        public readonly Node?[] Children = new Node?[2];
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
    }
}
