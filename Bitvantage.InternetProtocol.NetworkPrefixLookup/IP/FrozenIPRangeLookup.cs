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
using System.Collections.Immutable;
using System.Net;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     A read-only set of IPv4 and IPv6 address ranges with faster lookups than
///     <see cref="IPRangeLookup{TRange,TAdapter,TValue}" />, built by its <c>Freeze</c> method.
/// </summary>
/// <remarks>A frozen lookup never changes, so any number of threads may read it.</remarks>
/// <typeparam name="TRange">The range type.</typeparam>
/// <typeparam name="TAdapter">Reads the range type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenIPRangeLookup<TRange, TAdapter, TValue> : IReadOnlyIPPrefixLookup<TRange, TValue>
    where TAdapter : struct, IIPRangeAdapter<TRange>
{
    // The read-only members are the same as in IPRangeLookup; keep the two in step.
    private readonly FrozenBitTrie<Bits32, TRange, RangePiece<TRange, TValue>> _v4;
    private readonly FrozenBitTrie<Bits128, TRange, RangePiece<TRange, TValue>> _v6;
    private readonly bool _map;

    /// <summary>Freezes a read-write lookup. Derived classes call this from their own constructors.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenIPRangeLookup(IPRangeLookup<TRange, TAdapter, TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6)
    {
        ArgumentNullException.ThrowIfNull(source);
        (_v4, _v6, Ranges) = source.FreezeParts(ipv4, ipv6);
        Options = source.Options;
        _map = Options.MapIPv4MappedAddresses;
    }

    /// <summary>The options of the lookup this was frozen from.</summary>
    public IPLookupOptions Options { get; }

    /// <summary>The memory held by both address families' tables and pieces, in bytes, not counting the list of ranges.</summary>
    public long Bytes => _v4.Bytes + _v6.Bytes;

    private ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> Ranges { get; }

    // ---- lookups ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool TryGetMatch(IPAddress address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out var piece)
            : _v6.TryGetMatch(new Bits128(v6), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatch(ReadOnlySpan<byte> address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out var piece)
            : _v6.TryGetMatch(new Bits128(v6), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchIPv4(uint address, out TRange prefix, out TValue value)
    {
        var found = _v4.TryGetMatch(new Bits32(address), out prefix, out var piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchIPv6(UInt128 address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out var piece)
            : _v6.TryGetMatch(new Bits128(address), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValue(IPAddress address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatchValue(new Bits32(v4), out var piece)
            : _v6.TryGetMatchValue(new Bits128(v6), out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValue(ReadOnlySpan<byte> address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatchValue(new Bits32(v4), out var piece)
            : _v6.TryGetMatchValue(new Bits128(v6), out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValueIPv4(uint address, out TValue value)
    {
        var found = _v4.TryGetMatchValue(new Bits32(address), out var piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValueIPv6(UInt128 address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4)
            ? _v4.TryGetMatchValue(new Bits32(v4), out var piece)
            : _v6.TryGetMatchValue(new Bits128(address), out piece);
        value = piece.Value;
        return found;
    }

    /// <summary>Finds the smallest stored range that contains the whole of a range, which may be the range itself.</summary>
    /// <param name="network">The range to look up.</param>
    /// <param name="prefix">The matching range.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored range contains the range.</returns>
    public bool TryGetMatch(TRange network, out TRange prefix, out TValue value) =>
        RangePieces<TRange, TAdapter, TValue>.TryGetMatch(_v4, _v6, network, out prefix, out value);

    /// <summary>Gets the value of exactly this range.</summary>
    /// <param name="prefix">The range.</param>
    /// <param name="value">Its value, when stored.</param>
    /// <returns>True when the range is stored.</returns>
    public bool TryGetValue(TRange prefix, out TValue value)
    {
        var found = Ranges.TryGetValue(RangePieces<TRange, TAdapter, TValue>.KeyOf(prefix), out var claim);
        value = claim.Value;
        return found;
    }

    /// <summary>True when exactly this range is stored.</summary>
    /// <param name="prefix">The range.</param>
    public bool ContainsPrefix(TRange prefix) => Ranges.ContainsKey(RangePieces<TRange, TAdapter, TValue>.KeyOf(prefix));

    /// <summary>Every stored range that contains an address, smallest first.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    public IReadOnlyList<PrefixEntry<TRange, TValue>> GetMatches(IPAddress address) =>
        RangePieces<TRange, TAdapter, TValue>.Matches(IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? _v4.GetMatches(new Bits32(v4)) : _v6.GetMatches(new Bits128(v6)));

    /// <summary>Writes every stored range that contains an address into a span, smallest first.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="destination">Where to write the matches. Overlapping ranges can need any number of entries.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public int GetMatches(IPAddress address, Span<PrefixEntry<TRange, TValue>> destination) =>
        RangePieces<TRange, TAdapter, TValue>.Matches(IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? _v4.GetMatches(new Bits32(v4)) : _v6.GetMatches(new Bits128(v6)), destination);

    /// <summary>The number of stored ranges, IPv4 and IPv6.</summary>
    public int Count => Ranges.Count;

    /// <summary>Returns the IPv4 ranges and then the IPv6 ranges, each in address order, a larger range first at the same address.</summary>
    public IEnumerator<PrefixEntry<TRange, TValue>> GetEnumerator()
    {
        foreach (var claim in Ranges.Values)
            yield return new PrefixEntry<TRange, TValue>(claim.Range, claim.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
