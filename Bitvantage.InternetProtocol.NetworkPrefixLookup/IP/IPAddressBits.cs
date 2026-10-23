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

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     Converts addresses to and from integers whose most significant bit is the address's first bit, without allocating.
///     For writing adapters.
/// </summary>
public static class IPAddressBits
{
    /// <summary>The bits of an IPv4 address: 10.1.2.3 becomes 0x0A010203.</summary>
    /// <param name="address">An IPv4 address.</param>
    /// <exception cref="ArgumentNullException">The address is null.</exception>
    /// <exception cref="ArgumentException">The address is not IPv4.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ToIPv4Bits(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"{address} is not an IPv4 address.", nameof(address));
        return IPv4Bits(address);
    }

    /// <summary>The bits of an IPv6 address. The scope ID is ignored.</summary>
    /// <param name="address">An IPv6 address.</param>
    /// <exception cref="ArgumentNullException">The address is null.</exception>
    /// <exception cref="ArgumentException">The address is not IPv6.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt128 ToIPv6Bits(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException($"{address} is not an IPv6 address.", nameof(address));
        return IPv6Bits(address);
    }

    // the conversions for an address already known to be of the right family
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint IPv4Bits(IPAddress address)
    {
        // The obsolete Address property is about 30% faster end to end than TryWriteBytes. It holds the bytes in
        // network order, read as a machine integer.
#pragma warning disable CS0618
        var bits = (uint)address.Address;
#pragma warning restore CS0618
        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(bits) : bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 IPv6Bits(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    /// <summary>The IPv4 address with the given bits.</summary>
    /// <param name="bits">The bits, with the address's first bit in the most significant bit.</param>
    public static IPAddress FromIPv4Bits(uint bits)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, bits);
        return new IPAddress(bytes);
    }

    /// <summary>The IPv6 address with the given bits.</summary>
    /// <param name="bits">The bits, with the address's first bit in the most significant bit.</param>
    public static IPAddress FromIPv6Bits(UInt128 bits)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, bits);
        return new IPAddress(bytes);
    }

    // True for ::ffff:0:0/96, the IPv4-mapped IPv6 addresses.
    internal static bool IsIPv4Mapped(UInt128 bits) => (bits >> 32) == 0xFFFF;

    // True when an IPv6 prefix shorter than 96 bits contains all of ::ffff:0:0/96. A UInt128 shift takes its count modulo
    // 128, so length 0, which contains everything, cannot use the shift.
    internal static bool ContainsIPv4Mapped(UInt128 bits, int length) =>
        length < 96 && (length == 0 || ((bits ^ ((UInt128)0xFFFF << 32)) >> (128 - length)) == 0);

    // Sorts an address into a family: 4 with its bits in v4, or 6 with its bits in v6. With map set, an IPv4-mapped IPv6
    // address counts as IPv4.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsIPv4(IPAddress address, bool map, out uint v4, out UInt128 v6)
    {
        ArgumentNullException.ThrowIfNull(address);
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                v4 = IPv4Bits(address);
                v6 = default;
                return true;
            case AddressFamily.InterNetworkV6:
                v6 = IPv6Bits(address);
                return IsIPv4(v6, map, out v4);
            default:
                throw new ArgumentException($"Unsupported address family {address.AddressFamily}.", nameof(address));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsIPv4(ReadOnlySpan<byte> address, bool map, out uint v4, out UInt128 v6)
    {
        switch (address.Length)
        {
            case 4:
                v4 = BinaryPrimitives.ReadUInt32BigEndian(address);
                v6 = default;
                return true;
            case 16:
                v6 = BinaryPrimitives.ReadUInt128BigEndian(address);
                return IsIPv4(v6, map, out v4);
            default:
                throw new ArgumentException("An address has 4 or 16 bytes.", nameof(address));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsIPv4(UInt128 v6, bool map, out uint v4)
    {
        if (map && IsIPv4Mapped(v6))
        {
            v4 = (uint)v6;
            return true;
        }

        v4 = 0;
        return false;
    }

    // a null check that does not box a struct
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfNull<T>(T value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value is null)
            throw new ArgumentNullException(name);
    }

    internal static string Format(Bits32 bits, int length) => $"{FromIPv4Bits(bits.Value)}/{length}";

    internal static string Format(Bits128 bits, int length) => $"{FromIPv6Bits(bits.Value)}/{length}";

    internal static void CheckFamily(AddressFamily family)
    {
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException($"Unsupported address family {family}.", nameof(family));
    }
}
