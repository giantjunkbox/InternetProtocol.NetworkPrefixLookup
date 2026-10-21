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

namespace Bitvantage.InternetProtocol;

/// <summary>Reads <see cref="IPNetwork2" /> for the IP lookups.</summary>
public readonly struct IPNetwork2Adapter : IIPPrefixAdapter<IPNetwork2>
{
    /// <inheritdoc />
    public static bool IsIPv4(IPNetwork2 prefix) => prefix.AddressFamily == AddressFamily.InterNetwork;

    /// <inheritdoc />
    public static uint GetIPv4Bits(IPNetwork2 prefix) => IPAddressBits.ToIPv4Bits(prefix.Network);

    /// <inheritdoc />
    public static UInt128 GetIPv6Bits(IPNetwork2 prefix) => IPAddressBits.ToIPv6Bits(prefix.Network);

    /// <inheritdoc />
    public static int GetLength(IPNetwork2 prefix) => prefix.Cidr;
}

/// <summary>
///     A read-write set of <see cref="IPNetwork2" /> prefixes, each with a value, with fast longest-prefix-match lookups.
/// </summary>
/// <remarks>
///     <para>See <see cref="IPPrefixLookup{TPrefix,TAdapter,TValue}" /> for the details.</para>
/// </remarks>
/// <typeparam name="TValue">The value type; <see cref="NoValue" /> for a set of prefixes without values.</typeparam>
public class IPNetwork2Lookup<TValue> : IPPrefixLookup<IPNetwork2, IPNetwork2Adapter, TValue>
{
    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    public IPNetwork2Lookup(IPLookupOptions? options = null) : base(options)
    {
    }

    /// <summary>Creates a lookup holding the given prefixes, loaded as one batch.</summary>
    /// <param name="items">The prefixes and their values.</param>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    /// <exception cref="ArgumentException">A prefix appears more than once.</exception>
    public IPNetwork2Lookup(IEnumerable<KeyValuePair<IPNetwork2, TValue>> items, IPLookupOptions? options = null) : base(items, options)
    {
    }

    /// <summary>
    ///     Builds a read-only copy with faster lookups. Later changes to this lookup do not affect it. Writes wait while
    ///     the copy is built; reads do not.
    /// </summary>
    /// <param name="options">
    ///     The layout of both address families' tables; the default is leaf-pushed, with a root sized to each table.
    /// </param>
    public new FrozenIPNetwork2Lookup<TValue> Freeze(FrozenBitTrieOptions? options = null) => new(this, options, options);

    /// <summary>Builds a read-only copy with a different layout for each address family. See <see cref="Freeze(FrozenBitTrieOptions?)" />.</summary>
    /// <param name="ipv4">The layout of the IPv4 tables.</param>
    /// <param name="ipv6">The layout of the IPv6 tables.</param>
    public new FrozenIPNetwork2Lookup<TValue> Freeze(FrozenBitTrieOptions ipv4, FrozenBitTrieOptions ipv6) => new(this, ipv4, ipv6);
}

/// <summary>A read-only set of <see cref="IPNetwork2" /> prefixes, built by <see cref="IPNetwork2Lookup{TValue}.Freeze(FrozenBitTrieOptions?)" />.</summary>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenIPNetwork2Lookup<TValue> : FrozenIPPrefixLookup<IPNetwork2, IPNetwork2Adapter, TValue>
{
    /// <summary>Freezes a read-write lookup.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenIPNetwork2Lookup(IPNetwork2Lookup<TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6) : base(source, ipv4, ipv6)
    {
    }
}
