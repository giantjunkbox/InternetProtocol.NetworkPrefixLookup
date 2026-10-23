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
using System.Diagnostics.CodeAnalysis;

namespace Bitvantage.InternetProtocol.NetworkPrefixLookup;

/// <summary>Options for the IP lookups.</summary>
public sealed record IPLookupOptions
{
    /// <summary>
    ///     Looks up IPv4-mapped IPv6 addresses, such as <c>::ffff:10.1.2.3</c>, as the IPv4 address they hold, so that
    ///     <c>::ffff:10.1.2.3</c> always gets the same answer as <c>10.1.2.3</c>. Turn this on for addresses from a
    ///     dual-mode socket, which reports IPv4 clients this way. The default is false: every address is looked up as
    ///     given, which suits packets, where a mapped address is malformed or crafted (RFC 4942, section 2.2). MaxMind
    ///     lookups turn it on by default.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Address lookups are mapped, and so are the queries about which addresses a network shares with the stored
    ///         prefixes: <c>TryGetMatch</c>, <c>GetMatches</c>, <c>GetSubnets</c> and <c>Overlaps</c> given a network. An
    ///         IPv6 network inside <c>::ffff:0:0/96</c> gets the answers of the IPv4 network it holds, so
    ///         <c>::ffff:10.1.0.0/112</c> gets those of <c>10.1.0.0/16</c>. An IPv6 network that contains all of
    ///         <c>::ffff:0:0/96</c>, such as <c>::/0</c>, contains every IPv4 prefix too: <c>GetSubnets</c> returns them
    ///         first, as enumeration does, and <c>Overlaps</c> counts them.
    ///     </para>
    ///     <para>
    ///         Writes and the exact lookups, <c>TryGetValue</c> and <c>ContainsPrefix</c>, take a prefix as given. With this
    ///         on, adding an IPv6 prefix inside <c>::ffff:0:0/96</c> throws <see cref="ArgumentException" />, because no
    ///         address lookup could return it: add the IPv4 prefix instead.
    ///     </para>
    /// </remarks>
    public bool MapIPv4MappedAddresses { get; init; }
}

/// <summary>
///     Read-only access to a set of IPv4 and IPv6 prefixes, each with a value, with longest-prefix-match lookups.
///     Implemented by the read-write and frozen IP lookups.
/// </summary>
/// <remarks>
///     Methods named <c>Match</c> find the longest stored prefix that contains an address or prefix, and
///     <c>GetMatches</c> every one, longest first. <c>GetSubnets</c> finds the stored prefixes inside a network, and
///     <c>Overlaps</c> whether there are any either way. <c>TryGetValue</c> and <c>ContainsPrefix</c> match a prefix
///     exactly. Enumeration returns the IPv4 prefixes and then the IPv6 prefixes, each in address order with a shorter
///     prefix before a longer one at the same address.
/// </remarks>
/// <typeparam name="TPrefix">The prefix type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IReadOnlyIPPrefixLookup<TPrefix, TValue> : IReadOnlyCollection<PrefixEntry<TPrefix, TValue>>
{
    /// <summary>The number of stored IPv4 prefixes.</summary>
    /// <remarks>
    ///     Each count is read on its own, so while a read-write lookup changes, <see cref="IPv4Count" />,
    ///     <see cref="IPv6Count" /> and <see cref="IReadOnlyCollection{T}.Count" /> read one after another need not add
    ///     up. Changes in an uncommitted batch do not count.
    /// </remarks>
    int IPv4Count { get; }

    /// <summary>The number of stored IPv6 prefixes.</summary>
    /// <remarks>See <see cref="IPv4Count" />.</remarks>
    int IPv6Count { get; }

    /// <summary>Finds the longest stored prefix that contains an address.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatch(IPAddress address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the longest stored prefix that contains an address given as bytes.</summary>
    /// <param name="address">4 bytes for IPv4 or 16 for IPv6, in network order.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatch(ReadOnlySpan<byte> address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the longest stored IPv4 prefix that contains an IPv4 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit: 10.1.2.3 is 0x0A010203.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchIPv4(uint address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the longest stored prefix that contains an IPv6 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchIPv6(UInt128 address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>True when a stored prefix contains an address. Faster than a match that returns the prefix or value.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    bool ContainsMatch(IPAddress address);

    /// <summary>True when a stored prefix contains an address given as bytes.</summary>
    /// <param name="address">4 bytes for IPv4 or 16 for IPv6, in network order.</param>
    bool ContainsMatch(ReadOnlySpan<byte> address);

    /// <summary>True when a stored IPv4 prefix contains an IPv4 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    bool ContainsMatchIPv4(uint address);

    /// <summary>True when a stored prefix contains an IPv6 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    bool ContainsMatchIPv6(UInt128 address);

    /// <summary>Finds the value of the longest stored prefix that contains an address.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchValue(IPAddress address, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the value of the longest stored prefix that contains an address given as bytes.</summary>
    /// <param name="address">4 bytes for IPv4 or 16 for IPv6, in network order.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchValue(ReadOnlySpan<byte> address, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the value of the longest stored IPv4 prefix that contains an IPv4 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchValueIPv4(uint address, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the value of the longest stored prefix that contains an IPv6 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchValueIPv6(UInt128 address, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the longest stored prefix that contains a network, which may be the network itself.</summary>
    /// <param name="network">The network to look up; host bits after its length are ignored.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the network.</returns>
    bool TryGetMatch(TPrefix network, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Gets the value of exactly this prefix.</summary>
    /// <param name="prefix">The prefix.</param>
    /// <param name="value">Its value, when stored.</param>
    /// <returns>True when the prefix is stored.</returns>
    bool TryGetValue(TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>True when exactly this prefix is stored.</summary>
    /// <param name="prefix">The prefix.</param>
    bool ContainsPrefix(TPrefix prefix);

    /// <summary>Every stored prefix that contains an address, longest first.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(IPAddress address);

    /// <summary>Writes every stored prefix that contains an address into a span, in the order of <see cref="GetMatches(IPAddress)" />.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="destination">Where to write the matches; 129 entries always suffice.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    int GetMatches(IPAddress address, Span<PrefixEntry<TPrefix, TValue>> destination);

    /// <summary>Every stored prefix that contains an address given as bytes, longest first.</summary>
    /// <param name="address">4 bytes for IPv4 or 16 for IPv6, in network order.</param>
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(ReadOnlySpan<byte> address);

    /// <summary>Writes every stored prefix that contains an address given as bytes into a span, longest first.</summary>
    /// <param name="address">4 bytes for IPv4 or 16 for IPv6, in network order.</param>
    /// <param name="destination">Where to write the matches; 129 entries always suffice.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    int GetMatches(ReadOnlySpan<byte> address, Span<PrefixEntry<TPrefix, TValue>> destination);

    /// <summary>Every stored IPv4 prefix that contains an IPv4 address, longest first.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit: 10.1.2.3 is 0x0A010203.</param>
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatchesIPv4(uint address);

    /// <summary>Writes every stored IPv4 prefix that contains an IPv4 address into a span, longest first.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit: 10.1.2.3 is 0x0A010203.</param>
    /// <param name="destination">Where to write the matches; 33 entries always suffice.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    int GetMatchesIPv4(uint address, Span<PrefixEntry<TPrefix, TValue>> destination);

    /// <summary>Every stored prefix that contains an IPv6 address, longest first.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatchesIPv6(UInt128 address);

    /// <summary>Writes every stored prefix that contains an IPv6 address into a span, longest first.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    /// <param name="destination">Where to write the matches; 129 entries always suffice.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    int GetMatchesIPv6(UInt128 address, Span<PrefixEntry<TPrefix, TValue>> destination);

    /// <summary>
    ///     Every stored prefix that contains a network, the network itself included, longest first: which prefixes cover
    ///     it. The first is the one <see cref="TryGetMatch(TPrefix, out TPrefix, out TValue)" /> finds.
    /// </summary>
    /// <param name="network">The network; host bits after its length are ignored.</param>
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(TPrefix network);

    /// <summary>Writes every stored prefix that contains a network into a span, in the order of <see cref="GetMatches(TPrefix)" />.</summary>
    /// <param name="network">The network; host bits after its length are ignored.</param>
    /// <param name="destination">Where to write the matches; 129 entries always suffice.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    int GetMatches(TPrefix network, Span<PrefixEntry<TPrefix, TValue>> destination);

    /// <summary>
    ///     Every stored prefix that lies inside a network, the network itself included, in address order with a shorter
    ///     prefix before a longer one at the same address: what is allocated inside it. The prefixes are produced as the
    ///     result is enumerated, so <c>GetSubnets(network).Any()</c> stops at the first.
    /// </summary>
    /// <param name="network">The network; host bits after its length are ignored.</param>
    /// <remarks>
    ///     On a read-write lookup that changes while the result is enumerated, each prefix is seen whole, but the result is
    ///     not the lookup at one moment, as with enumerating the whole lookup.
    /// </remarks>
    IEnumerable<PrefixEntry<TPrefix, TValue>> GetSubnets(TPrefix network);

    /// <summary>
    ///     True when a stored prefix shares an address with a network: when one contains it or lies inside it, which are
    ///     the only ways two prefixes can overlap. Faster than <see cref="GetMatches(TPrefix)" /> or
    ///     <see cref="GetSubnets" />, and allocates nothing.
    /// </summary>
    /// <param name="network">The network; host bits after its length are ignored.</param>
    /// <remarks>
    ///     A stored default route overlaps every network of its family. To find whether anything is allocated inside a
    ///     network, use <c>GetSubnets(network).Any()</c> instead, since the pool the network was taken from overlaps it.
    /// </remarks>
    bool Overlaps(TPrefix network);
}
