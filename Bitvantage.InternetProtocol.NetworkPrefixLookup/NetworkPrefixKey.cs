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

// TODO: should these be classes?
/// <summary>
///     An item stored in a <see cref="NetworkPrefixLookupBase{TValue}" />, identified by its network prefix.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="NetworkPrefixLookup" /> stores these directly. The trie also uses plain instances of this record
///         as the placeholder key of routing nodes, which hold no user value.
///     </para>
///     <para>
///         Derive from this record to store extra data with each prefix, as
///         <see cref="NetworkPrefixKeyValuePair{TValue}" /> does. Being a record, it compares by value.
///     </para>
/// </remarks>
public record NetworkPrefixKey()
{
    /// <summary>
    ///     The network prefix that identifies this item.
    /// </summary>
    /// <remarks>
    ///     The prefix must not change while the item is stored. The trie copies the prefix bits into its node when
    ///     the item is added and would not see a later change.
    /// </remarks>
    public NetworkPrefix Prefix { get; internal set; }

    // should there be an empty network type?
    /// <summary>
    ///     Creates a key for <paramref name="prefix" />.
    /// </summary>
    /// <param name="prefix">The network prefix.</param>
    public NetworkPrefixKey(NetworkPrefix prefix) : this()
    {
        Prefix = prefix;
    }
}

/// <summary>
///     A network prefix paired with a value. This is the item type of <see cref="NetworkPrefixLookup{TValue}" />.
/// </summary>
/// <typeparam name="TValue">The type of the associated value.</typeparam>
public record NetworkPrefixKeyValuePair<TValue>() : NetworkPrefixKey
{
    /// <summary>
    ///     The value associated with <see cref="NetworkPrefixKey.Prefix" />.
    /// </summary>
    public TValue? Value { get; }

    // should there be an empty network type?
    /// <summary>
    ///     Creates a pair from a prefix and a value.
    /// </summary>
    /// <param name="prefix">The network prefix.</param>
    /// <param name="value">The associated value.</param>
    public NetworkPrefixKeyValuePair(NetworkPrefix prefix, TValue? value) : this()
    {
        Prefix = prefix;
        Value = value;
    }
}
