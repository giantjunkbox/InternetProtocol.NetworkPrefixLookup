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

using System.Net.Sockets;

namespace Bitvantage.InternetProtocol;

// A range's family and ends as integers. Sorts IPv4 before IPv6, then by first address, then the larger range first, so
// ranges enumerate in address order like prefixes do.
internal readonly record struct RangeKey(bool IsIPv6, UInt128 First, UInt128 Last) : IComparable<RangeKey>
{
    public UInt128 Span => Last - First;

    public bool Contains(in RangeKey other) => IsIPv6 == other.IsIPv6 && First <= other.First && Last >= other.Last;

    public int CompareTo(RangeKey other)
    {
        var c = IsIPv6.CompareTo(other.IsIPv6);
        if (c == 0) c = First.CompareTo(other.First);
        if (c == 0) c = other.Last.CompareTo(Last);
        return c;
    }

    // smaller ranges first, then by first address: the order of the claims on a piece
    public static int BySize(RangeKey a, RangeKey b)
    {
        var c = a.Span.CompareTo(b.Span);
        return c != 0 ? c : a.First.CompareTo(b.First);
    }
}

// One range, its value, and its ends as read when it was added: a stored range object is never read again, so a caller
// that changes one afterwards cannot make the lookup inconsistent.
internal readonly record struct RangeClaim<TRange, TValue>(TRange Range, TValue Value, RangeKey Key);

// What a piece stores besides the winning range, which is the piece's prefix object: the winner's value and ends, and the
// other ranges that split into exactly this piece, smallest first. Others is null for nearly every piece.
internal readonly record struct RangePiece<TRange, TValue>(TValue Value, RangeKey Key, RangeClaim<TRange, TValue>[]? Others)
{
    // a piece whose claims are already smallest first
    public static RangePiece<TRange, TValue> From(List<RangeClaim<TRange, TValue>> claims) =>
        new(claims[0].Value, claims[0].Key, claims.Count > 1 ? claims.Skip(1).ToArray() : null);
}

// The parts of a range lookup that both the read-write and the frozen form use.
internal static class RangePieces<TRange, TAdapter, TValue> where TAdapter : struct, IIPRangeAdapter<TRange>
{
    public static RangeKey KeyOf(TRange range)
    {
        IPAddressBits.ThrowIfNull(range);
        var first = TAdapter.GetFirst(range) ?? throw new ArgumentException("The range has no first address.", nameof(range));
        var last = TAdapter.GetLast(range) ?? throw new ArgumentException("The range has no last address.", nameof(range));
        if (first.AddressFamily != last.AddressFamily)
            throw new ArgumentException($"The range {range} mixes address families.", nameof(range));

        RangeKey key = first.AddressFamily switch
        {
            AddressFamily.InterNetwork => new(false, IPAddressBits.ToIPv4Bits(first), IPAddressBits.ToIPv4Bits(last)),
            AddressFamily.InterNetworkV6 => new(true, IPAddressBits.ToIPv6Bits(first), IPAddressBits.ToIPv6Bits(last)),
            _ => throw new ArgumentException($"Unsupported address family {first.AddressFamily}.", nameof(range))
        };

        if (key.First > key.Last)
            throw new ArgumentException($"The range {range} ends before it starts.", nameof(range));
        return key;
    }

    /// <summary>
    ///     The fewest prefixes that cover exactly the range: each is the largest aligned block that starts at the next
    ///     uncovered address and ends within the range. Bits are right-aligned integers of the family's width.
    /// </summary>
    public static IEnumerable<(UInt128 Bits, int Length)> Decompose(RangeKey key)
    {
        var width = key.IsIPv6 ? 128 : 32;
        var first = key.First;
        while (true)
        {
            var aligned = first == UInt128.Zero ? width : Math.Min(width, (int)UInt128.TrailingZeroCount(first));
            var span = key.Last - first;
            var fits = span == UInt128.MaxValue ? 128 : 127 - (int)UInt128.LeadingZeroCount(span + 1);
            var k = Math.Min(aligned, fits);
            yield return (first, width - k);

            if (k == 128) yield break;
            var blockLast = first + ((UInt128.One << k) - 1);
            if (blockLast == key.Last) yield break;
            first = blockLast + 1;
        }
    }

    // every claim of a piece, winner first
    public static void AddClaims(List<RangeClaim<TRange, TValue>> claims, TRange winner, in RangePiece<TRange, TValue> piece)
    {
        claims.Add(new RangeClaim<TRange, TValue>(winner, piece.Value, piece.Key));
        if (piece.Others != null) claims.AddRange(piece.Others);
    }

    // smallest first; the sort is stable, so equal claims keep their order
    public static void SortBySize(List<RangeClaim<TRange, TValue>> claims)
    {
        if (claims.Count < 2) return;
        var sorted = claims.OrderBy(c => c.Key, Comparer<RangeKey>.Create(RangeKey.BySize)).ToList();
        claims.Clear();
        claims.AddRange(sorted);
    }

    public static IReadOnlyList<PrefixEntry<TRange, TValue>> Matches(IReadOnlyList<PrefixEntry<TRange, RangePiece<TRange, TValue>>> pieces)
    {
        var claims = new List<RangeClaim<TRange, TValue>>();
        foreach (var piece in pieces)
            AddClaims(claims, piece.Prefix, piece.Value);
        SortBySize(claims);
        return claims.Select(c => new PrefixEntry<TRange, TValue>(c.Range, c.Value)).ToList();
    }

    public static int Matches(IReadOnlyList<PrefixEntry<TRange, RangePiece<TRange, TValue>>> pieces, Span<PrefixEntry<TRange, TValue>> destination)
    {
        var matches = Matches(pieces);
        if (matches.Count > destination.Length)
            throw new ArgumentException("The destination is too small for the matches.", nameof(destination));
        for (var i = 0; i < matches.Count; i++)
            destination[i] = matches[i];
        return matches.Count;
    }

    // the smallest stored range that contains the whole range: it has a piece that contains the range's first address
    public static bool TryGetMatch(IReadOnlyBitTrie<Bits32, TRange, RangePiece<TRange, TValue>> v4, IReadOnlyBitTrie<Bits128, TRange, RangePiece<TRange, TValue>> v6,
        TRange network, out TRange range, out TValue value)
    {
        var key = KeyOf(network);
        var pieces = key.IsIPv6 ? v6.GetMatches(new Bits128(key.First)) : v4.GetMatches(new Bits32((uint)key.First));
        var found = false;
        var best = default(RangeKey);
        range = default!;
        value = default!;
        var claims = new List<RangeClaim<TRange, TValue>>();
        foreach (var piece in pieces)
            AddClaims(claims, piece.Prefix, piece.Value);
        foreach (var claim in claims)
        {
            if (!claim.Key.Contains(key) || (found && RangeKey.BySize(claim.Key, best) >= 0)) continue;
            (found, best, range, value) = (true, claim.Key, claim.Range, claim.Value);
        }

        return found;
    }
}
