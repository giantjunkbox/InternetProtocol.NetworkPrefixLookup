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
using System.Net;
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
///         For a table that is loaded once and then only read, <see cref="Freeze" /> builds a copy whose lookups are about
///         twice as fast.
///     </para>
/// </remarks>
/// <typeparam name="TPrefix">The prefix type.</typeparam>
/// <typeparam name="TAdapter">Reads the prefix type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class IPPrefixLookup<TPrefix, TAdapter, TValue> : IReadOnlyIPPrefixLookup<TPrefix, TValue>
    where TAdapter : struct, IIPPrefixAdapter<TPrefix>
{
    // The read-only members are the same as in FrozenIPPrefixLookup; keep the two in step.
    private readonly BitTrie<Bits32, TPrefix, TValue> _v4 = new();
    private readonly BitTrie<Bits128, TPrefix, TValue> _v6 = new();
    private readonly bool _map;

    /// <summary>Creates an empty lookup.</summary>
    /// <param name="options">Lookup options; the defaults map IPv4-mapped IPv6 addresses to IPv4.</param>
    public IPPrefixLookup(IPLookupOptions? options = null)
    {
        Options = options ?? new IPLookupOptions();
        _map = Options.MapIPv4MappedAddresses;
    }

    /// <summary>Creates a lookup holding the given prefixes, loaded as one batch.</summary>
    /// <param name="items">The prefixes and their values.</param>
    /// <param name="options">Lookup options; the defaults map IPv4-mapped IPv6 addresses to IPv4.</param>
    /// <exception cref="ArgumentException">A prefix appears more than once.</exception>
    public IPPrefixLookup(IEnumerable<KeyValuePair<TPrefix, TValue>> items, IPLookupOptions? options = null) : this(options)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var batch = BeginBatch();
        foreach (var (prefix, value) in items)
            Add(prefix, value);
        batch.Commit();
    }

    /// <summary>The options the lookup was created with.</summary>
    public IPLookupOptions Options { get; }

    /// <summary>
    ///     A number that changes with every published change to either family, so a caller can cache lookup results and
    ///     check that they are still current with one comparison.
    /// </summary>
    public long Version => _v4.Version + _v6.Version;

    // ---- lookups ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool TryGetMatch(IPAddress address, out TPrefix prefix, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out value) || (v6 != 0 && _v6.TryGetMatch(new Bits128(v6), out prefix, out value))
            : _v6.TryGetMatch(new Bits128(v6), out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatch(ReadOnlySpan<byte> address, out TPrefix prefix, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out value) || (v6 != 0 && _v6.TryGetMatch(new Bits128(v6), out prefix, out value))
            : _v6.TryGetMatch(new Bits128(v6), out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatchIPv4(uint address, out TPrefix prefix, out TValue value) => _v4.TryGetMatch(new Bits32(address), out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatchIPv6(UInt128 address, out TPrefix prefix, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4)
            ? _v4.TryGetMatch(new Bits32(v4), out prefix, out value) || _v6.TryGetMatch(new Bits128(address), out prefix, out value)
            : _v6.TryGetMatch(new Bits128(address), out prefix, out value);

    /// <inheritdoc />
    public bool TryGetMatchValue(IPAddress address, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatchValue(new Bits32(v4), out value) || (v6 != 0 && _v6.TryGetMatchValue(new Bits128(v6), out value))
            : _v6.TryGetMatchValue(new Bits128(v6), out value);

    /// <inheritdoc />
    public bool TryGetMatchValue(ReadOnlySpan<byte> address, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4, out var v6)
            ? _v4.TryGetMatchValue(new Bits32(v4), out value) || (v6 != 0 && _v6.TryGetMatchValue(new Bits128(v6), out value))
            : _v6.TryGetMatchValue(new Bits128(v6), out value);

    /// <inheritdoc />
    public bool TryGetMatchValueIPv4(uint address, out TValue value) => _v4.TryGetMatchValue(new Bits32(address), out value);

    /// <inheritdoc />
    public bool TryGetMatchValueIPv6(UInt128 address, out TValue value) =>
        IPAddressBits.IsIPv4(address, _map, out var v4)
            ? _v4.TryGetMatchValue(new Bits32(v4), out value) || _v6.TryGetMatchValue(new Bits128(address), out value)
            : _v6.TryGetMatchValue(new Bits128(address), out value);

    /// <inheritdoc />
    public bool TryGetMatch(TPrefix network, out TPrefix prefix, out TValue value)
    {
        IPAddressBits.ThrowIfNull(network);
        return TAdapter.IsIPv4(network)
            ? _v4.TryGetMatch(new Bits32(TAdapter.GetIPv4Bits(network)), TAdapter.GetLength(network), out prefix, out value)
            : _v6.TryGetMatch(new Bits128(TAdapter.GetIPv6Bits(network)), TAdapter.GetLength(network), out prefix, out value);
    }

    /// <inheritdoc />
    public bool TryGetValue(TPrefix prefix, out TValue value)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? _v4.TryGetValue(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix), out value)
            : _v6.TryGetValue(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix), out value);
    }

    /// <inheritdoc />
    public bool ContainsPrefix(TPrefix prefix)
    {
        IPAddressBits.ThrowIfNull(prefix);
        return TAdapter.IsIPv4(prefix)
            ? _v4.ContainsPrefix(new Bits32(TAdapter.GetIPv4Bits(prefix)), TAdapter.GetLength(prefix))
            : _v6.ContainsPrefix(new Bits128(TAdapter.GetIPv6Bits(prefix)), TAdapter.GetLength(prefix));
    }

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(IPAddress address)
    {
        if (!IPAddressBits.IsIPv4(address, _map, out var v4, out var v6))
            return _v6.GetMatches(new Bits128(v6));
        var matches = _v4.GetMatches(new Bits32(v4));
        return v6 == 0 ? matches : [.. matches, .. _v6.GetMatches(new Bits128(v6))];
    }

    /// <inheritdoc />
    public int GetMatches(IPAddress address, Span<PrefixEntry<TPrefix, TValue>> destination)
    {
        if (!IPAddressBits.IsIPv4(address, _map, out var v4, out var v6))
            return _v6.GetMatches(new Bits128(v6), destination);
        var count = _v4.GetMatches(new Bits32(v4), destination);
        return v6 == 0 ? count : count + _v6.GetMatches(new Bits128(v6), destination[count..]);
    }

    /// <summary>The number of stored prefixes, IPv4 and IPv6.</summary>
    public int Count => _v4.Count + _v6.Count;

    /// <summary>Returns the IPv4 prefixes and then the IPv6 prefixes, each in address order.</summary>
    public IEnumerator<PrefixEntry<TPrefix, TValue>> GetEnumerator() => _v4.Concat(_v6).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

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
    ///     committed or disposed, and must end on the thread that started it.
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
        ///     Only running out of memory can make a commit throw. If the IPv4 changes fail, nothing is published; if the
        ///     IPv6 changes fail, the IPv4 changes stay published. Either way the batch ends.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The batch was already committed or disposed.</exception>
        public void Commit()
        {
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
        public void Dispose()
        {
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
    /// <param name="ipv4">The layout of the IPv4 tables; the default is leaf-pushed, with a root sized to the table.</param>
    /// <param name="ipv6">The layout of the IPv6 tables; the default is leaf-pushed, with a root sized to the table.</param>
    public FrozenIPPrefixLookup<TPrefix, TAdapter, TValue> Freeze(FrozenBitTrieOptions? ipv4 = null, FrozenBitTrieOptions? ipv6 = null) => new(this, ipv4, ipv6);

    internal FrozenBitTrie<Bits32, TPrefix, TValue> FreezeIPv4(FrozenBitTrieOptions? options) => _v4.Freeze(options);

    internal FrozenBitTrie<Bits128, TPrefix, TValue> FreezeIPv6(FrozenBitTrieOptions? options) => _v6.Freeze(options);

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
