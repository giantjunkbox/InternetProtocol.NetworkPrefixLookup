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

namespace Bitvantage.InternetProtocol;

/// <summary>A fixed set of 256 bits, with rank and highest-common-bit operations.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitSet256
{
    public ulong W0, W1, W2, W3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Test(int bit) => ((Unsafe.Add(ref Unsafe.AsRef(in W0), bit >> 6) >> (bit & 63)) & 1) != 0;

    public void Set(int bit) => Unsafe.Add(ref W0, bit >> 6) |= 1UL << (bit & 63);

    public void Clear(int bit) => Unsafe.Add(ref W0, bit >> 6) &= ~(1UL << (bit & 63));

    public readonly bool IsEmpty => (W0 | W1 | W2 | W3) == 0;

    public readonly int PopCount() =>
        BitOperations.PopCount(W0) + BitOperations.PopCount(W1) + BitOperations.PopCount(W2) + BitOperations.PopCount(W3);

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

    /// <summary>The lowest set bit, or 256 when the set is empty.</summary>
    public readonly int First() =>
        W0 != 0 ? BitOperations.TrailingZeroCount(W0) : W1 != 0 ? 64 + BitOperations.TrailingZeroCount(W1)
        : W2 != 0 ? 128 + BitOperations.TrailingZeroCount(W2) : W3 != 0 ? 192 + BitOperations.TrailingZeroCount(W3) : 256;

    /// <summary>The lowest set bit at or above <paramref name="from" />, or 256 when there is none.</summary>
    public readonly int NextSet(int from)
    {
        if (from >= 256) return 256;
        var w = from >> 6;
        var word = Unsafe.Add(ref Unsafe.AsRef(in W0), w) & (ulong.MaxValue << (from & 63));
        while (word == 0)
        {
            if (++w == 4) return 256;
            word = Unsafe.Add(ref Unsafe.AsRef(in W0), w);
        }

        return (w << 6) + BitOperations.TrailingZeroCount(word);
    }

    /// <summary>The bits set in either set.</summary>
    public static BitSet256 Or(in BitSet256 a, in BitSet256 b) => new() { W0 = a.W0 | b.W0, W1 = a.W1 | b.W1, W2 = a.W2 | b.W2, W3 = a.W3 | b.W3 };

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

/// <summary>
///     Knuth's ART base indexes for one 8-bit stride: the prefixes of relative length 0 to 7 inside a stride form a
///     complete binary tree numbered 1 to 255, where index i has children 2i and 2i + 1.
/// </summary>
internal static class ArtTables
{
    /// <summary>The base index of a prefix of <paramref name="relativeLength" /> bits, 0 to 7, inside a stride.</summary>
    public static int PrefixToIndex(int octet, int relativeLength) => (octet >> (8 - relativeLength)) + (1 << relativeLength);

    /// <summary>The base index of the longest prefix inside a stride that contains <paramref name="octet" />: relative length 7.</summary>
    public static int OctetToIndex(int octet) => (octet >> 1) + 128;

    /// <summary>The relative length of a base index.</summary>
    public static int IndexToLength(int index) => 31 - BitOperations.LeadingZeroCount((uint)index);

    /// <summary>Ancestors[i] holds index i and every index above it: i, i/2, i/4, ..., 1.</summary>
    public static readonly BitSet256[] Ancestors = BuildAncestors();

    /// <summary>StartOctet[i] is the first octet that base index i covers.</summary>
    public static readonly byte[] StartOctet = BuildStartOctets();

    /// <summary>The octets at which the prefixes with the given base indexes start.</summary>
    public static BitSet256 StartOctets(in BitSet256 indexes)
    {
        var octets = default(BitSet256);
        for (var w = 0; w < 4; w++)
            for (var word = Unsafe.Add(ref Unsafe.AsRef(in indexes.W0), w); word != 0; word &= word - 1)
                octets.Set(StartOctet[(w << 6) + BitOperations.TrailingZeroCount(word)]);
        return octets;
    }

    private static byte[] BuildStartOctets()
    {
        var table = new byte[256];
        for (var i = 1; i < 256; i++)
        {
            var length = IndexToLength(i);
            table[i] = (byte)((i - (1 << length)) << (8 - length));
        }

        return table;
    }

    private static BitSet256[] BuildAncestors()
    {
        var table = new BitSet256[256];
        for (var i = 1; i < 256; i++)
            for (var j = i; j > 0; j >>= 1)
                table[i].Set(j);
        return table;
    }
}
