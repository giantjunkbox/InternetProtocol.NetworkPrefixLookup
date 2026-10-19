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

namespace Bitvantage.InternetProtocol;

/// <summary>Options for the IP lookups.</summary>
public sealed record IPLookupOptions
{
    /// <summary>
    ///     Looks up IPv4-mapped IPv6 addresses, such as <c>::ffff:10.1.2.3</c>, as the IPv4 address they hold. Dual-mode
    ///     sockets report IPv4 clients this way. The default is true. Only address lookups are mapped; prefixes are
    ///     stored and looked up as given.
    /// </summary>
    public bool MapIPv4MappedAddresses { get; init; } = true;
}

/// <summary>
///     Read-only access to a set of IPv4 and IPv6 prefixes, each with a value, with longest-prefix-match lookups.
///     Implemented by the read-write and frozen IP lookups.
/// </summary>
/// <remarks>
///     Methods named <c>Match</c> find the longest stored prefix that contains an address or prefix. <c>TryGetValue</c>
///     and <c>ContainsPrefix</c> match a prefix exactly. Enumeration returns the IPv4 prefixes and then the IPv6
///     prefixes, each in address order with a shorter prefix before a longer one at the same address.
/// </remarks>
/// <typeparam name="TPrefix">The prefix type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IReadOnlyIPPrefixLookup<TPrefix, TValue> : IReadOnlyCollection<PrefixEntry<TPrefix, TValue>>
{
    /// <summary>Finds the longest stored prefix that contains an address.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatch(IPAddress address, out TPrefix prefix, out TValue value);

    /// <summary>Finds the longest stored prefix that contains an address given as bytes.</summary>
    /// <param name="address">4 bytes for IPv4 or 16 for IPv6, in network order.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatch(ReadOnlySpan<byte> address, out TPrefix prefix, out TValue value);

    /// <summary>Finds the longest stored IPv4 prefix that contains an IPv4 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit: 10.1.2.3 is 0x0A010203.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchIPv4(uint address, out TPrefix prefix, out TValue value);

    /// <summary>Finds the longest stored prefix that contains an IPv6 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchIPv6(UInt128 address, out TPrefix prefix, out TValue value);

    /// <summary>Finds the value of the longest stored prefix that contains an address.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchValue(IPAddress address, out TValue value);

    /// <summary>Finds the value of the longest stored prefix that contains an address given as bytes.</summary>
    /// <param name="address">4 bytes for IPv4 or 16 for IPv6, in network order.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchValue(ReadOnlySpan<byte> address, out TValue value);

    /// <summary>Finds the value of the longest stored IPv4 prefix that contains an IPv4 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchValueIPv4(uint address, out TValue value);

    /// <summary>Finds the value of the longest stored prefix that contains an IPv6 address.</summary>
    /// <param name="address">The address, with its first bit in the most significant bit.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the address.</returns>
    bool TryGetMatchValueIPv6(UInt128 address, out TValue value);

    /// <summary>Finds the longest stored prefix that contains a network, which may be the network itself.</summary>
    /// <param name="network">The network to look up.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored prefix contains the network.</returns>
    bool TryGetMatch(TPrefix network, out TPrefix prefix, out TValue value);

    /// <summary>Gets the value of exactly this prefix.</summary>
    /// <param name="prefix">The prefix.</param>
    /// <param name="value">Its value, when stored.</param>
    /// <returns>True when the prefix is stored.</returns>
    bool TryGetValue(TPrefix prefix, out TValue value);

    /// <summary>True when exactly this prefix is stored.</summary>
    /// <param name="prefix">The prefix.</param>
    bool ContainsPrefix(TPrefix prefix);

    /// <summary>Every stored prefix that contains an address, longest first.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(IPAddress address);

    /// <summary>Writes every stored prefix that contains an address into a span, longest first.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="destination">Where to write the matches; 129 entries always suffice.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    int GetMatches(IPAddress address, Span<PrefixEntry<TPrefix, TValue>> destination);
}
