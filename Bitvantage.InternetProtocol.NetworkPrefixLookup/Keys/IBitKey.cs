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
///     The key types of the tries: <see cref="Bits32" /> and <see cref="Bits128" />. A key is a fixed-width bit string,
///     left-aligned, with bit 0 the most significant bit.
/// </summary>
/// <remarks>
///     The members are internal, so no other assembly can implement this interface: the tries index their bit sets and
///     tables with <see cref="GetBits" /> without bounds checks, and a key type that returned a negative or wider value
///     would corrupt memory. Only the library's two key types, and the test project's, implement it.
/// </remarks>
/// <typeparam name="TSelf">The implementing key type.</typeparam>
public interface IBitKey<TSelf> where TSelf : struct, IBitKey<TSelf>
{
    // Every member is static, so the JIT compiles each key type into plain machine instructions. Bit operations past the
    // end of the key treat the missing bits as zeros. GetBits and GetTopBits must return a value from 0 to 2^count - 1.

    /// <summary>The number of bits in the key, a multiple of 8 from 8 to 128.</summary>
    internal static abstract int Width { get; }

    /// <summary>Returns a copy with every bit at index <paramref name="length" /> or later cleared.</summary>
    /// <param name="key">The key.</param>
    /// <param name="length">The number of leading bits to keep, from 0 to <see cref="Width" />.</param>
    internal static abstract TSelf Truncate(in TSelf key, int length);

    /// <summary>A key with the first <paramref name="length" /> bits set and the rest clear.</summary>
    /// <param name="length">The number of leading bits to set, from 0 to <see cref="Width" />.</param>
    internal static abstract TSelf MaskFor(int length);

    /// <summary>True when the bits of <paramref name="key" /> under <paramref name="mask" /> equal <paramref name="bits" />.</summary>
    /// <param name="key">The key to test.</param>
    /// <param name="bits">The expected bits, already truncated to the mask.</param>
    /// <param name="mask">The mask, from <see cref="MaskFor" />.</param>
    internal static abstract bool MatchesUnderMask(in TSelf key, in TSelf bits, in TSelf mask);

    /// <summary>The <paramref name="count" /> bits starting at bit <paramref name="offset" />, as an integer.</summary>
    /// <param name="key">The key.</param>
    /// <param name="offset">The index of the first bit, from 0 to <see cref="Width" /> - 1.</param>
    /// <param name="count">The number of bits, from 1 to 24.</param>
    internal static abstract int GetBits(in TSelf key, int offset, int count);

    /// <summary>A copy of <paramref name="key" /> with <paramref name="count" /> bits at <paramref name="offset" /> replaced.</summary>
    /// <param name="key">The key.</param>
    /// <param name="offset">The index of the first bit to replace.</param>
    /// <param name="count">The number of bits, from 1 to 24.</param>
    /// <param name="value">The new bits, right-aligned in an integer.</param>
    internal static abstract TSelf SetBits(in TSelf key, int offset, int count, int value);

    /// <summary>The first <paramref name="count" /> bits as an integer, from 1 to 24 bits.</summary>
    /// <param name="key">The key.</param>
    /// <param name="count">The number of bits.</param>
    internal static virtual int GetTopBits(in TSelf key, int count) => TSelf.GetBits(key, 0, count);

    /// <summary>
    ///     Compares two keys in address order: as unsigned integers with bit 0 the most significant. Negative when
    ///     <paramref name="a" /> comes first, zero when the keys are equal, positive when <paramref name="b" /> comes first.
    /// </summary>
    /// <param name="a">The first key.</param>
    /// <param name="b">The second key.</param>
    internal static virtual int Compare(in TSelf a, in TSelf b)
    {
        for (var offset = 0; offset < TSelf.Width; offset += 8)
        {
            var difference = TSelf.GetBits(a, offset, 8) - TSelf.GetBits(b, offset, 8);
            if (difference != 0) return difference;
        }

        return 0;
    }
}
