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

using System.Diagnostics.CodeAnalysis;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     Read-only access to a set of prefixes over keys of type <typeparamref name="TKey" />, each with a prefix object and
///     a value. Implemented by the read-write <see cref="BitTrie{TKey,TPrefix,TValue}" /> and the read-only
///     <see cref="FrozenBitTrie{TKey,TPrefix,TValue}" />.
/// </summary>
/// <remarks>
///     <para>
///         A prefix is a key plus a length: its first <c>length</c> bits. Bits after the length are ignored. Lengths
///         outside 0 to <c>TKey.Width</c> throw <see cref="ArgumentOutOfRangeException" />.
///     </para>
///     <para>
///         Methods named <c>Match</c> find the longest stored prefix that contains a key or prefix. <c>TryGetValue</c>
///         and <c>ContainsPrefix</c> match a prefix exactly, as <see cref="Dictionary{TKey,TValue}" /> does.
///         Enumeration returns entries in address order, a shorter prefix before a longer one at the same address.
///     </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TPrefix">The type of the prefix object stored with each prefix.</typeparam>
/// <typeparam name="TValue">The type of the value stored with each prefix.</typeparam>
public interface IReadOnlyBitTrie<TKey, TPrefix, TValue> : IReadOnlyCollection<PrefixEntry<TPrefix, TValue>>
    where TKey : struct, IBitKey<TKey>
{
    /// <summary>Finds the longest stored prefix that contains a full-width key, such as an address.</summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="prefix">The prefix object of the match.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the key.</returns>
    bool TryGetMatch(in TKey key, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the value of the longest stored prefix that contains a full-width key.</summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the key.</returns>
    bool TryGetMatchValue(in TKey key, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Finds the longest stored prefix that contains the given prefix, which may be the prefix itself.</summary>
    /// <param name="bits">The prefix bits.</param>
    /// <param name="length">The prefix length.</param>
    /// <param name="prefix">The prefix object of the match.</param>
    /// <param name="value">The value of the match.</param>
    /// <returns>True when a stored prefix contains the given prefix.</returns>
    bool TryGetMatch(in TKey bits, int length, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Gets the value of exactly this prefix.</summary>
    /// <param name="bits">The prefix bits.</param>
    /// <param name="length">The prefix length.</param>
    /// <param name="value">The value, when the prefix is stored.</param>
    /// <returns>True when the prefix is stored.</returns>
    bool TryGetValue(in TKey bits, int length, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Gets the prefix object and value of exactly this prefix.</summary>
    /// <param name="bits">The prefix bits.</param>
    /// <param name="length">The prefix length.</param>
    /// <param name="prefix">The prefix object, when the prefix is stored.</param>
    /// <param name="value">The value, when the prefix is stored.</param>
    /// <returns>True when the prefix is stored.</returns>
    bool TryGetValue(in TKey bits, int length, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value);

    /// <summary>True when exactly this prefix is stored.</summary>
    /// <param name="bits">The prefix bits.</param>
    /// <param name="length">The prefix length.</param>
    bool ContainsPrefix(in TKey bits, int length);

    /// <summary>Every stored prefix that contains a full-width key, longest first.</summary>
    /// <param name="key">The key to look up.</param>
    IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(in TKey key);

    /// <summary>Writes every stored prefix that contains a full-width key into a span, longest first.</summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="destination">The span to fill. <c>TKey.Width + 1</c> entries always suffice.</param>
    /// <returns>The number of entries written.</returns>
    /// <exception cref="ArgumentException">The span is too small for the matches.</exception>
    int GetMatches(in TKey key, Span<PrefixEntry<TPrefix, TValue>> destination);
}
