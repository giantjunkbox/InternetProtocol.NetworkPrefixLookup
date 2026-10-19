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

namespace Bitvantage.InternetProtocol;

/// <summary>
///     A <see cref="NetworkPrefixLookup" /> that serializes write operations with a lock, so that several threads can
///     add and remove prefixes at the same time.
/// </summary>
/// <remarks>
///     <para>
///         Reads are not locked and run concurrently with writes, exactly as in the base class. The lock only stops
///         writers from running at the same time as each other.
///     </para>
///     <para>
///         KNOWN ISSUE: <see cref="NetworkPrefixLookupBase{TValue}.TryAdd(TValue)" /> called with a
///         <see cref="NetworkPrefixKey" /> is not overridden and therefore not locked.
///     </para>
/// </remarks>
public class ConcurrenceNetworkPrefixLookup : NetworkPrefixLookup
{
    /// <summary>Serializes all overridden write operations.</summary>
    private readonly object _lock = new();

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override void Add(NetworkPrefix prefix)
    {
        lock (_lock)
            base.Add(prefix);
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override void Add(NetworkPrefixKey value)
    {
        lock (_lock)
            base.Add(value);
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override void Clear()
    {
        lock (_lock)
            base.Clear();
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override void Remove(NetworkPrefix prefix)
    {
        lock (_lock)
            base.Remove(prefix);
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override bool TryAdd(NetworkPrefix prefix)
    {
        lock (_lock)
            return base.TryAdd(new NetworkPrefixKey(prefix));
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override bool TryRemove(NetworkPrefix prefix)
    {
        lock (_lock)
            return base.TryRemove(prefix);
    }
}

/// <summary>
///     A <see cref="NetworkPrefixLookup{TValue}" /> that serializes write operations with a lock, so that several
///     threads can add and remove prefixes at the same time.
/// </summary>
/// <typeparam name="TValue">The type of the value associated with each prefix.</typeparam>
/// <remarks>
///     <para>
///         Reads are not locked and run concurrently with writes, exactly as in the base class. The lock only stops
///         writers from running at the same time as each other.
///     </para>
///     <para>
///         KNOWN ISSUE: <see cref="NetworkPrefixLookupBase{TValue}.Clear" />,
///         <see cref="NetworkPrefixLookupBase{TValue}.TryAdd(TValue)" /> called with a
///         <see cref="NetworkPrefixKeyValuePair{TValue}" />, and
///         <see cref="NetworkPrefixLookup{TValue}.GetOrAdd" /> are not overridden and therefore not locked.
///     </para>
/// </remarks>
public class ConcurrenceNetworkPrefixLookup<TValue> : NetworkPrefixLookup<TValue>
{
    /// <summary>Serializes all overridden write operations.</summary>
    private readonly object _lock = new();

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override void Add(NetworkPrefix network, TValue? value)
    {
        lock (_lock)
            base.Add(network, value);
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override void Add(NetworkPrefixKeyValuePair<TValue> value)
    {
        lock (_lock)
            base.Add(value);
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override void Remove(NetworkPrefix network)
    {
        lock (_lock)
            base.Remove(network);
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override bool TryAdd(NetworkPrefix network, TValue? value)
    {
        lock (_lock)
            return base.TryAdd(new NetworkPrefixKeyValuePair<TValue>(network, value));
    }

    /// <inheritdoc />
    /// <remarks>Runs under the writer lock.</remarks>
    public override bool TryRemove(NetworkPrefix network)
    {
        lock (_lock)
            return base.TryRemove(network);
    }
}