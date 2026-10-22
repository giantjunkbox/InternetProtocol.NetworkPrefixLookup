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

/// <summary>Reads <see cref="NetworkPrefix" /> for the IP lookups.</summary>
public readonly struct NetworkPrefixAdapter : IIPPrefixAdapter<NetworkPrefix>
{
    /// <inheritdoc />
    public static bool IsIPv4(NetworkPrefix prefix) => prefix.Version == IPVersion.IPv4;

    /// <inheritdoc />
    public static uint GetIPv4Bits(NetworkPrefix prefix) => (uint)prefix.NetworkBits;

    /// <inheritdoc />
    public static UInt128 GetIPv6Bits(NetworkPrefix prefix) => prefix.NetworkBits;

    /// <inheritdoc />
    public static int GetLength(NetworkPrefix prefix) => prefix.Length;
}

/// <summary>
///     A read-write set of <see cref="NetworkPrefix" /> prefixes, each with a value, with fast longest-prefix-match lookups.
/// </summary>
/// <remarks>
///     <para>See <see cref="IPPrefixLookup{TPrefix,TAdapter,TValue}" /> for the details.</para>
/// </remarks>
/// <typeparam name="TValue">The value type; <see cref="NoValue" /> for a set of prefixes without values.</typeparam>
public class NetworkPrefixLookup<TValue> : IPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, TValue>
{
    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    public NetworkPrefixLookup(IPLookupOptions? options = null) : base(options)
    {
    }

    /// <summary>Creates a lookup holding the given prefixes, loaded as one batch.</summary>
    /// <param name="items">The prefixes and their values.</param>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    /// <exception cref="ArgumentException">A prefix appears more than once.</exception>
    public NetworkPrefixLookup(IEnumerable<KeyValuePair<NetworkPrefix, TValue>> items, IPLookupOptions? options = null) : base(items, options)
    {
    }

    /// <summary>
    ///     Builds a read-only copy with faster lookups. Later changes to this lookup do not affect it. Writes wait while
    ///     the prefixes are copied out, but not while the copy's tables are built; reads never wait.
    /// </summary>
    /// <param name="options">
    ///     The layout of both address families' tables; by default each family's layout is chosen from its data.
    /// </param>
    public new FrozenNetworkPrefixLookup<TValue> Freeze(FrozenBitTrieOptions? options = null) => new(this, options, options);

    /// <summary>Builds a read-only copy with a different layout for each address family. See <see cref="Freeze(FrozenBitTrieOptions?)" />.</summary>
    /// <param name="ipv4">The layout of the IPv4 tables.</param>
    /// <param name="ipv6">The layout of the IPv6 tables.</param>
    public new FrozenNetworkPrefixLookup<TValue> Freeze(FrozenBitTrieOptions ipv4, FrozenBitTrieOptions ipv6) => new(this, ipv4, ipv6);
}

/// <summary>A read-only set of <see cref="NetworkPrefix" /> prefixes, built by <see cref="NetworkPrefixLookup{TValue}.Freeze(FrozenBitTrieOptions?)" />.</summary>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenNetworkPrefixLookup<TValue> : FrozenIPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, TValue>
{
    /// <summary>Freezes a read-write lookup.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenNetworkPrefixLookup(NetworkPrefixLookup<TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6) : base(source, ipv4, ipv6)
    {
    }
}

/// <summary>
///     A read-write set of <see cref="NetworkPrefix" /> prefixes without values, the successor of the value-less lookup
///     of version 2.
/// </summary>
public class NetworkPrefixLookup : NetworkPrefixLookup<NoValue>
{
    /// <summary>Creates an empty set.</summary>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    public NetworkPrefixLookup(IPLookupOptions? options = null) : base(options)
    {
    }

    /// <summary>Creates a set holding the given prefixes, loaded as one batch.</summary>
    /// <param name="prefixes">The prefixes.</param>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    /// <exception cref="ArgumentException">A prefix appears more than once.</exception>
    public NetworkPrefixLookup(IEnumerable<NetworkPrefix> prefixes, IPLookupOptions? options = null)
        : base((prefixes ?? throw new ArgumentNullException(nameof(prefixes))).Select(p => KeyValuePair.Create(p, default(NoValue))), options)
    {
    }

    /// <summary>
    ///     Builds a read-only copy with faster lookups. Later changes to this set do not affect it. Writes wait while the
    ///     prefixes are copied out, but not while the copy's tables are built; reads never wait.
    /// </summary>
    /// <param name="options">
    ///     The layout of both address families' tables; by default each family's layout is chosen from its data.
    /// </param>
    public new FrozenNetworkPrefixLookup Freeze(FrozenBitTrieOptions? options = null) => new(this, options, options);

    /// <summary>Builds a read-only copy with a different layout for each address family. See <see cref="Freeze(FrozenBitTrieOptions?)" />.</summary>
    /// <param name="ipv4">The layout of the IPv4 tables.</param>
    /// <param name="ipv6">The layout of the IPv6 tables.</param>
    public new FrozenNetworkPrefixLookup Freeze(FrozenBitTrieOptions ipv4, FrozenBitTrieOptions ipv6) => new(this, ipv4, ipv6);
}

/// <summary>A read-only set of <see cref="NetworkPrefix" /> prefixes without values, built by <see cref="NetworkPrefixLookup.Freeze(FrozenBitTrieOptions?)" />.</summary>
public class FrozenNetworkPrefixLookup : FrozenNetworkPrefixLookup<NoValue>
{
    /// <summary>Freezes a read-write set.</summary>
    /// <param name="source">The set to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenNetworkPrefixLookup(NetworkPrefixLookup source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6) : base(source, ipv4, ipv6)
    {
    }
}
