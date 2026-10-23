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

namespace Bitvantage.InternetProtocol.NetworkPrefixLookup;

/// <summary>
///     Teaches <see cref="IPPrefixLookup{TPrefix,TAdapter,TValue}" /> to read a prefix type: its address family, network
///     bits and length. Implement it on an empty struct, so that the JIT compiles each lookup for your type.
/// </summary>
/// <remarks>
///     The prefix is read once, when it is added or looked up; later changes to a stored prefix object are not seen. Bits
///     after the length are ignored. <see cref="IPAddressBits" /> converts an <see cref="IPAddress" /> to bits.
/// </remarks>
/// <typeparam name="TPrefix">The prefix type.</typeparam>
public interface IIPPrefixAdapter<in TPrefix>
{
    /// <summary>True for an IPv4 prefix, false for an IPv6 prefix.</summary>
    /// <param name="prefix">The prefix.</param>
    static abstract bool IsIPv4(TPrefix prefix);

    /// <summary>The network address of an IPv4 prefix, with the address's first bit in the most significant bit.</summary>
    /// <param name="prefix">An IPv4 prefix.</param>
    static abstract uint GetIPv4Bits(TPrefix prefix);

    /// <summary>The network address of an IPv6 prefix, with the address's first bit in the most significant bit.</summary>
    /// <param name="prefix">An IPv6 prefix.</param>
    static abstract UInt128 GetIPv6Bits(TPrefix prefix);

    /// <summary>The prefix length: 0 to 32 for IPv4, 0 to 128 for IPv6.</summary>
    /// <param name="prefix">The prefix.</param>
    static abstract int GetLength(TPrefix prefix);
}
