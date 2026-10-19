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
using System.Runtime.CompilerServices;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     A set of IPv4 and IPv6 network prefixes that supports longest-prefix-match lookups. Use
///     <see cref="NetworkPrefixLookup{TValue}" /> to associate a value with each prefix.
/// </summary>
/// <remarks>
///     One writer may run concurrently with any number of readers. Use <see cref="ConcurrenceNetworkPrefixLookup" />
///     when several threads write.
/// </remarks>
/// <example>
///     <code>
///     var lookup = new NetworkPrefixLookup();
///     lookup.Add("10.0.0.0/8");
///     lookup.Add("10.1.0.0/16");
///     var match = lookup.GetMatch(IPAddress.Parse("10.1.2.3")); // 10.1.0.0/16
///     </code>
/// </example>
public class NetworkPrefixLookup : NetworkPrefixLookupBase<NetworkPrefixKey>
{
    /// <summary>
    ///     Adds a prefix. A string such as <c>"10.0.0.0/8"</c> converts implicitly to <see cref="NetworkPrefix" />.
    /// </summary>
    /// <param name="prefix">The prefix to add.</param>
    /// <exception cref="ArgumentException">The prefix already exists.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void Add(NetworkPrefix prefix)
    {
        Add(new NetworkPrefixKey(prefix));
    }

    /// <summary>
    ///     Adds a prefix if it is not already present.
    /// </summary>
    /// <param name="prefix">The prefix to add.</param>
    /// <returns><see langword="true" /> if the prefix was added; <see langword="false" /> if it already existed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual bool TryAdd(NetworkPrefix prefix)
    {
        return TryAdd(new NetworkPrefixKey(prefix));
    }
}

/// <summary>
///     A map from IPv4 and IPv6 network prefixes to values that supports longest-prefix-match lookups.
/// </summary>
/// <typeparam name="TValue">The type of the value associated with each prefix.</typeparam>
/// <remarks>
///     <para>
///         Lookups return <see cref="NetworkPrefixKeyValuePair{TValue}" /> items, so the caller gets both the matching
///         prefix and its value.
///     </para>
///     <para>
///         One writer may run concurrently with any number of readers. Use
///         <see cref="ConcurrenceNetworkPrefixLookup{TValue}" /> when several threads write.
///     </para>
///     <para>
///         KNOWN ISSUE: the class declares <see cref="IEnumerable{T}" /> but enumeration is not implemented.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     var routes = new NetworkPrefixLookup&lt;string&gt;();
///     routes.Add("0.0.0.0/0", "default");
///     routes.Add("192.168.0.0/16", "lan");
///     var nextHop = routes.GetMatch(IPAddress.Parse("192.168.1.10")).Value; // "lan"
///     </code>
/// </example>
public class NetworkPrefixLookup<TValue> : NetworkPrefixLookupBase<NetworkPrefixKeyValuePair<TValue>>, IEnumerable<NetworkPrefixKeyValuePair<TValue>>
{
    /// <summary>
    ///     Adds a prefix and its value. A string such as <c>"10.0.0.0/8"</c> converts implicitly to
    ///     <see cref="NetworkPrefix" />.
    /// </summary>
    /// <param name="prefix">The prefix to add.</param>
    /// <param name="value">The value to associate with the prefix.</param>
    /// <exception cref="ArgumentException">The prefix already exists.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void Add(NetworkPrefix prefix, TValue? value)
    {
        Add(new NetworkPrefixKeyValuePair<TValue>(prefix, value));
    }

    /// <summary>
    ///     Returns the longest stored prefix that equals or contains <paramref name="address" />, or adds
    ///     <paramref name="address" /> with a value from <paramref name="valueFactoryFunc" /> when nothing matches.
    /// </summary>
    /// <param name="address">The prefix to look up or add.</param>
    /// <param name="valueFactoryFunc">Creates the value when no stored prefix matches.</param>
    /// <returns>The matching item, or the newly created item.</returns>
    /// <remarks>
    ///     <para>
    ///         This is a longest-prefix match, not an exact match. If a shorter prefix that contains
    ///         <paramref name="address" /> is already stored, that item is returned and nothing is added. For example,
    ///         when <c>10.0.0.0/8</c> is stored, <c>GetOrAdd("10.1.0.0/16", ...)</c> returns the <c>10.0.0.0/8</c> item.
    ///     </para>
    ///     <para>
    ///         The lookup and the add are separate steps and are not atomic, even in
    ///         <see cref="ConcurrenceNetworkPrefixLookup{TValue}" />. Two concurrent callers can both miss; the second
    ///         add then fails, but the second caller still receives its own item, which is not stored.
    ///     </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual NetworkPrefixKeyValuePair<TValue> GetOrAdd(NetworkPrefix address, Func<NetworkPrefix, TValue> valueFactoryFunc)
    {
        if (TryGetMatch(address, out var networkValuePair))
            return networkValuePair;

        networkValuePair = new NetworkPrefixKeyValuePair<TValue>(address, valueFactoryFunc.Invoke(address));
        TryAdd(networkValuePair);

        return networkValuePair;
    }

    /// <summary>
    ///     Adds a prefix and its value if the prefix is not already present.
    /// </summary>
    /// <param name="network">The prefix to add.</param>
    /// <param name="value">The value to associate with the prefix.</param>
    /// <returns><see langword="true" /> if the prefix was added; <see langword="false" /> if it already existed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual bool TryAdd(NetworkPrefix network, TValue? value)
    {
        return TryAdd(new NetworkPrefixKeyValuePair<TValue>(network, value));
    }

    /// <summary>Not implemented.</summary>
    /// <exception cref="NotImplementedException">Always thrown.</exception>
    IEnumerator<NetworkPrefixKeyValuePair<TValue>> IEnumerable<NetworkPrefixKeyValuePair<TValue>>.GetEnumerator()
    {
        throw new NotImplementedException();
    }

    /// <summary>Not implemented.</summary>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotImplementedException">Always thrown.</exception>
    public IEnumerator GetEnumerator()
    {
        throw new NotImplementedException();
    }
}