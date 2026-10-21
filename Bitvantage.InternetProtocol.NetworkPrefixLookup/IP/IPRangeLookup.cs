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

using System.Collections;
using System.Collections.Immutable;
using System.Net;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     A read-write set of IPv4 and IPv6 address ranges of type <typeparamref name="TRange" />, each with a value, with
///     fast lookups of the smallest range that contains an address. A range need not be a CIDR block, as in
///     <c>192.168.0.10-192.168.0.20</c>. <typeparamref name="TAdapter" /> tells it how to read the range type.
/// </summary>
/// <remarks>
///     <para>
///         Each range is stored as the fewest prefixes that cover it exactly: one for a CIDR block, at most 62 for an IPv4
///         range and 254 for an IPv6 range. Every piece holds the range and its value, so lookups return the range and
///         are as fast as prefix lookups.
///     </para>
///     <para>
///         <b>Nested ranges</b> work as expected: an address in both gets the inner range. When two ranges split into
///         an identical piece, the piece keeps both, the smaller range first, so removing one uncovers the other.
///         <b>Partly overlapping ranges</b> have no single right answer: an address in both gets whichever range's
///         covering piece is smaller. Split them into ranges that nest or do not touch if that matters.
///     </para>
///     <para>
///         Counting, listing, <see cref="TryGetValue" />, <see cref="ContainsPrefix" /> and <see cref="Remove" /> work on
///         whole ranges, matched exactly by their first and last address. A range is read once, when it is added;
///         changing a stored range object afterwards changes nothing in the lookup.
///     </para>
///     <para>
///         <b>Thread safety.</b> Any number of threads may read while one writes, and reads take no locks. Every change
///         to a range publishes all of its pieces at once. A change either completes or leaves the lookup as it was.
///     </para>
/// </remarks>
/// <typeparam name="TRange">The range type.</typeparam>
/// <typeparam name="TAdapter">Reads the range type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class IPRangeLookup<TRange, TAdapter, TValue> : IReadOnlyIPPrefixLookup<TRange, TValue>
    where TAdapter : struct, IIPRangeAdapter<TRange>
{
    // The read-only members are the same as in FrozenIPRangeLookup; keep the two in step.
    private readonly BitTrie<Bits32, TRange, RangePiece<TRange, TValue>> _v4 = new();
    private readonly BitTrie<Bits128, TRange, RangePiece<TRange, TValue>> _v6 = new();
    private readonly bool _map;
    private readonly Lock _lock = new();

    // the ranges readers see, and the writer's view, which is ahead only during a batch
    private ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> _published = ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>>.Empty;
    private ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> _pending = ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>>.Empty;
    private bool _batch;

    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; the defaults map IPv4-mapped IPv6 addresses to IPv4.</param>
    public IPRangeLookup(IPLookupOptions? options = null)
    {
        Options = options ?? new IPLookupOptions();
        _map = Options.MapIPv4MappedAddresses;
    }

    /// <summary>Creates a lookup holding the given ranges, loaded as one batch.</summary>
    /// <param name="items">The ranges and their values.</param>
    /// <param name="options">Lookup options; the defaults map IPv4-mapped IPv6 addresses to IPv4.</param>
    /// <exception cref="ArgumentException">A range appears more than once.</exception>
    public IPRangeLookup(IEnumerable<KeyValuePair<TRange, TValue>> items, IPLookupOptions? options = null) : this(options)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var batch = BeginBatch();
        foreach (var (range, value) in items)
            Add(range, value);
        batch.Commit();
    }

    /// <summary>The options the lookup was created with.</summary>
    public IPLookupOptions Options { get; }

    /// <summary>
    ///     A number that changes with every published change, so a caller can cache lookup results and check that they are
    ///     still current with one comparison.
    /// </summary>
    public long Version => _v4.Version + _v6.Version;

    private ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> Ranges => Volatile.Read(ref _published);

    // ---- lookups ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool TryGetMatch(IPAddress address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out var piece) || (v6 != 0 && _v6.TryGetMatch(new Bits128(v6), out prefix, out piece))
            : _v6.TryGetMatch(new Bits128(v6), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatch(ReadOnlySpan<byte> address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out var piece) || (v6 != 0 && _v6.TryGetMatch(new Bits128(v6), out prefix, out piece))
            : _v6.TryGetMatch(new Bits128(v6), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchIPv4(uint address, out TRange prefix, out TValue value)
    {
        var found = _v4.TryGetMatch(new Bits32(address), out prefix, out var piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchIPv6(UInt128 address, out TRange prefix, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out var piece) || _v6.TryGetMatch(new Bits128(address), out prefix, out piece)
            : _v6.TryGetMatch(new Bits128(address), out prefix, out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValue(IPAddress address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatchValue(new Bits32(v4), out var piece) || (v6 != 0 && _v6.TryGetMatchValue(new Bits128(v6), out piece))
            : _v6.TryGetMatchValue(new Bits128(v6), out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValue(ReadOnlySpan<byte> address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatchValue(new Bits32(v4), out var piece) || (v6 != 0 && _v6.TryGetMatchValue(new Bits128(v6), out piece))
            : _v6.TryGetMatchValue(new Bits128(v6), out piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValueIPv4(uint address, out TValue value)
    {
        var found = _v4.TryGetMatchValue(new Bits32(address), out var piece);
        value = piece.Value;
        return found;
    }

    /// <inheritdoc />
    public bool TryGetMatchValueIPv6(UInt128 address, out TValue value)
    {
        var found = IPAddressBits.IsIPv4(address, _map, out var v4)
            ? _v4.TryGetMatchValue(new Bits32(v4), out var piece) || _v6.TryGetMatchValue(new Bits128(address), out piece)
            : _v6.TryGetMatchValue(new Bits128(address), out piece);
        value = piece.Value;
        return found;
    }

    /// <summary>Finds the smallest stored range that contains the whole of a range, which may be the range itself.</summary>
    /// <param name="network">The range to look up.</param>
    /// <param name="prefix">The matching range.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when a stored range contains the range.</returns>
    public bool TryGetMatch(TRange network, out TRange prefix, out TValue value) =>
        RangePieces<TRange, TAdapter, TValue>.TryGetMatch(_v4, _v6, network, out prefix, out value);

    /// <summary>Gets the value of exactly this range.</summary>
    /// <param name="prefix">The range.</param>
    /// <param name="value">Its value, when stored.</param>
    /// <returns>True when the range is stored.</returns>
    public bool TryGetValue(TRange prefix, out TValue value)
    {
        var found = Ranges.TryGetValue(RangePieces<TRange, TAdapter, TValue>.KeyOf(prefix), out var claim);
        value = claim.Value;
        return found;
    }

    /// <summary>True when exactly this range is stored.</summary>
    /// <param name="prefix">The range.</param>
    public bool ContainsPrefix(TRange prefix) => Ranges.ContainsKey(RangePieces<TRange, TAdapter, TValue>.KeyOf(prefix));

    /// <summary>
    ///     Every stored range that contains an address, smallest first. An IPv4-mapped address that is looked up as IPv4
    ///     gets the IPv4 ranges first and then the IPv6 ones.
    /// </summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    public IReadOnlyList<PrefixEntry<TRange, TValue>> GetMatches(IPAddress address)
    {
        if (!IPAddressBits.IsIPv4(address, _map, out var v4, out var v6))
            return RangePieces<TRange, TAdapter, TValue>.Matches(_v6.GetMatches(new Bits128(v6)));
        var matches = RangePieces<TRange, TAdapter, TValue>.Matches(_v4.GetMatches(new Bits32(v4)));
        return v6 == 0 ? matches : [.. matches, .. RangePieces<TRange, TAdapter, TValue>.Matches(_v6.GetMatches(new Bits128(v6)))];
    }

    /// <summary>Writes every stored range that contains an address into a span, in the order of <see cref="GetMatches(IPAddress)" />.</summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <param name="destination">Where to write the matches. Overlapping ranges can need any number of entries.</param>
    /// <returns>The number of matches written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public int GetMatches(IPAddress address, Span<PrefixEntry<TRange, TValue>> destination)
    {
        var matches = GetMatches(address);
        if (matches.Count > destination.Length)
            throw new ArgumentException("The destination is too small for the matches.", nameof(destination));
        for (var i = 0; i < matches.Count; i++)
            destination[i] = matches[i];
        return matches.Count;
    }

    /// <summary>The number of stored ranges, IPv4 and IPv6.</summary>
    public int Count => Ranges.Count;

    /// <summary>Returns the IPv4 ranges and then the IPv6 ranges, each in address order, a larger range first at the same address.</summary>
    public IEnumerator<PrefixEntry<TRange, TValue>> GetEnumerator()
    {
        foreach (var claim in Ranges.Values)
            yield return new PrefixEntry<TRange, TValue>(claim.Range, claim.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- writes ----------------------------------------------------------------------------------------------------

    /// <summary>Adds a range. Returns false, and changes nothing, when it is already present.</summary>
    /// <param name="range">The range.</param>
    /// <param name="value">Its value.</param>
    /// <exception cref="ArgumentException">The range mixes address families or ends before it starts.</exception>
    public bool TryAdd(TRange range, TValue value)
    {
        var key = RangePieces<TRange, TAdapter, TValue>.KeyOf(range);
        using (_lock.EnterScope())
        {
            if (_pending.ContainsKey(key))
                return false;
            var claim = new RangeClaim<TRange, TValue>(range, value, key);
            ChangeClaims(key, claims => claims.Add(claim));
            Publish(_pending.Add(key, claim));
            return true;
        }
    }

    /// <summary>Adds a range.</summary>
    /// <param name="range">The range.</param>
    /// <param name="value">Its value.</param>
    /// <exception cref="ArgumentException">The range is already present, mixes address families or ends before it starts.</exception>
    public void Add(TRange range, TValue value)
    {
        if (!TryAdd(range, value))
            throw new ArgumentException($"The range {range} is already present.", nameof(range));
    }

    /// <summary>Adds a range, or replaces the stored range object and value of one already present.</summary>
    /// <param name="range">The range.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when the range was added, false when it was replaced.</returns>
    public bool AddOrUpdate(TRange range, TValue value)
    {
        var key = RangePieces<TRange, TAdapter, TValue>.KeyOf(range);
        using (_lock.EnterScope())
        {
            if (!_pending.ContainsKey(key))
                return TryAdd(range, value);

            var claim = new RangeClaim<TRange, TValue>(range, value, key);
            ChangeClaims(key, claims => claims[IndexOf(claims, key)] = claim);
            Publish(_pending.SetItem(key, claim));
            return false;
        }
    }

    /// <summary>Returns the value of a range, adding it with a value from <paramref name="valueFactory" /> if absent.</summary>
    /// <param name="range">The range.</param>
    /// <param name="valueFactory">Creates the value, called only when the range is absent, while other writers wait.</param>
    public TValue GetOrAdd(TRange range, Func<TRange, TValue> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        var key = RangePieces<TRange, TAdapter, TValue>.KeyOf(range);
        using (_lock.EnterScope())
        {
            if (_pending.TryGetValue(key, out var claim))
                return claim.Value;
            var value = valueFactory(range);
            TryAdd(range, value);
            return value;
        }
    }

    /// <summary>Removes a range. Returns false when it is not present.</summary>
    /// <param name="range">The range, matched by its first and last address.</param>
    public bool Remove(TRange range)
    {
        var key = RangePieces<TRange, TAdapter, TValue>.KeyOf(range);
        using (_lock.EnterScope())
        {
            if (!_pending.ContainsKey(key))
                return false;
            ChangeClaims(key, claims => claims.RemoveAt(IndexOf(claims, key)));
            Publish(_pending.Remove(key));
            return true;
        }
    }

    /// <summary>Removes every range.</summary>
    public void Clear()
    {
        using (_lock.EnterScope())
        {
            _v4.Clear();
            _v6.Clear();
            Publish(_pending.Clear());
        }
    }

    private void Publish(ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> ranges)
    {
        _pending = ranges;
        if (!_batch) Volatile.Write(ref _published, ranges);
    }

    private static int IndexOf(List<RangeClaim<TRange, TValue>> claims, RangeKey key)
    {
        var at = claims.FindIndex(c => c.Key == key);
        return at >= 0 ? at : throw new InvalidOperationException("A piece of a stored range is missing.");
    }

    private void ChangeClaims(RangeKey key, Action<List<RangeClaim<TRange, TValue>>> change)
    {
        if (key.IsIPv6) ChangeClaims(_v6, key, change, bits => new Bits128(bits));
        else ChangeClaims(_v4, key, change, bits => new Bits32((uint)bits));
    }

    // Applies a change to the claims of every piece of a range. All the pieces' new claims are worked out from the
    // writer's view of the trie before anything is written, so a change that fails leaves the lookup as it was, and a
    // range of several pieces is published at once: by the caller's batch, or by a batch of its own.
    private void ChangeClaims<TKey>(BitTrie<TKey, TRange, RangePiece<TRange, TValue>> core, RangeKey key, Action<List<RangeClaim<TRange, TValue>>> change, Func<UInt128, TKey> toKey)
        where TKey : struct, IBitKey<TKey>
    {
        var updates = new List<(TKey Bits, int Length, List<RangeClaim<TRange, TValue>> Claims)>();
        foreach (var (value, length) in RangePieces<TRange, TAdapter, TValue>.Decompose(key))
        {
            var bits = toKey(value);
            var claims = new List<RangeClaim<TRange, TValue>>();
            if (core.TryGetPendingValue(bits, length, out var winner, out var piece))
                RangePieces<TRange, TAdapter, TValue>.AddClaims(claims, winner, piece);
            change(claims);
            RangePieces<TRange, TAdapter, TValue>.SortBySize(claims);
            updates.Add((bits, length, claims));
        }

        using var batch = updates.Count > 1 && !_batch ? core.BeginBatch() : null;
        foreach (var (bits, length, claims) in updates)
        {
            if (claims.Count == 0) core.Remove(bits, length);
            else core.AddOrUpdate(bits, length, claims[0].Range, RangePiece<TRange, TValue>.From(claims));
        }

        batch?.Commit();
    }

    /// <summary>
    ///     Starts a batch: the following changes build privately and stay invisible to readers until
    ///     <see cref="Batch.Commit" />. Disposing the batch without committing it discards them.
    /// </summary>
    /// <remarks>
    ///     Each address family publishes its pieces with one reference write when the batch commits, IPv4 first. The batch
    ///     holds the writer locks until it is committed or disposed, and must end on the thread that started it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A batch is already in progress.</exception>
    public Batch BeginBatch()
    {
        _lock.Enter();
        try
        {
            if (_batch)
                throw new InvalidOperationException("A batch is already in progress.");
            var v4 = _v4.BeginBatch();
            BitTrie<Bits128, TRange, RangePiece<TRange, TValue>>.Batch v6;
            try
            {
                v6 = _v6.BeginBatch();
            }
            catch
            {
                v4.Dispose();
                throw;
            }

            _batch = true;
            return new Batch(this, v4, v6);
        }
        catch
        {
            _lock.Exit();
            throw;
        }
    }

    /// <summary>A set of changes that readers see all at once, or not at all. See <see cref="BeginBatch" />.</summary>
    public sealed class Batch : IDisposable
    {
        private IPRangeLookup<TRange, TAdapter, TValue>? _lookup;
        private readonly BitTrie<Bits32, TRange, RangePiece<TRange, TValue>>.Batch _v4;
        private readonly BitTrie<Bits128, TRange, RangePiece<TRange, TValue>>.Batch _v6;

        internal Batch(IPRangeLookup<TRange, TAdapter, TValue> lookup, BitTrie<Bits32, TRange, RangePiece<TRange, TValue>>.Batch v4, BitTrie<Bits128, TRange, RangePiece<TRange, TValue>>.Batch v6)
        {
            _lookup = lookup;
            _v4 = v4;
            _v6 = v6;
        }

        /// <summary>Publishes every change in the batch.</summary>
        /// <remarks>
        ///     Only running out of memory can make a commit throw. If the IPv4 changes fail, nothing is published; if the
        ///     IPv6 changes fail, the IPv4 changes stay published. Either way the batch ends, and the ranges listed match
        ///     the ranges found.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The batch was already committed or disposed.</exception>
        public void Commit()
        {
            var lookup = _lookup ?? throw new ObjectDisposedException(nameof(Batch));
            _lookup = null;
            try
            {
                try
                {
                    _v4.Commit();
                }
                catch
                {
                    // nothing is published yet, so the IPv6 changes go too
                    _v6.Dispose();
                    lookup._pending = lookup._published;
                    throw;
                }

                try
                {
                    _v6.Commit();
                }
                catch
                {
                    // the IPv4 changes are published and the IPv6 ones discarded, so list the ranges to match
                    var published = lookup._published;
                    lookup._pending = published.RemoveRange(published.Keys.Where(k => !k.IsIPv6)).AddRange(lookup._pending.Where(r => !r.Key.IsIPv6));
                    Volatile.Write(ref lookup._published, lookup._pending);
                    throw;
                }

                Volatile.Write(ref lookup._published, lookup._pending);
            }
            finally
            {
                lookup._batch = false;
                lookup._lock.Exit();
            }
        }

        /// <summary>Discards the batch's changes unless it was committed.</summary>
        public void Dispose()
        {
            var lookup = _lookup;
            if (lookup == null) return;
            _lookup = null;
            try
            {
                try
                {
                    _v6.Dispose();
                }
                finally
                {
                    _v4.Dispose();
                }

                lookup._pending = lookup._published;
            }
            finally
            {
                lookup._batch = false;
                lookup._lock.Exit();
            }
        }
    }

    // ---- freezing --------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Builds a read-only copy with faster lookups. Later changes to this lookup do not affect it. Writes wait while
    ///     the copy is built; reads do not.
    /// </summary>
    /// <param name="ipv4">The layout of the IPv4 tables; the default is leaf-pushed, with a root sized to the table.</param>
    /// <param name="ipv6">The layout of the IPv6 tables; the default is leaf-pushed, with a root sized to the table.</param>
    public FrozenIPRangeLookup<TRange, TAdapter, TValue> Freeze(FrozenBitTrieOptions? ipv4 = null, FrozenBitTrieOptions? ipv6 = null) => new(this, ipv4, ipv6);

    internal (FrozenBitTrie<Bits32, TRange, RangePiece<TRange, TValue>> IPv4, FrozenBitTrie<Bits128, TRange, RangePiece<TRange, TValue>> IPv6, ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> Ranges)
        FreezeParts(FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6)
    {
        using (_lock.EnterScope())
            return (_v4.Freeze(ipv4), _v6.Freeze(ipv6), _published);
    }

    /// <summary>Counts the nodes and estimates the memory each address family holds. Prefixes here are range pieces.</summary>
    public IPLookupStatistics GetStatistics() => new(_v4.GetStatistics(), _v6.GetStatistics());
}
