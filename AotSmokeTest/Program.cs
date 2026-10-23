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
using Bitvantage.InternetProtocol.NetworkPrefixLookup;

// Exercises each package once, published with native AOT: every check must hold, or the process exits with 1. A
// trimming or AOT warning in the libraries already fails the publish, since the project treats warnings as errors.
var failures = 0;
void Check(bool condition, string what)
{
    if (condition) return;
    Console.Error.WriteLine($"FAILED: {what}");
    failures++;
}

// the core package: writes, a batch, every kind of query, freezing
var routes = new IPNetworkLookup<int> { { IPNetwork.Parse("10.0.0.0/8"), 1 }, { IPNetwork.Parse("2001:db8::/32"), 2 } };
using (var batch = routes.BeginBatch())
{
    routes.Add(IPNetwork.Parse("10.1.0.0/16"), 3);
    batch.Commit();
}

foreach (IReadOnlyIPPrefixLookup<IPNetwork, int> form in new IReadOnlyIPPrefixLookup<IPNetwork, int>[] { routes, routes.Freeze() })
{
    Check(form.TryGetMatchValue(IPAddress.Parse("10.1.2.3"), out var value) && value == 3, "TryGetMatchValue");
    Check(form.ContainsMatchIPv6(IPAddressBits.ToIPv6Bits(IPAddress.Parse("2001:db8::1"))), "ContainsMatchIPv6");
    Check(form.GetMatches(IPNetwork.Parse("10.1.2.0/24")).Count == 2, "GetMatches");
    Check(form.GetSubnets(IPNetwork.Parse("10.0.0.0/8")).Count() == 2, "GetSubnets");
    Check(form.Overlaps(IPNetwork.Parse("10.0.0.0/7")) && !form.Overlaps(IPNetwork.Parse("11.0.0.0/8")), "Overlaps");
    Check(form.Count == 3 && form.IPv4Count == 2, "Count");
}

// the core trie on its own
var trie = new BitTrie<Bits32, string, int>();
trie.Add(new Bits32(0x0A000000), 8, "10/8", 1);
Check(trie.Freeze().TryGetMatch(new Bits32(0x0A010203), out var prefix, out _) && prefix == "10/8", "BitTrie");

// the IPNetwork2 package
var ipNetwork2 = new IPNetwork2Lookup<int> { { System.Net.IPNetwork2.Parse("192.0.2.0/24"), 4 } };
Check(ipNetwork2.Freeze().TryGetMatchValue(IPAddress.Parse("192.0.2.9"), out var asn) && asn == 4, "IPNetwork2Lookup");

// the MaxMind package: the loader decodes records with MaxMind.Db, whose own AOT support decides whether this works
var path = Path.Combine(AppContext.BaseDirectory, "GeoIP2-Country-Test.mmdb");
var countries = MaxMindDatabase.Load<Dictionary<string, object>>(path);
Check(countries.Count > 0 && countries.Any(e => e.Prefix.NetworkAddress.AddressFamily == AddressFamily.InterNetworkV6), "MaxMindDatabase.Load");

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} checks failed.");
return failures == 0 ? 0 : 1;
