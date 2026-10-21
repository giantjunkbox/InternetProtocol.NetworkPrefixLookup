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

/// <summary>
///     A read-only set of IPv4 and IPv6 prefixes with faster lookups than
///     <see cref="IPPrefixLookup{TPrefix,TAdapter,TValue}" />, built by its <c>Freeze</c> method.
/// </summary>
/// <remarks>A frozen lookup never changes, so any number of threads may read it.</remarks>
/// <typeparam name="TPrefix">The prefix type.</typeparam>
/// <typeparam name="TAdapter">Reads the prefix type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> : IPPrefixLookupBase<TPrefix, TAdapter, TValue, FrozenForm>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>
{
    private readonly FrozenBitTrie<Bits32, TPrefix, TValue> _v4;
    private readonly FrozenBitTrie<Bits128, TPrefix, TValue> _v6;

    /// <summary>Freezes a read-write lookup. Derived classes call this from their own constructors.</summary>
    /// <param name="source">The lookup to copy.</param>
    /// <param name="ipv4">The layout of the IPv4 tables, or null for the default.</param>
    /// <param name="ipv6">The layout of the IPv6 tables, or null for the default.</param>
    protected internal FrozenIPPrefixLookup(IPPrefixLookup<TPrefix, TAdapter, TValue> source, FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6)
        : this((source ?? throw new ArgumentNullException(nameof(source))).FreezeIPv4(ipv4), source.FreezeIPv6(ipv6), source.Options)
    {
    }

    private FrozenIPPrefixLookup(FrozenBitTrie<Bits32, TPrefix, TValue> v4, FrozenBitTrie<Bits128, TPrefix, TValue> v6, IPLookupOptions options)
        : base(v4, v6, options)
    {
        _v4 = v4;
        _v6 = v6;
    }

    /// <summary>The memory held by both address families' tables and entries, in bytes.</summary>
    public long Bytes => _v4.Bytes + _v6.Bytes;
}
