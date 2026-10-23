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

using System.Net;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;

namespace Bitvantage.InternetProtocol.NetworkPrefixLookup;

/// <summary>Reads <see cref="IPNetwork" /> for the IP lookups.</summary>
public readonly struct IPNetworkAdapter : IIPPrefixAdapter<IPNetwork>
{
    /// <inheritdoc />
    public static bool IsIPv4(IPNetwork prefix) => prefix.BaseAddress.AddressFamily == AddressFamily.InterNetwork;

    /// <inheritdoc />
    public static uint GetIPv4Bits(IPNetwork prefix) => IPAddressBits.ToIPv4Bits(prefix.BaseAddress);

    /// <inheritdoc />
    public static UInt128 GetIPv6Bits(IPNetwork prefix) => IPAddressBits.ToIPv6Bits(prefix.BaseAddress);

    /// <inheritdoc />
    public static int GetLength(IPNetwork prefix) => prefix.PrefixLength;
}

/// <summary>
///     A read-write set of <see cref="IPNetwork" /> prefixes, each with a value, with fast longest-prefix-match lookups.
/// </summary>
/// <remarks>See <see cref="IPPrefixLookup{TPrefix,TAdapter,TValue}" /> for the details.</remarks>
/// <typeparam name="TValue">The value type; <see cref="NoValue" /> for a set of networks without values.</typeparam>
public class IPNetworkLookup<TValue> : IPPrefixLookup<IPNetwork, IPNetworkAdapter, TValue>
{
    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    public IPNetworkLookup(IPLookupOptions? options = null) : base(options)
    {
    }

    /// <summary>Creates a lookup holding the given networks, built in one pass.</summary>
    /// <param name="items">The networks and their values.</param>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    /// <exception cref="ArgumentException">A network appears more than once.</exception>
    public IPNetworkLookup(IEnumerable<KeyValuePair<IPNetwork, TValue>> items, IPLookupOptions? options = null) : base(items, options)
    {
    }

    /// <summary>Creates a lookup holding the given networks, built in one pass: another lookup's contents, or the result of a query.</summary>
    /// <param name="items">The networks and their values.</param>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    /// <exception cref="ArgumentException">A network appears more than once.</exception>
    public IPNetworkLookup(IEnumerable<PrefixEntry<IPNetwork, TValue>> items, IPLookupOptions? options = null) : base(items, options)
    {
    }

    /// <summary>
    ///     Builds a read-only copy with faster lookups. Later changes to this lookup do not affect it. Writes wait while
    ///     the prefixes are copied out, but not while the copy's tables are built; reads never wait.
    /// </summary>
    /// <param name="options">
    ///     The layout of both address families' tables; by default each family's layout is chosen from its data.
    /// </param>
    public new FrozenIPNetworkLookup<TValue> Freeze(FrozenBitTrieOptions? options = null) => new(this, options, options);

    /// <summary>Builds a read-only copy with a different layout for each address family. See <see cref="Freeze(FrozenBitTrieOptions?)" />.</summary>
    /// <param name="ipv4">The layout of the IPv4 tables.</param>
    /// <param name="ipv6">The layout of the IPv6 tables.</param>
    public new FrozenIPNetworkLookup<TValue> Freeze(FrozenBitTrieOptions ipv4, FrozenBitTrieOptions ipv6) => new(this, ipv4, ipv6);
}

/// <summary>A read-only set of <see cref="IPNetwork" /> prefixes, built by <see cref="IPNetworkLookup{TValue}.Freeze(FrozenBitTrieOptions?)" />.</summary>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenIPNetworkLookup<TValue> : FrozenIPPrefixLookup<IPNetwork, IPNetworkAdapter, TValue>
{
    /// <summary>Freezes a read-write lookup.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenIPNetworkLookup(IPNetworkLookup<TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6) : base(source, ipv4, ipv6)
    {
    }
}

/// <summary>Shortcuts for IP lookups that store prefixes without values.</summary>
public static class NoValueLookupExtensions
{
    /// <summary>Adds a prefix. Returns false when it is already present.</summary>
    /// <param name="lookup">The lookup.</param>
    /// <param name="prefix">The prefix to add.</param>
    /// <exception cref="ArgumentException">
    ///     <see cref="IPLookupOptions.MapIPv4MappedAddresses" /> is on and the prefix lies inside <c>::ffff:0:0/96</c>.
    /// </exception>
    public static bool TryAdd<TPrefix, TAdapter>(this IPPrefixLookup<TPrefix, TAdapter, NoValue> lookup, TPrefix prefix)
        where TAdapter : struct, IIPPrefixAdapter<TPrefix> => lookup.TryAdd(prefix, default);

    /// <summary>Adds a prefix.</summary>
    /// <param name="lookup">The lookup.</param>
    /// <param name="prefix">The prefix to add.</param>
    /// <exception cref="ArgumentException">
    ///     The prefix is already present, or <see cref="IPLookupOptions.MapIPv4MappedAddresses" /> is on and the prefix lies
    ///     inside <c>::ffff:0:0/96</c>.
    /// </exception>
    public static void Add<TPrefix, TAdapter>(this IPPrefixLookup<TPrefix, TAdapter, NoValue> lookup, TPrefix prefix)
        where TAdapter : struct, IIPPrefixAdapter<TPrefix> => lookup.Add(prefix, default);

    /// <summary>Finds the longest stored prefix that contains an address.</summary>
    /// <param name="lookup">The lookup.</param>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    public static bool TryGetMatch<TPrefix>(this IReadOnlyIPPrefixLookup<TPrefix, NoValue> lookup, IPAddress address, [MaybeNullWhen(false)] out TPrefix prefix) =>
        lookup.TryGetMatch(address, out prefix, out _);
}
