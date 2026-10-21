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

using System.Collections.Immutable;

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
///         Counting, listing, <c>TryGetValue</c>, <c>ContainsPrefix</c> and <see cref="Remove" /> work on
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
public class IPRangeLookup<TRange, TAdapter, TValue> : IPRangeLookupBase<TRange, TAdapter, TValue, ReadWriteForm>
    where TAdapter : struct, IIPRangeAdapter<TRange>
{
    private readonly BitTrie<Bits32, TRange, RangePiece<TRange, TValue>> _v4;
    private readonly BitTrie<Bits128, TRange, RangePiece<TRange, TValue>> _v6;
    private readonly Lock _lock = new();

    // the writer's view of the ranges, which runs ahead of the published one only during a batch
    private ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>> _pending = ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>>.Empty;
    private bool _batch;

    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    public IPRangeLookup(IPLookupOptions? options = null)
        : this(new BitTrie<Bits32, TRange, RangePiece<TRange, TValue>>(), new BitTrie<Bits128, TRange, RangePiece<TRange, TValue>>(), options)
    {
    }

    private IPRangeLookup(BitTrie<Bits32, TRange, RangePiece<TRange, TValue>> v4, BitTrie<Bits128, TRange, RangePiece<TRange, TValue>> v6, IPLookupOptions? options)
        : base(v4, v6, options ?? new IPLookupOptions(), ImmutableSortedDictionary<RangeKey, RangeClaim<TRange, TValue>>.Empty)
    {
        _v4 = v4;
        _v6 = v6;
    }

    /// <summary>Creates a lookup holding the given ranges, loaded as one batch.</summary>
    /// <param name="items">The ranges and their values.</param>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    /// <exception cref="ArgumentException">A range appears more than once.</exception>
    public IPRangeLookup(IEnumerable<KeyValuePair<TRange, TValue>> items, IPLookupOptions? options = null) : this(options)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var batch = BeginBatch();
        foreach (var (range, value) in items)
            Add(range, value);
        batch.Commit();
    }

    /// <summary>
    ///     A number that changes with every published change, so a caller can cache lookup results and check that they are
    ///     still current with one comparison.
    /// </summary>
    /// <remarks>Read it before the lookups whose results it is to vouch for: a value read after them may already count a change they missed.</remarks>
    public long Version => _v4.Version + _v6.Version;


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
    ///     holds the writer locks until it is committed or disposed. The locks belong to the thread that started the
    ///     batch, so it must end on that thread and cannot span an <c>await</c>.
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

        // the locks belong to the thread that started the batch; ending it elsewhere is refused before anything changes
        private static void CheckThread(IPRangeLookup<TRange, TAdapter, TValue> lookup)
        {
            if (!lookup._lock.IsHeldByCurrentThread)
                throw new InvalidOperationException(
                    "A batch must end on the thread that started it, so it cannot span an await. The batch is still open, and other writers wait until that thread ends it.");
        }

        /// <summary>Publishes every change in the batch.</summary>
        /// <remarks>
        ///     If a write in the batch threw, nothing is published, the batch is discarded and Commit throws. Otherwise only
        ///     running out of memory can make it throw: if the IPv4 changes fail, nothing is published; if the IPv6 changes
        ///     fail, the IPv4 changes stay published. Either way the batch ends, and the ranges listed match the ranges
        ///     found.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The batch was already committed or disposed.</exception>
        /// <exception cref="InvalidOperationException">
        ///     The batch was ended on a thread other than the one that started it, which leaves it open; or a write in the
        ///     batch threw, and the batch was discarded.
        /// </exception>
        public void Commit()
        {
            var lookup = _lookup ?? throw new ObjectDisposedException(nameof(Batch));
            CheckThread(lookup);
            if (_v4.Failed || _v6.Failed)
            {
                Dispose();
                throw new InvalidOperationException("A write in the batch threw, so the batch was discarded.");
            }

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
        /// <exception cref="InvalidOperationException">
        ///     The batch was ended on a thread other than the one that started it, which leaves it open.
        /// </exception>
        public void Dispose()
        {
            var lookup = _lookup;
            if (lookup == null) return;
            CheckThread(lookup);
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
