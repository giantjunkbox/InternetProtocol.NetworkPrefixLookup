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

namespace Bitvantage.InternetProtocol;

/// <summary>
///     A fixed-width bit string used as a trie key. Bits are left-aligned: bit 0 is the most significant bit.
/// </summary>
/// <remarks>
///     <para>
///         Every member is static, so the JIT compiles each key type into plain machine instructions. The library ships
///         <see cref="Bits32" /> for IPv4 and <see cref="Bits128" /> for IPv6; any other fixed width that is a multiple
///         of 8, up to 128 bits, can implement this interface.
///     </para>
///     <para>
///         Bit operations past the end of the key must treat the missing bits as zeros.
///     </para>
///     <para>
///         <see cref="GetBits" /> and <see cref="GetTopBits" /> must return a value from 0 to 2^<c>count</c> - 1. The
///         tries index their bit sets and tables with these values without bounds checks, so a key type that returns a
///         negative or wider value, through a sign extension for example, corrupts memory.
///     </para>
/// </remarks>
/// <typeparam name="TSelf">The implementing key type.</typeparam>
public interface IBitKey<TSelf> where TSelf : struct, IBitKey<TSelf>
{
    /// <summary>The number of bits in the key, a multiple of 8 from 8 to 128.</summary>
    static abstract int Width { get; }

    /// <summary>Returns a copy with every bit at index <paramref name="length" /> or later cleared.</summary>
    /// <param name="key">The key.</param>
    /// <param name="length">The number of leading bits to keep, from 0 to <see cref="Width" />.</param>
    static abstract TSelf Truncate(in TSelf key, int length);

    /// <summary>A key with the first <paramref name="length" /> bits set and the rest clear.</summary>
    /// <param name="length">The number of leading bits to set, from 0 to <see cref="Width" />.</param>
    static abstract TSelf MaskFor(int length);

    /// <summary>True when the bits of <paramref name="key" /> under <paramref name="mask" /> equal <paramref name="bits" />.</summary>
    /// <param name="key">The key to test.</param>
    /// <param name="bits">The expected bits, already truncated to the mask.</param>
    /// <param name="mask">The mask, from <see cref="MaskFor" />.</param>
    static abstract bool MatchesUnderMask(in TSelf key, in TSelf bits, in TSelf mask);

    /// <summary>The <paramref name="count" /> bits starting at bit <paramref name="offset" />, as an integer.</summary>
    /// <param name="key">The key.</param>
    /// <param name="offset">The index of the first bit, from 0 to <see cref="Width" /> - 1.</param>
    /// <param name="count">The number of bits, from 1 to 24.</param>
    static abstract int GetBits(in TSelf key, int offset, int count);

    /// <summary>A copy of <paramref name="key" /> with <paramref name="count" /> bits at <paramref name="offset" /> replaced.</summary>
    /// <param name="key">The key.</param>
    /// <param name="offset">The index of the first bit to replace.</param>
    /// <param name="count">The number of bits, from 1 to 24.</param>
    /// <param name="value">The new bits, right-aligned in an integer.</param>
    static abstract TSelf SetBits(in TSelf key, int offset, int count, int value);

    /// <summary>The first <paramref name="count" /> bits as an integer, from 1 to 24 bits.</summary>
    /// <param name="key">The key.</param>
    /// <param name="count">The number of bits.</param>
    static virtual int GetTopBits(in TSelf key, int count) => TSelf.GetBits(key, 0, count);
}
