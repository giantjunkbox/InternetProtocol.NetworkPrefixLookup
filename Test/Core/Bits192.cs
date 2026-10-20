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
using Bitvantage.InternetProtocol;

namespace Test.Core;

/// <summary>
///     A 192-bit key held in three 64-bit words. The library does not ship it: it existed for an IPv6 address followed by
///     up to 64 bits of context, such as a VRF, and one lookup per context serves that better. The tests keep it to cover
///     the trie's code for keys wider than 128 bits.
/// </summary>
public readonly struct Bits192 : IBitKey<Bits192>, IEquatable<Bits192>
{
    /// <summary>Bits 0 to 63, with bit 0 in the most significant bit.</summary>
    public readonly ulong W0;

    /// <summary>Bits 64 to 127.</summary>
    public readonly ulong W1;

    /// <summary>Bits 128 to 191.</summary>
    public readonly ulong W2;

    /// <summary>Creates a key from its three words.</summary>
    /// <param name="w0">Bits 0 to 63, with bit 0 in the most significant bit.</param>
    /// <param name="w1">Bits 64 to 127.</param>
    /// <param name="w2">Bits 128 to 191.</param>
    public Bits192(ulong w0, ulong w1, ulong w2)
    {
        W0 = w0;
        W1 = w1;
        W2 = w2;
    }

    /// <inheritdoc />
    public static int Width => 192;

    // the mask for the word that starts at bit index start
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong WordMask(int length, int start) =>
        length <= start ? 0 : length >= start + 64 ? ulong.MaxValue : ulong.MaxValue << (64 - (length - start));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Word(in Bits192 key, int index) => index switch { 0 => key.W0, 1 => key.W1, 2 => key.W2, _ => 0 };

    /// <inheritdoc />
    public static Bits192 Truncate(in Bits192 key, int length) =>
        new(key.W0 & WordMask(length, 0), key.W1 & WordMask(length, 64), key.W2 & WordMask(length, 128));

    /// <inheritdoc />
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

    /// <inheritdoc />
    public static Bits192 MaskFor(int length) => new(WordMask(length, 0), WordMask(length, 64), WordMask(length, 128));

    /// <inheritdoc />
    public static Bits192 SplitFor(int length) =>
        length >= 192 ? default :
        length < 64 ? new(1UL << (63 - length), 0, 0) :
        length < 128 ? new(0, 1UL << (127 - length), 0) :
        new(0, 0, 1UL << (191 - length));

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(in Bits192 key, in Bits192 split) => ((key.W0 & split.W0) | (key.W1 & split.W1) | (key.W2 & split.W2)) != 0;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesUnderMask(in Bits192 key, in Bits192 bits, in Bits192 mask) =>
        (((key.W0 & mask.W0) ^ bits.W0) | ((key.W1 & mask.W1) ^ bits.W1) | ((key.W2 & mask.W2) ^ bits.W2)) == 0;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBits(in Bits192 key, int offset, int count)
    {
        // the 64 bits starting at offset, then the first count of them
        var word = offset >> 6;
        var shift = offset & 63;
        var window = Word(key, word) << shift;
        if (shift != 0) window |= Word(key, word + 1) >> (64 - shift);
        return (int)(window >> (64 - count));
    }

    /// <inheritdoc />
    public static Bits192 SetBits(in Bits192 key, int offset, int count, int value)
    {
        ulong w0 = key.W0, w1 = key.W1, w2 = key.W2;
        for (var word = 0; word < 3; word++)
        {
            // the part of the field that falls into this word, as a mask and as bits
            var start = word << 6;
            var from = Math.Max(offset, start);
            var to = Math.Min(offset + count, start + 64);
            if (from >= to) continue;
            var width = to - from;
            var mask = (ulong.MaxValue >> (64 - width)) << (start + 64 - to);
            var part = ((ulong)(uint)value >> (offset + count - to)) << (start + 64 - to) & mask;
            switch (word)
            {
                case 0: w0 = (w0 & ~mask) | part; break;
                case 1: w1 = (w1 & ~mask) | part; break;
                default: w2 = (w2 & ~mask) | part; break;
            }
        }

        return new(w0, w1, w2);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTopBits(in Bits192 key, int count) => (int)(key.W0 >> (64 - count));

    /// <inheritdoc />
    public static Bits192 FromTopBits(int top, int count) => new((ulong)(uint)top << (64 - count), 0, 0);

    /// <inheritdoc />
    public bool Equals(Bits192 other) => W0 == other.W0 && W1 == other.W1 && W2 == other.W2;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Bits192 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(W0, W1, W2);

    /// <summary>The bits in hexadecimal.</summary>
    public override string ToString() => $"{W0:x16}{W1:x16}{W2:x16}";

    /// <summary>True when both keys hold the same bits.</summary>
    public static bool operator ==(Bits192 left, Bits192 right) => left.Equals(right);

    /// <summary>True when the keys hold different bits.</summary>
    public static bool operator !=(Bits192 left, Bits192 right) => !left.Equals(right);
}
