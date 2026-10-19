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
using Bitvantage.InternetProtocol;

namespace GenericTrie;

/// <summary>
///     What the IP layer needs from a network type. Implemented by small structs so the JIT specializes and inlines the
///     static calls; no interface dispatch, delegates or boxing on any path.
/// </summary>
public interface IIPPrefixAdapter<TPrefix>
{
    static abstract bool IsIPv4(TPrefix prefix);
    static abstract uint GetIPv4Bits(TPrefix prefix);    // left-aligned
    static abstract UInt128 GetIPv6Bits(TPrefix prefix); // left-aligned
    static abstract int GetLength(TPrefix prefix);
}

/// <summary>Dual-stack IP layer over two generic cores: 32-bit keys for IPv4 and 128-bit keys for IPv6.</summary>
public sealed class IPPrefixLookup<TPrefix, TAdapter, TValue> where TAdapter : struct, IIPPrefixAdapter<TPrefix>
{
    private readonly BitTrie<uint, TPrefix, TValue> _v4 = new();
    private readonly BitTrie<UInt128, TPrefix, TValue> _v6 = new();

    public BitTrie<uint, TPrefix, TValue> V4 => _v4;

    public bool TryAdd(TPrefix prefix, TValue value) =>
        TAdapter.IsIPv4(prefix)
            ? _v4.TryAdd(TAdapter.GetIPv4Bits(prefix), TAdapter.GetLength(prefix), prefix, value)
            : _v6.TryAdd(TAdapter.GetIPv6Bits(prefix), TAdapter.GetLength(prefix), prefix, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(IPAddress address, out TPrefix prefix, out TValue value) =>
        address.AddressFamily == AddressFamily.InterNetwork
            ? _v4.TryGetMatch(IPBits.V4(address), out prefix, out value)
            : _v6.TryGetMatch(IPBits.V6(address), out prefix, out value);
}

/// <summary>Allocation-free conversion of IP addresses to left-aligned integers.</summary>
public static class IPBits
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint V4(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt128 V6(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }
}

/// <summary>Adapter for Bitvantage NetworkPrefix, whose NetworkBits are right-aligned.</summary>
public struct NetworkPrefixAdapter : IIPPrefixAdapter<NetworkPrefix>
{
    public static bool IsIPv4(NetworkPrefix prefix) => prefix.AddressLength == 32;
    public static uint GetIPv4Bits(NetworkPrefix prefix) => (uint)prefix.NetworkBits;
    public static UInt128 GetIPv6Bits(NetworkPrefix prefix) => prefix.NetworkBits;
    public static int GetLength(NetworkPrefix prefix) => prefix.Length;
}

/// <summary>Adapter for the built-in System.Net.IPNetwork struct.</summary>
public struct IPNetworkAdapter : IIPPrefixAdapter<IPNetwork>
{
    public static bool IsIPv4(IPNetwork prefix) => prefix.BaseAddress.AddressFamily == AddressFamily.InterNetwork;
    public static uint GetIPv4Bits(IPNetwork prefix) => IPBits.V4(prefix.BaseAddress);
    public static UInt128 GetIPv6Bits(IPNetwork prefix) => IPBits.V6(prefix.BaseAddress);
    public static int GetLength(IPNetwork prefix) => prefix.PrefixLength;
}

/// <summary>
///     Control variant: the generic core with 128-bit keys for IPv4 and the same allocating address conversion as the
///     current library. Differences from the current library therefore come only from the abstraction.
/// </summary>
public sealed class UInt128Control<TValue>
{
    private readonly BitTrie<UInt128, NetworkPrefix, TValue> _v4 = new();

    public BitTrie<UInt128, NetworkPrefix, TValue> V4 => _v4;

    public bool TryAdd(NetworkPrefix prefix, TValue value) =>
        _v4.TryAdd(prefix.NetworkBits << (128 - prefix.AddressLength), prefix.Length, prefix, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(IPAddress address, out NetworkPrefix prefix, out TValue value) =>
        _v4.TryGetMatch(ToUInt128(address) << 96, out prefix, out value);

    // copied from NetworkPrefixLookupBase.ToUInt128 (IPv4 branch), including its allocations
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 ToUInt128(IPAddress address)
    {
        var addressBytes = address.GetAddressBytes();
        return new UInt128(0, BitConverter.ToUInt64(new[] { addressBytes[3], addressBytes[2], addressBytes[1], addressBytes[0], (byte)0, (byte)0, (byte)0, (byte)0 }));
    }
}
