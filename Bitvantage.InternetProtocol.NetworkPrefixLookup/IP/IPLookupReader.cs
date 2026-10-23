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
using System.Net;
using System.Runtime.CompilerServices;

namespace Bitvantage.InternetProtocol;

// The two forms of an IP lookup, as type arguments of IPLookupReader.
internal readonly struct ReadWriteForm
{
}

internal readonly struct FrozenForm
{
}

// The read-only members of IPPrefixLookup and FrozenIPPrefixLookup, written once; each class holds one of these and
// forwards to it. TForm, ReadWriteForm or FrozenForm, tells the JIT which sealed trie type holds each address family, so
// that each form's lookups call their tries directly and inline them. The members are AggressiveInlining, so that each
// one-line forwarder compiles to what a copy of the member would.
internal readonly struct IPLookupReader<TPrefix, TAdapter, TValue, TForm>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>
    where TForm : struct
{
    // a BitTrie in the read-write form, a FrozenBitTrie in the frozen form
    private readonly IReadOnlyBitTrie<Bits32, TPrefix, TValue> _ipv4;
    private readonly IReadOnlyBitTrie<Bits128, TPrefix, TValue> _ipv6;
    private readonly bool _map;

    public IPLookupReader(IReadOnlyBitTrie<Bits32, TPrefix, TValue> ipv4, IReadOnlyBitTrie<Bits128, TPrefix, TValue> ipv6, bool map)
    {
        _ipv4 = ipv4;
        _ipv6 = ipv6;
        _map = map;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(IPAddress address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? Match4(new Bits32(v4), out prefix, out value)
            : Match6(new Bits128(v6), out prefix, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(ReadOnlySpan<byte> address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? Match4(new Bits32(v4), out prefix, out value)
            : Match6(new Bits128(v6), out prefix, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatchIPv4(uint address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => Match4(new Bits32(address), out prefix, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatchIPv6(UInt128 address, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4)
            ? Match4(new Bits32(v4), out prefix, out value)
            : Match6(new Bits128(address), out prefix, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsMatch(IPAddress address) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? Has4(new Bits32(v4)) : Has6(new Bits128(v6));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsMatch(ReadOnlySpan<byte> address) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? Has4(new Bits32(v4)) : Has6(new Bits128(v6));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsMatchIPv4(uint address) => Has4(new Bits32(address));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsMatchIPv6(UInt128 address) =>
        IPAddressBits.IsIPv4(address, _map, out var v4) ? Has4(new Bits32(v4)) : Has6(new Bits128(address));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatchValue(IPAddress address, [MaybeNullWhen(false)] out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? MatchValue4(new Bits32(v4), out value)
            : MatchValue6(new Bits128(v6), out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatchValue(ReadOnlySpan<byte> address, [MaybeNullWhen(false)] out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? MatchValue4(new Bits32(v4), out value)
            : MatchValue6(new Bits128(v6), out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatchValueIPv4(uint address, [MaybeNullWhen(false)] out TValue value) => MatchValue4(new Bits32(address), out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatchValueIPv6(UInt128 address, [MaybeNullWhen(false)] out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4)
            ? MatchValue4(new Bits32(v4), out value)
            : MatchValue6(new Bits128(address), out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(TPrefix network, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) =>
        IsIPv4(network, out var v4, out var v6, out var length)
            ? MatchPrefix4(in v4, length, out prefix, out value)
            : MatchPrefix6(in v6, length, out prefix, out value);

    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(TPrefix network) =>
        IsIPv4(network, out var v4, out var v6, out var length) ? PrefixMatchList4(in v4, length) : PrefixMatchList6(in v6, length);

    public int GetMatches(TPrefix network, Span<PrefixEntry<TPrefix, TValue>> destination) =>
        IsIPv4(network, out var v4, out var v6, out var length) ? PrefixMatches4(in v4, length, destination) : PrefixMatches6(in v6, length, destination);

    public IEnumerable<PrefixEntry<TPrefix, TValue>> GetSubnets(TPrefix network)
    {
        if (IsIPv4(network, out var v4, out var v6, out var length))
            return Subnets4(in v4, length);

        // a network that contains every mapped address contains, for the mapped lookups, every IPv4 prefix
        var ipv6 = Subnets6(in v6, length);
        return _map && IPAddressBits.ContainsIPv4Mapped(v6.Value, length) ? _ipv4.Concat(ipv6) : ipv6;
    }

    public bool Overlaps(TPrefix network)
    {
        if (IsIPv4(network, out var v4, out var v6, out var length))
            return Overlaps4(in v4, length);
        return Overlaps6(in v6, length) || (_map && _ipv4.Count > 0 && IPAddressBits.ContainsIPv4Mapped(v6.Value, length));
    }

    // Sorts a network into a family for the queries about coverage, which follow the address lookups: with mapping on,
    // an IPv6 network inside ::ffff:0:0/96 is the IPv4 network it holds, so ::ffff:10.1.0.0/112 is 10.1.0.0/16.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsIPv4(TPrefix network, out Bits32 v4, out Bits128 v6, out int length)
    {
        IPAddressBits.ThrowIfNull(network);
        length = TAdapter.GetLength(network);
        if (TAdapter.IsIPv4(network))
        {
            v4 = new Bits32(TAdapter.GetIPv4Bits(network));
            v6 = default;
            return true;
        }

        var bits = TAdapter.GetIPv6Bits(network);
        v6 = new Bits128(bits);
        if (_map && length >= 96 && IPAddressBits.IsIPv4Mapped(bits))
        {
            v4 = new Bits32((uint)bits);
            length -= 96;
            return true;
        }

        v4 = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(TPrefix prefix, [MaybeNullWhen(false)] out TValue value)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? Value4(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix), out value)
            : Value6(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix), out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsPrefix(TPrefix prefix)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? Contains4(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix))
            : Contains6(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(IPAddress address) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? MatchList4(new Bits32(v4)) : MatchList6(new Bits128(v6));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetMatches(IPAddress address, Span<PrefixEntry<TPrefix, TValue>> destination) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6) ? Matches4(new Bits32(v4), destination) : Matches6(new Bits128(v6), destination);

    public int Count => _ipv4.Count + _ipv6.Count;

    public IEnumerator<PrefixEntry<TPrefix, TValue>> GetEnumerator() => _ipv4.Concat(_ipv6).GetEnumerator();

    // ---- calls into the tries --------------------------------------------------------------------------------------

    // Each casts the trie to its form's sealed type. The JIT resolves the typeof test when it compiles the form, so only
    // a direct call remains and the trie's lookup inlines. A call through the interface would do neither.
    private static bool ReadWrite
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => typeof(TForm) == typeof(ReadWriteForm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Match4(in Bits32 key, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).TryGetMatch(in key, out prefix, out value)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).TryGetMatch(in key, out prefix, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Match6(in Bits128 key, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).TryGetMatch(in key, out prefix, out value)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).TryGetMatch(in key, out prefix, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Has4(in Bits32 key) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).ContainsMatch(in key)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).ContainsMatch(in key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Has6(in Bits128 key) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).ContainsMatch(in key)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).ContainsMatch(in key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchValue4(in Bits32 key, [MaybeNullWhen(false)] out TValue value) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).TryGetMatchValue(in key, out value)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).TryGetMatchValue(in key, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchValue6(in Bits128 key, [MaybeNullWhen(false)] out TValue value) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).TryGetMatchValue(in key, out value)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).TryGetMatchValue(in key, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchPrefix4(in Bits32 bits, int length, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).TryGetMatch(in bits, length, out prefix, out value)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).TryGetMatch(in bits, length, out prefix, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchPrefix6(in Bits128 bits, int length, [MaybeNullWhen(false)] out TPrefix prefix, [MaybeNullWhen(false)] out TValue value) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).TryGetMatch(in bits, length, out prefix, out value)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).TryGetMatch(in bits, length, out prefix, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Value4(in Bits32 bits, int length, [MaybeNullWhen(false)] out TValue value) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).TryGetValue(in bits, length, out value)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).TryGetValue(in bits, length, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Value6(in Bits128 bits, int length, [MaybeNullWhen(false)] out TValue value) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).TryGetValue(in bits, length, out value)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).TryGetValue(in bits, length, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Contains4(in Bits32 bits, int length) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).ContainsPrefix(in bits, length)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).ContainsPrefix(in bits, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Contains6(in Bits128 bits, int length) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).ContainsPrefix(in bits, length)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).ContainsPrefix(in bits, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IReadOnlyList<PrefixEntry<TPrefix, TValue>> MatchList4(in Bits32 key) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetMatches(in key)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetMatches(in key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IReadOnlyList<PrefixEntry<TPrefix, TValue>> MatchList6(in Bits128 key) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetMatches(in key)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetMatches(in key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Matches4(in Bits32 key, Span<PrefixEntry<TPrefix, TValue>> destination) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetMatches(in key, destination)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetMatches(in key, destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Matches6(in Bits128 key, Span<PrefixEntry<TPrefix, TValue>> destination) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetMatches(in key, destination)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetMatches(in key, destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IReadOnlyList<PrefixEntry<TPrefix, TValue>> PrefixMatchList4(in Bits32 bits, int length) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetMatches(in bits, length)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetMatches(in bits, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IReadOnlyList<PrefixEntry<TPrefix, TValue>> PrefixMatchList6(in Bits128 bits, int length) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetMatches(in bits, length)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetMatches(in bits, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PrefixMatches4(in Bits32 bits, int length, Span<PrefixEntry<TPrefix, TValue>> destination) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetMatches(in bits, length, destination)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetMatches(in bits, length, destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PrefixMatches6(in Bits128 bits, int length, Span<PrefixEntry<TPrefix, TValue>> destination) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetMatches(in bits, length, destination)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetMatches(in bits, length, destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IEnumerable<PrefixEntry<TPrefix, TValue>> Subnets4(in Bits32 bits, int length) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetSubnets(in bits, length)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).GetSubnets(in bits, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IEnumerable<PrefixEntry<TPrefix, TValue>> Subnets6(in Bits128 bits, int length) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetSubnets(in bits, length)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).GetSubnets(in bits, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Overlaps4(in Bits32 bits, int length) => ReadWrite
        ? Unsafe.As<BitTrie<Bits32, TPrefix, TValue>>(_ipv4).Overlaps(in bits, length)
        : Unsafe.As<FrozenBitTrie<Bits32, TPrefix, TValue>>(_ipv4).Overlaps(in bits, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Overlaps6(in Bits128 bits, int length) => ReadWrite
        ? Unsafe.As<BitTrie<Bits128, TPrefix, TValue>>(_ipv6).Overlaps(in bits, length)
        : Unsafe.As<FrozenBitTrie<Bits128, TPrefix, TValue>>(_ipv6).Overlaps(in bits, length);
}
