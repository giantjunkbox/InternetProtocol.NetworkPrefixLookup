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

using System.Net.Sockets;
using MaxMind.Db;

namespace Bitvantage.InternetProtocol;

/// <summary>Reads <see cref="Network" /> for the IP lookups.</summary>
public readonly struct MaxMindNetworkAdapter : IIPPrefixAdapter<Network>
{
    /// <inheritdoc />
    public static bool IsIPv4(Network prefix) => prefix.NetworkAddress.AddressFamily == AddressFamily.InterNetwork;

    /// <inheritdoc />
    public static uint GetIPv4Bits(Network prefix) => IPAddressBits.ToIPv4Bits(prefix.NetworkAddress);

    /// <inheritdoc />
    public static UInt128 GetIPv6Bits(Network prefix) => IPAddressBits.ToIPv6Bits(prefix.NetworkAddress);

    /// <inheritdoc />
    public static int GetLength(Network prefix) => prefix.PrefixLength;
}

/// <summary>
///     A read-write set of <see cref="Network" /> prefixes, each with a value, with fast longest-prefix-match lookups.
/// </summary>
/// <remarks>
///     <para>See <see cref="IPPrefixLookup{TPrefix,TAdapter,TValue}" /> for the details.</para>
///     <para><see cref="MaxMindDatabase.Load{T}(Reader,MaxMindLoadOptions?)" /> fills one from a MaxMind DB file.</para>
/// </remarks>
/// <typeparam name="TValue">The value type; <see cref="NoValue" /> for a set of prefixes without values.</typeparam>
public class MaxMindNetworkLookup<TValue> : IPPrefixLookup<Network, MaxMindNetworkAdapter, TValue>
{
    // MaxMind's IPv6 databases answer IPv4-mapped addresses from the IPv4 networks, so these lookups map them by default
    private static readonly IPLookupOptions MapMapped = new() { MapIPv4MappedAddresses = true };

    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; by default IPv4-mapped IPv6 addresses are looked up as IPv4, as MaxMind's reader does.</param>
    public MaxMindNetworkLookup(IPLookupOptions? options = null) : base(options ?? MapMapped)
    {
    }

    /// <summary>Creates a lookup holding the given prefixes, loaded as one batch.</summary>
    /// <param name="items">The prefixes and their values.</param>
    /// <param name="options">Lookup options; by default IPv4-mapped IPv6 addresses are looked up as IPv4, as MaxMind's reader does.</param>
    /// <exception cref="ArgumentException">A prefix appears more than once.</exception>
    public MaxMindNetworkLookup(IEnumerable<KeyValuePair<Network, TValue>> items, IPLookupOptions? options = null) : base(items, options ?? MapMapped)
    {
    }

    /// <summary>
    ///     Builds a read-only copy with faster lookups. Later changes to this lookup do not affect it. Writes wait while
    ///     the prefixes are copied out, but not while the copy's tables are built; reads never wait.
    /// </summary>
    /// <param name="options">
    ///     The layout of both address families' tables; by default each family's layout is chosen from its data.
    /// </param>
    public new FrozenMaxMindNetworkLookup<TValue> Freeze(FrozenBitTrieOptions? options = null) => new(this, options, options);

    /// <summary>Builds a read-only copy with a different layout for each address family. See <see cref="Freeze(FrozenBitTrieOptions?)" />.</summary>
    /// <param name="ipv4">The layout of the IPv4 tables.</param>
    /// <param name="ipv6">The layout of the IPv6 tables.</param>
    public new FrozenMaxMindNetworkLookup<TValue> Freeze(FrozenBitTrieOptions ipv4, FrozenBitTrieOptions ipv6) => new(this, ipv4, ipv6);
}

/// <summary>A read-only set of <see cref="Network" /> prefixes, built by <see cref="MaxMindNetworkLookup{TValue}.Freeze(FrozenBitTrieOptions?)" />.</summary>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenMaxMindNetworkLookup<TValue> : FrozenIPPrefixLookup<Network, MaxMindNetworkAdapter, TValue>
{
    /// <summary>Freezes a read-write lookup.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenMaxMindNetworkLookup(MaxMindNetworkLookup<TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6) : base(source, ipv4, ipv6)
    {
    }
}
