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

using System.Collections;
using System.Net;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     A read-only set of IPv4 and IPv6 prefixes with faster lookups than
///     <see cref="IPPrefixLookup{TPrefix,TAdapter,TValue}" />, built by its <c>Freeze</c> method.
/// </summary>
/// <remarks>A frozen lookup never changes, so any number of threads may read it.</remarks>
/// <typeparam name="TPrefix">The prefix type.</typeparam>
/// <typeparam name="TAdapter">Reads the prefix type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> : IReadOnlyIPPrefixLookup<TPrefix, TValue>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>
{
    // The read-only members are the same as in IPPrefixLookup; keep the two in step.
    private readonly FrozenBitTrie<Bits32, TPrefix, TValue> _v4;
    private readonly FrozenBitTrie<Bits128, TPrefix, TValue> _v6;
    private readonly bool _map;

    /// <summary>Freezes a read-write lookup. Derived classes call this from their own constructors.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenIPPrefixLookup(IPPrefixLookup<TPrefix, TAdapter, TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6)
    {
        ArgumentNullException.ThrowIfNull(source);
        _v4 = source.FreezeIPv4(ipv4);
        _v6 = source.FreezeIPv6(ipv6);
        Options = source.Options;
        _map = Options.MapIPv4MappedAddresses;
    }

    /// <summary>The options of the lookup this was frozen from.</summary>
    public IPLookupOptions Options { get; }

    /// <summary>The memory held by both address families' tables and entries, in bytes.</summary>
    public long Bytes => _v4.Bytes + _v6.Bytes;

    // ---- lookups ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool TryGetMatch(IPAddress address, out TPrefix prefix, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out value)
            : _v6.TryGetMatch(new Bits128(v6), out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatch(ReadOnlySpan<byte> address, out TPrefix prefix, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out value)
            : _v6.TryGetMatch(new Bits128(v6), out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatchIPv4(uint address, out TPrefix prefix, out TValue value) => _v4.TryGetMatch(new Bits32(address), out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatchIPv6(UInt128 address, out TPrefix prefix, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out value)
            : _v6.TryGetMatch(new Bits128(address), out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatchValue(IPAddress address, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatchValue(new Bits32(v4), out value)
            : _v6.TryGetMatchValue(new Bits128(v6), out value);

    /// <inheritdoc />
    public bool TryGetMatchValue(ReadOnlySpan<byte> address, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatchValue(new Bits32(v4), out value)
            : _v6.TryGetMatchValue(new Bits128(v6), out value);

    /// <inheritdoc />
    public bool TryGetMatchValueIPv4(uint address, out TValue value) => _v4.TryGetMatchValue(new Bits32(address), out value);

    /// <inheritdoc />
    public bool TryGetMatchValueIPv6(UInt128 address, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4)
            ? _v4.TryGetMatchValue(new Bits32(v4), out value)
            : _v6.TryGetMatchValue(new Bits128(address), out value);

    /// <inheritdoc />
    public bool TryGetMatch(TPrefix network, out TPrefix prefix, out TValue value)
    {
        IPAddressBits.ThrowIfNull(network);
        return TAdapter.IsIPv4(network)
            ? _v4.TryGetMatch(new Bits32(TAdapter.GetIPv4Bits(network)), TAdapter.GetLength(network), out prefix, out value)
            : _v6.TryGetMatch(new Bits128(TAdapter.GetIPv6Bits(network)), TAdapter.GetLength(network), out prefix, out value);
    }

    /// <inheritdoc />
    public bool TryGetValue(TPrefix prefix, out TValue value)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? _v4.TryGetValue(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix), out value)
            : _v6.TryGetValue(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix), out value);
    }

    /// <inheritdoc />
    public bool ContainsPrefix(TPrefix prefix)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? _v4.ContainsPrefix(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix))
            : _v6.ContainsPrefix(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix));
    }

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(IPAddress address) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? _v4.GetMatches(new Bits32(v4)) : _v6.GetMatches(new Bits128(v6));

    /// <inheritdoc />
    public int GetMatches(IPAddress address, Span<PrefixEntry<TPrefix, TValue>> destination) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? _v4.GetMatches(new Bits32(v4), destination) : _v6.GetMatches(new Bits128(v6), destination);

    /// <summary>The number of stored prefixes, IPv4 and IPv6.</summary>
    public int Count => _v4.Count + _v6.Count;

    /// <summary>Returns the IPv4 prefixes and then the IPv6 prefixes, each in address order.</summary>
    public IEnumerator<PrefixEntry<TPrefix, TValue>> GetEnumerator() => _v4.Concat(_v6).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
