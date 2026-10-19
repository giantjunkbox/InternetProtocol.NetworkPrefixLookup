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

namespace Bitvantage.InternetProtocol;

/// <summary>A 64-bit key, such as a MAC address left-aligned in 64 bits.</summary>
public readonly struct Bits64 : IBitKey<Bits64>, IEquatable<Bits64>
{
    /// <summary>The bits, with bit 0 of the key in the most significant bit.</summary>
    public readonly ulong Value;

    /// <summary>Creates a key from its bits, with bit 0 of the key in the most significant bit.</summary>
    /// <param name="value">The bits.</param>
    public Bits64(ulong value) => Value = value;

    /// <inheritdoc />
    public static int Width => 64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mask(int length) => length == 0 ? 0UL : ulong.MaxValue << (64 - length);

    /// <inheritdoc />
    public static Bits64 Truncate(in Bits64 key, int length) => new(key.Value & Mask(length));

    /// <inheritdoc />
    public static int CommonPrefixLength(in Bits64 a, in Bits64 b, int maxLength) => Math.Min(BitOperations.LeadingZeroCount(a.Value ^ b.Value), maxLength);

    /// <inheritdoc />
    public static Bits64 MaskFor(int length) => new(Mask(length));

    /// <inheritdoc />
    public static Bits64 SplitFor(int length) => new(length >= 64 ? 0UL : 1UL << (63 - length));

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(in Bits64 key, in Bits64 split) => (key.Value & split.Value) != 0UL;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesUnderMask(in Bits64 key, in Bits64 bits, in Bits64 mask) => (key.Value & mask.Value) == bits.Value;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBits(in Bits64 key, int offset, int count) => (int)((key.Value << offset) >> (64 - count));

    /// <inheritdoc />
    public static Bits64 SetBits(in Bits64 key, int offset, int count, int value)
    {
        var mask = (ulong.MaxValue >> (64 - count)) << (64 - offset - count);
        return new((key.Value & ~mask) | (((ulong)(uint)value << (64 - offset - count)) & mask));
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTopBits(in Bits64 key, int count) => (int)(key.Value >> (64 - count));

    /// <inheritdoc />
    public static Bits64 FromTopBits(int top, int count) => new((ulong)(uint)top << (64 - count));

    /// <inheritdoc />
    public bool Equals(Bits64 other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Bits64 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>The bits in hexadecimal.</summary>
    public override string ToString() => Value.ToString("x16");

    /// <summary>True when both keys hold the same bits.</summary>
    public static bool operator ==(Bits64 left, Bits64 right) => left.Equals(right);

    /// <summary>True when the keys hold different bits.</summary>
    public static bool operator !=(Bits64 left, Bits64 right) => !left.Equals(right);
}
