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

using System.Collections.Immutable;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     A read-only set of IPv4 and IPv6 address ranges with faster lookups than
///     <see cref="IPRangeLookup{TRange,TAdapter,TValue}" />, built by its <c>Freeze</c> method.
/// </summary>
/// <remarks>A frozen lookup never changes, so any number of threads may read it.</remarks>
/// <typeparam name="TRange">The range type.</typeparam>
/// <typeparam name="TAdapter">Reads the range type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenIPRangeLookup<TRange, TAdapter, TValue> : IPRangeLookupBase<TRange, TAdapter, TValue, FrozenForm>
    where TAdapter : struct, IIPRangeAdapter<TRange>
{
    private readonly FrozenBitTrie<Bits32, TRange, RangePiece<TRange, TValue>> _v4;
    private readonly FrozenBitTrie<Bits128, TRange, RangePiece<TRange, TValue>> _v6;

    /// <summary>Freezes a read-write lookup. Derived classes call this from their own constructors.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenIPRangeLookup(IPRangeLookup<TRange, TAdapter, TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6)
        : this((source ?? throw new ArgumentNullException(nameof(source))).FreezeParts(ipv4, ipv6), source.Options)
    {
    }

    // the tries and the ranges, taken together under the read-write lookup's lock
    private FrozenIPRangeLookup(
        (FrozenBitTrie<Bits32, TRange, RangePiece<TRange, TValue>> V4, FrozenBitTrie<Bits128, TRange, RangePiece<TRange, TValue>> V6, ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> Ranges) parts,
        IPLookupOptions options)
        : base(parts.V4, parts.V6, options, parts.Ranges)
    {
        _v4 = parts.V4;
        _v6 = parts.V6;
    }

    /// <summary>The memory held by both address families' tables and pieces, in bytes, not counting the list of ranges.</summary>
    public long Bytes => _v4.Bytes + _v6.Bytes;
}
