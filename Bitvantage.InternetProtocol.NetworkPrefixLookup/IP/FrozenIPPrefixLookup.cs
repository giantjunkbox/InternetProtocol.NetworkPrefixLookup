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
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Bitvantage.InternetProtocol.NetworkPrefixLookup;

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
    private readonly FrozenBitTrie<Bits32, TPrefix, TValue> _v4;
    private readonly FrozenBitTrie<Bits128, TPrefix, TValue> _v6;
    private readonly IPLookupReader<TPrefix, TAdapter, TValue, FrozenForm> _reader;

    /// <summary>Freezes a read-write lookup. Derived classes call this from their own constructors.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenIPPrefixLookup(IPPrefixLookup<TPrefix, TAdapter, TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6)
        : this((source ?? throw new ArgumentNullException(nameof(source))).FreezeParts(ipv4, ipv6), source.Options)
    {
    }

    // both families, frozen together under the read-write lookup's locks
    private FrozenIPPrefixLookup((FrozenBitTrie<Bits32, TPrefix, TValue> V4, FrozenBitTrie<Bits128, TPrefix, TValue> V6) parts, IPLookupOptions options)
    {
        _v4 = parts.V4;
        _v6 = parts.V6;
        Options = options;
        _reader = new(parts.V4, parts.V6, options.MapIPv4MappedAddresses);
    }

    /// <summary>The memory held by both address families' tables and entries, in bytes.</summary>
    public long Bytes => _v4.Bytes + _v6.Bytes;

    // ---- lookups, written once in IPLookupReader -------------------------------------------------------------------

    /// <summary>The lookup's options; a frozen lookup has those of the lookup it was frozen from.</summary>
    public IPLookupOptions Options { get; }

    /// <inheritdoc />
    public int Count => _reader.Count;

    /// <inheritdoc />
    public int IPv4Count => _reader.IPv4Count;

    /// <inheritdoc />
    public int IPv6Count => _reader.IPv6Count;

    /// <inheritdoc />
    public bool TryGetMatch(IPAddress address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatch(address, out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatch(ReadOnlySpan<byte> address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatch(address, out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatchIPv4(uint address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatchIPv4(address, out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatchIPv6(UInt128 address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatchIPv6(address, out prefix, out value);

    /// <inheritdoc />
    public bool ContainsMatch(IPAddress address) => _reader.ContainsMatch(address);

    /// <inheritdoc />
    public bool ContainsMatch(ReadOnlySpan<byte> address) => _reader.ContainsMatch(address);

    /// <inheritdoc />
    public bool ContainsMatchIPv4(uint address) => _reader.ContainsMatchIPv4(address);

    /// <inheritdoc />
    public bool ContainsMatchIPv6(UInt128 address) => _reader.ContainsMatchIPv6(address);

    /// <inheritdoc />
    public bool TryGetMatchValue(IPAddress address, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatchValue(address, out value);

    /// <inheritdoc />
    public bool TryGetMatchValue(ReadOnlySpan<byte> address, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatchValue(address, out value);

    /// <inheritdoc />
    public bool TryGetMatchValueIPv4(uint address, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatchValueIPv4(address, out value);

    /// <inheritdoc />
    public bool TryGetMatchValueIPv6(UInt128 address, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatchValueIPv6(address, out value);

    /// <inheritdoc />
    public bool TryGetMatch(TPrefix network, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetMatch(network, out prefix, out value);

    /// <inheritdoc />
    public bool TryGetValue(TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => _reader.TryGetValue(prefix, out value);

    /// <inheritdoc />
    public bool ContainsPrefix(TPrefix prefix) => _reader.ContainsPrefix(prefix);

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(IPAddress address) => _reader.GetMatches(address);

    /// <inheritdoc />
    public int GetMatches(IPAddress address, Span<PrefixEntry<TPrefix, TValue>> destination) => _reader.GetMatches(address, destination);

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(ReadOnlySpan<byte> address) => _reader.GetMatches(address);

    /// <inheritdoc />
    public int GetMatches(ReadOnlySpan<byte> address, Span<PrefixEntry<TPrefix, TValue>> destination) => _reader.GetMatches(address, destination);

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatchesIPv4(uint address) => _reader.GetMatchesIPv4(address);

    /// <inheritdoc />
    public int GetMatchesIPv4(uint address, Span<PrefixEntry<TPrefix, TValue>> destination) => _reader.GetMatchesIPv4(address, destination);

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatchesIPv6(UInt128 address) => _reader.GetMatchesIPv6(address);

    /// <inheritdoc />
    public int GetMatchesIPv6(UInt128 address, Span<PrefixEntry<TPrefix, TValue>> destination) => _reader.GetMatchesIPv6(address, destination);

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(TPrefix network) => _reader.GetMatches(network);

    /// <inheritdoc />
    public int GetMatches(TPrefix network, Span<PrefixEntry<TPrefix, TValue>> destination) => _reader.GetMatches(network, destination);

    /// <inheritdoc />
    public IEnumerable<PrefixEntry<TPrefix, TValue>> GetSubnets(TPrefix network) => _reader.GetSubnets(network);

    /// <inheritdoc />
    public bool Overlaps(TPrefix network) => _reader.Overlaps(network);

    /// <summary>Returns the IPv4 prefixes and then the IPv6 prefixes, each in address order.</summary>
    public IEnumerator<PrefixEntry<TPrefix, TValue>> GetEnumerator() => _reader.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
