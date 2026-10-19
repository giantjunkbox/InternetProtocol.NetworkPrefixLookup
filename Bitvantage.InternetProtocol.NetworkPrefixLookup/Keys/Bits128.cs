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

/// <summary>A 128-bit key, such as an IPv6 address.</summary>
public readonly struct Bits128 : IBitKey<Bits128>, IEquatable<Bits128>
{
    /// <summary>The bits, with bit 0 of the key in the most significant bit.</summary>
    public readonly UInt128 Value;

    /// <summary>Creates a key from its bits, with bit 0 of the key in the most significant bit.</summary>
    /// <param name="value">The bits.</param>
    public Bits128(UInt128 value) => Value = value;

    /// <inheritdoc />
    public static int Width => 128;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 Mask(int length) => length == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - length);

    /// <inheritdoc />
    public static Bits128 Truncate(in Bits128 key, int length) => new(key.Value & Mask(length));

    /// <inheritdoc />
    public static int CommonPrefixLength(in Bits128 a, in Bits128 b, int maxLength) => Math.Min((int)UInt128.LeadingZeroCount(a.Value ^ b.Value), maxLength);

    /// <inheritdoc />
    public static Bits128 MaskFor(int length) => new(Mask(length));

    /// <inheritdoc />
    public static Bits128 SplitFor(int length) => new(length >= 128 ? UInt128.Zero : UInt128.One << (127 - length));

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(in Bits128 key, in Bits128 split) => (key.Value & split.Value) != UInt128.Zero;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesUnderMask(in Bits128 key, in Bits128 bits, in Bits128 mask) => (key.Value & mask.Value) == bits.Value;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBits(in Bits128 key, int offset, int count) => (int)(ulong)((key.Value << offset) >> (128 - count));

    /// <inheritdoc />
    public static Bits128 SetBits(in Bits128 key, int offset, int count, int value)
    {
        var mask = (UInt128.MaxValue >> (128 - count)) << (128 - offset - count);
        return new((key.Value & ~mask) | (((UInt128)(uint)value << (128 - offset - count)) & mask));
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTopBits(in Bits128 key, int count) => (int)(ulong)(key.Value >> (128 - count));

    /// <inheritdoc />
    public static Bits128 FromTopBits(int top, int count) => new((UInt128)(uint)top << (128 - count));

    /// <inheritdoc />
    public bool Equals(Bits128 other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Bits128 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>The bits in hexadecimal.</summary>
    public override string ToString() => Value.ToString("x32");

    /// <summary>True when both keys hold the same bits.</summary>
    public static bool operator ==(Bits128 left, Bits128 right) => left.Equals(right);

    /// <summary>True when the keys hold different bits.</summary>
    public static bool operator !=(Bits128 left, Bits128 right) => !left.Equals(right);
}
