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
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     The read-only members of <see cref="IPRangeLookup{TRange,TAdapter,TValue}" /> and
///     <see cref="FrozenIPRangeLookup{TRange,TAdapter,TValue}" />, written once. Infrastructure: use one of those.
/// </summary>
/// <remarks>
///     <typeparamref name="TForm" /> is <see cref="ReadWriteForm" /> or <see cref="FrozenForm" />. It tells the JIT which
///     trie type holds each address family, so that each form's lookups call their tries directly and inline them, as
///     if each class had its own copy of these members.
/// </remarks>
/// <typeparam name="TRange">The range type.</typeparam>
/// <typeparam name="TAdapter">Reads the range type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <typeparam name="TForm">The form of the lookup.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class IPRangeLookupBase<TRange, TAdapter, TValue, TForm> : IReadOnlyIPPrefixLookup<TRange, TValue>
    where TAdapter : struct, IIPRangeAdapter<TRange>
    where TForm : struct
{
    // a BitTrie in the read-write form, a FrozenBitTrie in the frozen form
    private readonly IReadOnlyBitTrie<Bits32, TRange, RangePiece<TRange, TValue>> _ipv4;
    private readonly IReadOnlyBitTrie<Bits128, TRange, RangePiece<TRange, TValue>> _ipv6;
    private readonly bool _map;

    // the ranges readers see; the read-write form replaces it with every published change
    private protected ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> _published;

    private protected IPRangeLookupBase(IReadOnlyBitTrie<Bits32, TRange, RangePiece<TRange, TValue>> ipv4, IReadOnlyBitTrie<Bits128, TRange, RangePiece<TRange, TValue>> ipv6,
        IPLookupOptions options, ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> ranges)
    {
        _ipv4 = ipv4;
        _ipv6 = ipv6;
        Options = options;
        _map = options.MapIPv4MappedAddresses;
        _published = ranges;
    }

    /// <summary>The lookup's options; a frozen lookup has those of the lookup it was frozen from.</summary>
    public IPLookupOptions Options { get; }

    private ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> Ranges => Volatile.Read(ref _published);

    // ---- lookups ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool TryGetMatch(IPAddress address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? Match4(new Bits32(v4), out prefix, out var piece)
            : Match6(new Bits128(v6), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatch(ReadOnlySpan<byte> address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? Match4(new Bits32(v4), out prefix, out var piece)
            : Match6(new Bits128(v6), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchIPv4(uint address, out TRange prefix, out TValue value)
    {
        var found = Match4(new Bits32(address), out prefix, out var piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchIPv6(UInt128 address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4)
            ? Match4(new Bits32(v4), out prefix, out var piece)
            : Match6(new Bits128(address), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValue(IPAddress address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? MatchValue4(new Bits32(v4), out var piece)
            : MatchValue6(new Bits128(v6), out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValue(ReadOnlySpan<byte> address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? MatchValue4(new Bits32(v4), out var piece)
            : MatchValue6(new Bits128(v6), out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValueIPv4(uint address, out TValue value)
    {
        var found = MatchValue4(new Bits32(address), out var piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValueIPv6(UInt128 address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4)
            ? MatchValue4(new Bits32(v4), out var piece)
            : MatchValue6(new Bits128(address), out piece);
        value = piece.Value;
        return found;
    }

    /// <summary>Finds the smallest stored range that contains the whole of a range, which may be the range itself.</summary>
    /// <param name="network">The range to look up.</param>
    /// <param name="prefix">The matching range.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored range contains the range.</returns>
    public bool TryGetMatch(TRange network, out TRange prefix, out TValue value) =>
        RangePieces<TRange, TAdapter, TValue>.TryGetMatch(_ipv4, _ipv6, network, out prefix, out value);

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
        RangePieces<TRange, TAdapter, TValue>.Matches(IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? _ipv4.GetMatches(new Bits32(v4)) : _ipv6.GetMatches(new Bits128(v6)));

    /// <summary>Writes every stored range that contains an address into a span, smallest first.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="destination">Where to write the matches. Overlapping ranges can need any number of entries.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public int GetMatches(IPAddress address, Span<PrefixEntry<TRange, TValue>> destination)
    {
        var matches = GetMatches(address);
        if (matches.Count > destination.Length)
            throw new ArgumentException("The destination is too small for the matches.", nameof(destination));
        for (var i = 0; i < matches.Count; i++)
            destination[i] = matches[i];
        return matches.Count;
    }

    /// <summary>The number of stored ranges, IPv4 and IPv6.</summary>
    public int Count => Ranges.Count;

    /// <summary>Returns the IPv4 ranges and then the IPv6 ranges, each in address order, a larger range first at the same address.</summary>
    public IEnumerator<PrefixEntry<TRange, TValue>> GetEnumerator()
    {
        foreach (var claim in Ranges.Values)
            yield return new PrefixEntry<TRange, TValue>(claim.Range, claim.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- calls into the tries --------------------------------------------------------------------------------------

    // Each casts the trie to its form's sealed type. The JIT resolves the typeof test when it compiles the form, so only
    // a direct call remains and the trie's lookup inlines. A call through the interface would do neither.
    private static bool ReadWrite
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => typeof(TForm) == typeof(ReadWriteForm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Match4(in Bits32 key, out TRange prefix, out RangePiece<TRange, TValue> piece) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TRange, RangePiece<TRange, TValue>>>(_ipv4).TryGetMatch(in key, out prefix, out piece)
        : Unsafe.As<FrozenBitTrie<Bits32, TRange, RangePiece<TRange, TValue>>>(_ipv4).TryGetMatch(in key, out prefix, out piece);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Match6(in Bits128 key, out TRange prefix, out RangePiece<TRange, TValue> piece) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TRange, RangePiece<TRange, TValue>>>(_ipv6).TryGetMatch(in key, out prefix, out piece)
        : Unsafe.As<FrozenBitTrie<Bits128, TRange, RangePiece<TRange, TValue>>>(_ipv6).TryGetMatch(in key, out prefix, out piece);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchValue4(in Bits32 key, out RangePiece<TRange, TValue> piece) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TRange, RangePiece<TRange, TValue>>>(_ipv4).TryGetMatchValue(in key, out piece)
        : Unsafe.As<FrozenBitTrie<Bits32, TRange, RangePiece<TRange, TValue>>>(_ipv4).TryGetMatchValue(in key, out piece);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchValue6(in Bits128 key, out RangePiece<TRange, TValue> piece) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TRange, RangePiece<TRange, TValue>>>(_ipv6).TryGetMatchValue(in key, out piece)
        : Unsafe.As<FrozenBitTrie<Bits128, TRange, RangePiece<TRange, TValue>>>(_ipv6).TryGetMatchValue(in key, out piece);
}
