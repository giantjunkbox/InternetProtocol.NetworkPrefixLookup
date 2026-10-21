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
using NetTools;

namespace Bitvantage.InternetProtocol;

/// <summary>Reads <see cref="IPAddressRange" /> for the range lookups.</summary>
public readonly struct IPAddressRangeAdapter : IIPRangeAdapter<IPAddressRange>
{
    /// <inheritdoc />
    public static IPAddress GetFirst(IPAddressRange range) => range.Begin;

    /// <inheritdoc />
    public static IPAddress GetLast(IPAddressRange range) => range.End;
}

/// <summary>
///     A read-write set of <see cref="IPAddressRange" /> ranges, each with a value, with fast lookups of the smallest
///     range that contains an address.
/// </summary>
/// <remarks>
///     <para>See <see cref="IPRangeLookup{TRange,TAdapter,TValue}" /> for the details.</para>
///     <para>
///         <see cref="IPAddressRange" /> is mutable. A range is read when it is added, so changing its
///         <see cref="IPAddressRange.Begin" /> or <see cref="IPAddressRange.End" /> afterwards is not seen by lookups,
///         and the range can then no longer be found by <c>Remove</c> or <c>TryGetValue</c> under its new ends.
///     </para>
/// </remarks>
/// <typeparam name="TValue">The value type.</typeparam>
public class IPAddressRangeLookup<TValue> : IPRangeLookup<IPAddressRange, IPAddressRangeAdapter, TValue>
{
    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    public IPAddressRangeLookup(IPLookupOptions? options = null) : base(options)
    {
    }

    /// <summary>Creates a lookup holding the given ranges, loaded as one batch.</summary>
    /// <param name="items">The ranges and their values.</param>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    /// <exception cref="ArgumentException">A range appears more than once.</exception>
    public IPAddressRangeLookup(IEnumerable<KeyValuePair<IPAddressRange, TValue>> items, IPLookupOptions? options = null) : base(items, options)
    {
    }

    /// <summary>
    ///     Builds a read-only copy with faster lookups. Later changes to this lookup do not affect it. Writes wait while
    ///     the copy is built; reads do not.
    /// </summary>
    /// <param name="options">
    ///     The layout of both address families' tables; the default is leaf-pushed, with a root sized to each table.
    /// </param>
    public new FrozenIPAddressRangeLookup<TValue> Freeze(FrozenBitTrieOptions? options = null) => new(this, options, options);

    /// <summary>Builds a read-only copy with a different layout for each address family. See <see cref="Freeze(FrozenBitTrieOptions?)" />.</summary>
    /// <param name="ipv4">The layout of the IPv4 tables.</param>
    /// <param name="ipv6">The layout of the IPv6 tables.</param>
    public new FrozenIPAddressRangeLookup<TValue> Freeze(FrozenBitTrieOptions ipv4, FrozenBitTrieOptions ipv6) => new(this, ipv4, ipv6);
}

/// <summary>A read-only set of <see cref="IPAddressRange" /> ranges, built by <see cref="IPAddressRangeLookup{TValue}.Freeze(FrozenBitTrieOptions?)" />.</summary>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenIPAddressRangeLookup<TValue> : FrozenIPRangeLookup<IPAddressRange, IPAddressRangeAdapter, TValue>
{
    /// <summary>Freezes a read-write lookup.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenIPAddressRangeLookup(IPAddressRangeLookup<TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6) : base(source, ipv4, ipv6)
    {
    }
}
