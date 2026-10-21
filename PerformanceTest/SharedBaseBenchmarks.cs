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
using BenchmarkDotNet.Attributes;
using Bitvantage.InternetProtocol;

namespace PerformanceTest;

/// <summary>
///     TEMP: the IP lookups on their shared base against the copies they replaced, in one session, old and new of each
///     case back to back. Covers a value-type prefix (IPNetwork) and a reference-type one (NetworkPrefix), whose generic
///     code the runtime shares. Removed once measured. Set ROUTEVIEWS_DIR.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 2, warmupCount: 5, iterationCount: 15)]
public class SharedBaseBenchmarks
{
    private IPPrefixLookup<IPNetwork, IPNetworkAdapter, int> _new = null!;
    private FrozenIPPrefixLookup<IPNetwork, IPNetworkAdapter, int> _newFrozen = null!;
    private OldIPPrefixLookup<IPNetwork, IPNetworkAdapter, int> _old = null!;
    private OldFrozenIPPrefixLookup<IPNetwork, IPNetworkAdapter, int> _oldFrozen = null!;
    private IPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, int> _newRef = null!;
    private FrozenIPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, int> _newRefFrozen = null!;
    private OldIPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, int> _oldRef = null!;
    private OldFrozenIPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, int> _oldRefFrozen = null!;
    private IPAddress[] _addresses = null!;

    [GlobalSetup]
    public void Setup()
    {
        var v4 = RouteViews.IPv4;
        var v6 = RouteViews.IPv6;
        var items = v4.Networks.Zip(v4.Asns, KeyValuePair.Create).Concat(v6.Networks.Zip(v6.Asns, KeyValuePair.Create)).DistinctBy(p => p.Key).ToList();
        _new = new IPPrefixLookup<IPNetwork, IPNetworkAdapter, int>(items);
        _old = new OldIPPrefixLookup<IPNetwork, IPNetworkAdapter, int>(items);
        _newFrozen = _new.Freeze();
        _oldFrozen = _old.Freeze();

        var real = ComparisonData.Get("RealIPv4");
        var refItems = real.Prefixes.Zip(real.Values, KeyValuePair.Create).ToList();
        _newRef = new IPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, int>(refItems);
        _oldRef = new OldIPPrefixLookup<NetworkPrefix, NetworkPrefixAdapter, int>(refItems);
        _newRefFrozen = _newRef.Freeze();
        _oldRefFrozen = _oldRef.Freeze();
        _addresses = real.Addresses;
    }

    [Benchmark]
    public long Old_IPv4_ReadWrite()
    {
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += _old.TryGetMatchIPv4(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark]
    public long New_IPv4_ReadWrite()
    {
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += _new.TryGetMatchIPv4(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark]
    public long Old_IPv4_Frozen()
    {
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += _oldFrozen.TryGetMatchIPv4(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark]
    public long New_IPv4_Frozen()
    {
        long sum = 0;
        foreach (var key in RouteViews.IPv4.IPv4Keys)
            sum += _newFrozen.TryGetMatchIPv4(key, out var network, out var asn) ? network.PrefixLength + asn : -1;
        return sum;
    }

    [Benchmark]
    public long Old_IPv6_ReadWrite()
    {
        long sum = 0;
        foreach (var key in RouteViews.IPv6.Keys)
            sum += _old.TryGetMatchValueIPv6(key, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark]
    public long New_IPv6_ReadWrite()
    {
        long sum = 0;
        foreach (var key in RouteViews.IPv6.Keys)
            sum += _new.TryGetMatchValueIPv6(key, out var asn) ? asn : -1;
        return sum;
    }

    [Benchmark]
    public long Old_Reference_Addresses_ReadWrite()
    {
        long sum = 0;
        foreach (var address in _addresses)
            sum += _oldRef.TryGetMatch(address, out var prefix, out var value) ? prefix.Length + value : -1;
        return sum;
    }

    [Benchmark]
    public long New_Reference_Addresses_ReadWrite()
    {
        long sum = 0;
        foreach (var address in _addresses)
            sum += _newRef.TryGetMatch(address, out var prefix, out var value) ? prefix.Length + value : -1;
        return sum;
    }

    [Benchmark]
    public long Old_Reference_Addresses_Frozen()
    {
        long sum = 0;
        foreach (var address in _addresses)
            sum += _oldRefFrozen.TryGetMatch(address, out var prefix, out var value) ? prefix.Length + value : -1;
        return sum;
    }

    [Benchmark]
    public long New_Reference_Addresses_Frozen()
    {
        long sum = 0;
        foreach (var address in _addresses)
            sum += _newRefFrozen.TryGetMatch(address, out var prefix, out var value) ? prefix.Length + value : -1;
        return sum;
    }
}
