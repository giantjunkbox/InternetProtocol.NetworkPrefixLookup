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

/// <summary>
///     A read-write set of IPv4 and IPv6 prefixes of type <typeparamref name="TPrefix" />, each with a value, with fast
///     longest-prefix-match lookups. <typeparamref name="TAdapter" /> tells it how to read the prefix type.
/// </summary>
/// <remarks>
///     <para>
///         Each address family has its own <see cref="BitTrie{TKey,TPrefix,TValue}" />, with the same guarantees: any number
///         of threads may read while one writes, reads take no locks, and every write does a bounded amount of work.
///     </para>
///     <para>
///         For a table that is loaded once and then only read, <see cref="Freeze(FrozenBitTrieOptions?)" /> builds a copy with faster lookups.
///     </para>
/// </remarks>
/// <typeparam name="TPrefix">The prefix type.</typeparam>
/// <typeparam name="TAdapter">Reads the prefix type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class IPPrefixLookup<TPrefix, TAdapter, TValue> : IPPrefixLookupBase<TPrefix, TAdapter, TValue, ReadWriteForm>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>
{
    private readonly BitTrie<Bits32, TPrefix, TValue> _v4;
    private readonly BitTrie<Bits128, TPrefix, TValue> _v6;

    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    public IPPrefixLookup(IPLookupOptions? options = null) : this(new BitTrie<Bits32, TPrefix, TValue>(), new BitTrie<Bits128, TPrefix, TValue>(), options)
    {
    }

    private IPPrefixLookup(BitTrie<Bits32, TPrefix, TValue> v4, BitTrie<Bits128, TPrefix, TValue> v6, IPLookupOptions? options)
        : base(v4, v6, options ?? new IPLookupOptions())
    {
        _v4 = v4;
        _v6 = v6;
    }

    /// <summary>Creates a lookup holding the given prefixes, loaded as one batch.</summary>
    /// <param name="items">The prefixes and their values.</param>
    /// <param name="options">Lookup options; by default every address is looked up as given.</param>
    /// <exception cref="ArgumentException">A prefix appears more than once.</exception>
    public IPPrefixLookup(IEnumerable<KeyValuePair<TPrefix, TValue>> items, IPLookupOptions? options = null) : this(options)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var batch = BeginBatch();
        foreach (var (prefix, value) in items)
            Add(prefix, value);
        batch.Commit();
    }

    /// <summary>
    ///     A number that changes with every published change to either family, so a caller can cache lookup results and
    ///     check that they are still current with one comparison.
    /// </summary>
    /// <remarks>Read it before the lookups whose results it is to vouch for: a value read after them may already count a change they missed.</remarks>
    public long Version => _v4.Version + _v6.Version;

    // ---- writes ----------------------------------------------------------------------------------------------------

    /// <summary>Adds a prefix. Returns false, and changes nothing, when it is already present.</summary>
    /// <param name="prefix">The prefix; host bits after its length are ignored.</param>
    /// <param name="value">Its value.</param>
    public bool TryAdd(TPrefix prefix, TValue value)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? _v4.TryAdd(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix), prefix, value)
            : _v6.TryAdd(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix), prefix, value);
    }

    /// <summary>Adds a prefix.</summary>
    /// <param name="prefix">The prefix; host bits after its length are ignored.</param>
    /// <param name="value">Its value.</param>
    /// <exception cref="ArgumentException">The prefix is already present.</exception>
    public void Add(TPrefix prefix, TValue value)
    {
        if (!TryAdd(prefix, value))
            throw new ArgumentException($"The prefix {prefix} is already present.", nameof(prefix));
    }

    /// <summary>Adds a prefix, or replaces the stored prefix object and value of one already present.</summary>
    /// <param name="prefix">The prefix; host bits after its length are ignored.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True when the prefix was added, false when it was replaced.</returns>
    public bool AddOrUpdate(TPrefix prefix, TValue value)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? _v4.AddOrUpdate(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix), prefix, value)
            : _v6.AddOrUpdate(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix), prefix, value);
    }

    /// <summary>Returns the value of a prefix, adding it with a value from <paramref name="valueFactory" /> if absent.</summary>
    /// <param name="prefix">The prefix; host bits after its length are ignored.</param>
    /// <param name="valueFactory">Creates the value, called only when the prefix is absent, while other writers wait.</param>
    public TValue GetOrAdd(TPrefix prefix, Func<TPrefix, TValue> valueFactory)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? _v4.GetOrAdd(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix), prefix, valueFactory)
            : _v6.GetOrAdd(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix), prefix, valueFactory);
    }

    /// <summary>Removes a prefix. Returns false when it is not present.</summary>
    /// <param name="prefix">The prefix; host bits after its length are ignored.</param>
    public bool Remove(TPrefix prefix)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? _v4.Remove(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix))
            : _v6.Remove(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix));
    }

    /// <summary>Removes every prefix.</summary>
    public void Clear()
    {
        _v4.Clear();
        _v6.Clear();
    }

    /// <summary>
    ///     Starts a batch: the following changes build privately and stay invisible to readers until
    ///     <see cref="Batch.Commit" />. Disposing the batch without committing it discards them.
    /// </summary>
    /// <remarks>
    ///     Each address family publishes its changes with one reference write when the batch commits, IPv4 first; a reader
    ///     can see the new IPv4 prefixes a moment before the new IPv6 ones. The batch holds the writer locks until it is
    ///     committed or disposed. The locks belong to the thread that started the batch, so it must end on that thread
    ///     and cannot span an <c>await</c>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A batch is already in progress.</exception>
    public Batch BeginBatch()
    {
        var v4 = _v4.BeginBatch();
        try
        {
            return new Batch(v4, _v6.BeginBatch());
        }
        catch
        {
            v4.Dispose();
            throw;
        }
    }

    /// <summary>A set of changes that readers see all at once, or not at all. See <see cref="BeginBatch" />.</summary>
    public sealed class Batch : IDisposable
    {
        private readonly BitTrie<Bits32, TPrefix, TValue>.Batch _v4;
        private readonly BitTrie<Bits128, TPrefix, TValue>.Batch _v6;

        internal Batch(BitTrie<Bits32, TPrefix, TValue>.Batch v4, BitTrie<Bits128, TPrefix, TValue>.Batch v6)
        {
            _v4 = v4;
            _v6 = v6;
        }

        /// <summary>Publishes every change in the batch, one reference write per address family.</summary>
        /// <remarks>
        ///     If a write in the batch threw, nothing is published, the batch is discarded and Commit throws. Otherwise only
        ///     running out of memory can make it throw: if the IPv4 changes fail, nothing is published; if the IPv6 changes
        ///     fail, the IPv4 changes stay published. Either way the batch ends.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The batch was already committed or disposed.</exception>
        /// <exception cref="InvalidOperationException">
        ///     The batch was ended on a thread other than the one that started it, which leaves it open; or a write in the
        ///     batch threw, and the batch was discarded.
        /// </exception>
        public void Commit()
        {
            _v4.CheckThread();
            _v6.CheckThread();
            if (_v4.Failed || _v6.Failed)
            {
                Dispose();
                throw new InvalidOperationException("A write in the batch threw, so the batch was discarded.");
            }

            try
            {
                _v4.Commit();
            }
            catch
            {
                // nothing is published yet, so the IPv6 changes go too
                _v6.Dispose();
                throw;
            }

            _v6.Commit();
        }

        /// <summary>Discards the batch's changes unless it was committed.</summary>
        /// <exception cref="InvalidOperationException">
        ///     The batch was ended on a thread other than the one that started it, which leaves it open.
        /// </exception>
        public void Dispose()
        {
            _v4.CheckThread();
            _v6.CheckThread();
            try
            {
                _v6.Dispose();
            }
            finally
            {
                _v4.Dispose();
            }
        }
    }

    // ---- freezing and diagnostics ----------------------------------------------------------------------------------

    /// <summary>
    ///     Builds a read-only copy with faster lookups. Later changes to this lookup do not affect it. Writes wait while
    ///     the copy is built; reads do not.
    /// </summary>
    /// <param name="options">
    ///     The layout of both address families' tables; the default is leaf-pushed, with a root sized to each table.
    /// </param>
    public FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> Freeze(FrozenBitTrieOptions? options = null) => new(this, options, options);

    /// <summary>Builds a read-only copy with a different layout for each address family. See <see cref="Freeze(FrozenBitTrieOptions?)" />.</summary>
    /// <param name="ipv4">The layout of the IPv4 tables.</param>
    /// <param name="ipv6">The layout of the IPv6 tables.</param>
    public FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> Freeze(FrozenBitTrieOptions ipv4, FrozenBitTrieOptions ipv6) => new(this, ipv4, ipv6);

    // Both families under both writer locks, taken in BeginBatch's order, so that no batch commits between the two and
    // the copy is the lookup at one moment.
    internal (FrozenBitTrie<Bits32, TPrefix, TValue> IPv4, FrozenBitTrie<Bits128, TPrefix, TValue> IPv6) FreezeParts(FrozenBitTrieOptions? ipv4, FrozenBitTrieOptions? ipv6)
    {
        using (_v4.WriterLock.EnterScope())
        using (_v6.WriterLock.EnterScope())
            return (_v4.Freeze(ipv4), _v6.Freeze(ipv6));
    }

    /// <summary>Counts the nodes and estimates the memory each address family holds.</summary>
    public IPLookupStatistics GetStatistics() => new(_v4.GetStatistics(), _v6.GetStatistics());

    /// <summary>The structure of one address family's trie as indented text, for people and tests.</summary>
    /// <param name="family"><see cref="AddressFamily.InterNetwork" /> or <see cref="AddressFamily.InterNetworkV6" />.</param>
    public string ToTextTree(AddressFamily family)
    {
        IPAddressBits.CheckFamily(family);
        return family == AddressFamily.InterNetwork ? _v4.ToTextTree(IPAddressBits.Format) : _v6.ToTextTree(IPAddressBits.Format);
    }

    /// <summary>The structure of one address family's trie in Graphviz DOT format.</summary>
    /// <param name="family"><see cref="AddressFamily.InterNetwork" /> or <see cref="AddressFamily.InterNetworkV6" />.</param>
    public string ToDotTree(AddressFamily family)
    {
        IPAddressBits.CheckFamily(family);
        return family == AddressFamily.InterNetwork ? _v4.ToDotTree(IPAddressBits.Format) : _v6.ToDotTree(IPAddressBits.Format);
    }
}

/// <summary>The size of each address family's trie in an IP lookup.</summary>
/// <param name="IPv4">The IPv4 trie.</param>
/// <param name="IPv6">The IPv6 trie.</param>
public readonly record struct IPLookupStatistics(BitTrieStatistics IPv4, BitTrieStatistics IPv6);
