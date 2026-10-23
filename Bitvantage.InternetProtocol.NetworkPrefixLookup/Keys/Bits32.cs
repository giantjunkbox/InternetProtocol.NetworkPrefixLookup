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

/// <summary>A 32-bit key, such as an IPv4 address.</summary>
public readonly struct Bits32 : IBitKey<Bits32>, IEquatable<Bits32>
{
    /// <summary>The bits, with bit 0 of the key in the most significant bit.</summary>
    public uint Value { get; }

    /// <summary>Creates a key from its bits, with bit 0 of the key in the most significant bit.</summary>
    /// <param name="value">The bits.</param>
    public Bits32(uint value) => Value = value;

    /// <inheritdoc />
    static int IBitKey<Bits32>.Width => 32;

    // shifting a 64-bit value gives 0 for length 0 and all ones for length 32, without a branch
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mask(int length) => (uint)((ulong)uint.MaxValue << (32 - length));

    /// <inheritdoc />
    static Bits32 IBitKey<Bits32>.Truncate(in Bits32 key, int length) => new(key.Value & Mask(length));

    /// <inheritdoc />
    static Bits32 IBitKey<Bits32>.MaskFor(int length) => new(Mask(length));

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IBitKey<Bits32>.MatchesUnderMask(in Bits32 key, in Bits32 bits, in Bits32 mask) => (key.Value & mask.Value) == bits.Value;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int IBitKey<Bits32>.GetBits(in Bits32 key, int offset, int count) => (int)((key.Value << offset) >> (32 - count));

    /// <inheritdoc />
    static Bits32 IBitKey<Bits32>.SetBits(in Bits32 key, int offset, int count, int value)
    {
        var mask = (uint.MaxValue >> (32 - count)) << (32 - offset - count);
        return new((key.Value & ~mask) | (((uint)value << (32 - offset - count)) & mask));
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int IBitKey<Bits32>.GetTopBits(in Bits32 key, int count) => (int)(key.Value >> (32 - count));

    /// <inheritdoc />
    static int IBitKey<Bits32>.Compare(in Bits32 a, in Bits32 b) => a.Value.CompareTo(b.Value);

    /// <inheritdoc />
    public bool Equals(Bits32 other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Bits32 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>The bits in hexadecimal.</summary>
    public override string ToString() => Value.ToString("x8");

    /// <summary>True when both keys hold the same bits.</summary>
    public static bool operator ==(Bits32 left, Bits32 right) => left.Equals(right);

    /// <summary>True when the keys hold different bits.</summary>
    public static bool operator !=(Bits32 left, Bits32 right) => !left.Equals(right);
}
