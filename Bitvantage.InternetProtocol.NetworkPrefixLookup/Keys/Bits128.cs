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

namespace Bitvantage.InternetProtocol.NetworkPrefixLookup;

/// <summary>A 128-bit key, such as an IPv6 address.</summary>
public readonly struct Bits128 : IBitKey<Bits128>, IEquatable<Bits128>
{
    /// <summary>The bits, with bit 0 of the key in the most significant bit.</summary>
    public UInt128 Value { get; }

    /// <summary>Creates a key from its bits, with bit 0 of the key in the most significant bit.</summary>
    /// <param name="value">The bits.</param>
    public Bits128(UInt128 value) => Value = value;

    /// <inheritdoc />
    static int IBitKey<Bits128>.Width => 128;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 Mask(int length) => length == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - length);

    /// <inheritdoc />
    static Bits128 IBitKey<Bits128>.Truncate(in Bits128 key, int length) => new(key.Value & Mask(length));

    /// <inheritdoc />
    static Bits128 IBitKey<Bits128>.MaskFor(int length) => new(Mask(length));

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IBitKey<Bits128>.MatchesUnderMask(in Bits128 key, in Bits128 bits, in Bits128 mask) => (key.Value & mask.Value) == bits.Value;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int IBitKey<Bits128>.GetBits(in Bits128 key, int offset, int count) => (int)(ulong)((key.Value << offset) >> (128 - count));

    /// <inheritdoc />
    static Bits128 IBitKey<Bits128>.SetBits(in Bits128 key, int offset, int count, int value)
    {
        var mask = (UInt128.MaxValue >> (128 - count)) << (128 - offset - count);
        return new((key.Value & ~mask) | (((UInt128)(uint)value << (128 - offset - count)) & mask));
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int IBitKey<Bits128>.GetTopBits(in Bits128 key, int count) => (int)(ulong)(key.Value >> (128 - count));

    /// <inheritdoc />
    static int IBitKey<Bits128>.Compare(in Bits128 a, in Bits128 b) => a.Value.CompareTo(b.Value);

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
