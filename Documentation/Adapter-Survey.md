# Survey: IP Prefix Types on NuGet

The redesigned core works on bits, and each network type plugs in through a small adapter. This survey looks for
popular NuGet packages that define their own IP network, prefix or range type, to decide which deserve an adapter
package. Download counts are NuGet totals on 2026-10-19.

## Method

- Searched NuGet for cidr, ipnetwork, ip network, subnet, ip range, ip address range, ip prefix, netmask, network
  prefix, ipv6 subnet, ip filter, longest prefix match and related terms, then looked up likely packages by id.
- For each candidate, read the public API from the source or from the XML documentation shipped in the package.
- Checked recent version downloads and release dates, to tell packages in use today from old totals.

## Candidates

| Package | Downloads | Type | Status | Verdict |
| --- | --- | --- | --- | --- |
| Npgsql | 1,015 M | `NpgsqlCidr`, `NpgsqlInet` | Active; 10.0.x has 100 M+ downloads | No package: PostgreSQL `cidr` maps to `System.Net.IPNetwork`, and `NpgsqlCidr` is obsolete |
| Microsoft.AspNetCore.HttpOverrides, and the ASP.NET Core shared framework | 206 M | `Microsoft.AspNetCore.HttpOverrides.IPNetwork` | Obsolete in favor of `System.Net.IPNetwork` (ASPDEPR005) | No package |
| MaxMind.Db | 36.8 M | `MaxMind.Db.Network` | Active; 5.2.0 released 2026-09 | **Add**: adapter plus a loader for MaxMind databases |
| IPNetwork2 | 35.6 M | `System.Net.IPNetwork2` | Active; 4.3.0 targets .NET 10 | **Add**: adapter |
| MaxMind.GeoIP2 | 32.9 M | Uses `MaxMind.Db.Network` in responses | Active; 6.1.0 released 2026-07 | Covered by the MaxMind.Db package |
| IPAddressRange | 30.8 M | `NetTools.IPAddressRange` | Active; each 6.x release has 2 to 8 M downloads, 6.3.0 targets .NET 10 | **Add**: range adapter |
| IPNetwork | 1.1 M | `LukeSkywalker.IPNetwork`, the predecessor of IPNetwork2 | Last release 2015 | No package |
| IpMatcher | 0.56 M | A matcher, not a network type | Active, small | No package; a possible competitor to benchmark |
| IpSet | 0.19 M | Range and set helpers | Last release 2019 | No package |
| TakeIo.NetworkAddress | 0.13 M | IP and mask helpers | Last release 2011 | No package |
| Arcus | 0.09 M | `Subnet` | Active, small | No package |
| IPRangeLookup | 0.07 M | None: a lookup library | Last release 2021 | No package; a competitor to benchmark |
| Bart.NET | 0.01 M | None: a routing table, ported from Go's BART | Active, small | No package; a competitor to benchmark |
| Bitvantage.NetworkAddressing | 3,441 | A network class, a variable-stride lookup, MAC addresses and an OUI lookup | Your own package | Decide whether it merges into the redesign |

Everything else found either has no prefix type of its own, such as geolocation clients, firewalls and rate limiters
that take strings, or has fewer than 70,000 downloads.

## Recommended packages

### IPNetwork2

`System.Net.IPNetwork2` is a class with `Network` (an `IPAddress`), `Cidr` (a `byte`) and `AddressFamily`. Version 3.0
renamed the class from `System.Net.IPNetwork`, which clashes with the built-in type, so only versions 3 and later work
on .NET 10. The adapter is four lines, like the `IPNetwork` one:

```csharp
public struct IPNetwork2Adapter : IIPPrefixAdapter<IPNetwork2>
{
    public static bool IsIPv4(IPNetwork2 prefix) => prefix.AddressFamily == AddressFamily.InterNetwork;
    public static uint GetIPv4Bits(IPNetwork2 prefix) => IPBits.V4(prefix.Network);
    public static UInt128 GetIPv6Bits(IPNetwork2 prefix) => IPBits.V6(prefix.Network);
    public static int GetLength(IPNetwork2 prefix) => prefix.Cidr;
}
```

IPNetwork2 keeps its addresses as `BigInteger` values, so `Network` probably builds a new `IPAddress` on each call.
That only affects adding and prefix queries; lookups by address never call the adapter.

### IPAddressRange

`NetTools.IPAddressRange` holds `Begin` and `End` addresses. A range can be a CIDR block, such as `192.168.0.0/24`, or
any span, such as `192.168.0.10-192.168.0.20`. `GetPrefixLength()` throws for a range that is not a CIDR block.

A range adapter therefore supplies the first and last address, and a range lookup in the core splits each range into
the fewest prefixes that cover it exactly: at most 62 for IPv4 and 254 for IPv6, and one for a CIDR block. Every piece
stores the original range and value, so lookups return the caller's range. The splitting lives in the core, so it also
serves other range types, such as the ranges that IPNetwork2's `ParseRange` produces. The interface is in
[Interface.md](Interface.md#ranges).

`Begin` and `End` have public setters. The lookup reads them once, when the range is added; changing a stored range
afterwards is not supported.

### MaxMind.Db

`MaxMind.Db.Network` is a class with `NetworkAddress` and `PrefixLength`, and GeoIP2 responses use it to say which
network an answer covers. The adapter is as small as the others.

The larger opportunity is a loader. `Reader.FindAll<T>()` lists every network in a MaxMind database with its decoded
record, so a whole GeoIP database can be loaded into a frozen lookup. MaxMind's reader walks its search tree one bit at
a time and decodes the record on every lookup, while a frozen lookup returns an already decoded record after reading a
few table entries. That should be much faster, but it is not measured, and it costs memory for every decoded record.
Two details need care:

- **Aliases.** In a database with an IPv6 tree, the IPv4 networks are also reachable under `::ffff:0:0/96` and
  `2002::/16`, and `FindAll` returns each of those copies as an IPv6 network. Keeping them roughly doubles the IPv4
  part of the table. Skipping them is smaller, but then IPv4-mapped addresses must be looked up as IPv4, which is an
  open question in [Interface.md](Interface.md#open-questions).
- **Shared records.** `FindAll` caches decoded records by their position in the file, up to its `cacheSize`
  argument, so many networks share one object. The loader should pass a cache large enough for the database.

Benchmark the loader against MaxMind's own reader on a GeoLite2 database before publishing it.

## No package needed

- **`System.Net.IPNetwork`** is built into .NET and covered by the core package.
- **Npgsql.** PostgreSQL `cidr` columns map to `System.Net.IPNetwork`, so they work with the core package. `inet`
  columns map to `NpgsqlInet`, an address plus a netmask that may have host bits set. Callers can convert with
  `new IPNetwork(inet.Address, inet.Netmask)` after clearing the host bits, or the core can take the bits and length
  directly. An adapter can follow if users ask.
- **ASP.NET Core's `IPNetwork`** is obsolete and points to `System.Net.IPNetwork`.

## Competitors to benchmark

IpMatcher, IPRangeLookup and Bart.NET solve the same problem. Bart.NET, a port of an early version of the Go BART
routing table, is compared in detail in [Bart-Comparison.md](Bart-Comparison.md), together with the current BART
design.

## Your other package

`Bitvantage.NetworkAddressing` has its own network class and a variable-stride prefix lookup, which overlaps with this
redesign, plus MAC addresses and an OUI lookup. The OUI lookup is the non-IP example in [Interface.md](Interface.md),
a 48-bit key in a `Bits64`, so it could move onto the generic core. Whether to merge, adapt or retire it is your call.
