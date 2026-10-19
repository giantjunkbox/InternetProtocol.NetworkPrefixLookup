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
using System.Net.Sockets;
using MaxMind.Db;

namespace Bitvantage.InternetProtocol;

/// <summary>Options for <see cref="MaxMindDatabase.Load{T}(Reader,MaxMindLoadOptions?)" />.</summary>
public sealed record MaxMindLoadOptions
{
    /// <summary>
    ///     Loads the networks under <c>::ffff:0:0/96</c>, <c>2001::/32</c> and <c>2002::/16</c> too. MaxMind's IPv6
    ///     databases point these three networks at the IPv4 networks, so loading them stores every IPv4 network three more
    ///     times. The default is false: they are skipped, and with
    ///     <see cref="IPLookupOptions.MapIPv4MappedAddresses" /> on, an IPv4-mapped address still finds its IPv4 network.
    ///     Teredo and 6to4 addresses then find only what covers them outside the aliases.
    /// </summary>
    public bool IncludeAliasedNetworks { get; init; }

    /// <summary>
    ///     The number of decoded records the reader keeps while loading. Networks that share a record share one decoded
    ///     object, but only while it stays in this cache. The default is 16,384, as in <see cref="Reader.FindAll{T}" />.
    /// </summary>
    public int CacheSize { get; init; } = 16384;

    /// <summary>Values to inject while decoding records, as in <see cref="Reader.FindAll{T}" />.</summary>
    public InjectableValues? Injectables { get; init; }

    /// <summary>Options for the lookup that is returned.</summary>
    public IPLookupOptions? LookupOptions { get; init; }
}

/// <summary>Loads MaxMind DB files, such as GeoLite2 and GeoIP2 databases, into lookups.</summary>
public static class MaxMindDatabase
{
    private static readonly (UInt128 Bits, int Length)[] Aliases =
    {
        (IPAddressBits.ToIPv6Bits(IPAddress.Parse("::ffff:0:0")), 96),
        (IPAddressBits.ToIPv6Bits(IPAddress.Parse("2001::")), 32),
        (IPAddressBits.ToIPv6Bits(IPAddress.Parse("2002::")), 16)
    };

    /// <summary>
    ///     Reads every network in a database into a read-write lookup, loaded as one batch. Freeze the result for the
    ///     fastest lookups.
    /// </summary>
    /// <param name="reader">An open reader.</param>
    /// <param name="options">Load options; by default the aliases of the IPv4 networks are skipped.</param>
    /// <typeparam name="T">The record type, as for <see cref="Reader.Find{T}(IPAddress,InjectableValues?)" />.</typeparam>
    /// <remarks>
    ///     Records come from <see cref="Reader.FindAll{T}" />, which reuses decoded records from a cache, so networks with
    ///     the same record often share one object. Do not change the records.
    /// </remarks>
    public static MaxMindNetworkLookup<T> Load<T>(Reader reader, MaxMindLoadOptions? options = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new MaxMindLoadOptions();
        var lookup = new MaxMindNetworkLookup<T>(options.LookupOptions);
        using var batch = lookup.BeginBatch();
        foreach (var node in reader.FindAll<T>(options.Injectables, options.CacheSize))
        {
            var network = ToNetwork(node.Start, node.PrefixLength);
            if (!options.IncludeAliasedNetworks && IsAliased(network))
                continue;
            lookup.TryAdd(network, node.Data);
        }

        batch.Commit();
        return lookup;
    }

    /// <summary>Opens a database file and reads every network into a read-write lookup. See <see cref="Load{T}(Reader,MaxMindLoadOptions?)" />.</summary>
    /// <param name="path">The path of a .mmdb file.</param>
    /// <param name="options">Load options; by default the aliases of the IPv4 networks are skipped.</param>
    /// <typeparam name="T">The record type.</typeparam>
    public static MaxMindNetworkLookup<T> Load<T>(string path, MaxMindLoadOptions? options = null) where T : class
    {
        using var reader = new Reader(path, FileAccessMode.Memory);
        return Load<T>(reader, options);
    }

    // FindAll reports a network in the IPv4 part of an IPv6 tree as IPv4, with the prefix length less 96. A network above
    // that part, such as ::/64, would come out with a negative length, so it goes back to IPv6.
    private static Network ToNetwork(IPAddress start, int length) =>
        length < 0 && start.AddressFamily == AddressFamily.InterNetwork ? new Network(IPAddress.IPv6Any, length + 96) : new Network(start, length);

    private static bool IsAliased(Network network)
    {
        if (network.NetworkAddress.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        var bits = IPAddressBits.ToIPv6Bits(network.NetworkAddress);
        foreach (var (alias, length) in Aliases)
            if (network.PrefixLength >= length && (bits >> (128 - length)) == (alias >> (128 - length)))
                return true;
        return false;
    }
}
