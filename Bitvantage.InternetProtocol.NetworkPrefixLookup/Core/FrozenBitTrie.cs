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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bitvantage.InternetProtocol;

/// <summary>The layout of a <see cref="FrozenBitTrie{TKey,TPrefix,TValue}" />'s lookup tables.</summary>
public enum FrozenLayout
{
    /// <summary>
    ///     Leaf-pushed tables: a root table over the first bits of the key, and child tables wherever a block still holds
    ///     a more specific prefix. Every entry that is not a child table holds the final answer for its block, so a
    ///     lookup reads only tables. The fastest layout.
    /// </summary>
    LeafPushed,

    /// <summary>
    ///     Compressed tables, after Poptrie: a root table, then nodes that each decide 6 more bits and store their
    ///     children and their runs of equal answers once, found by popcount. A quarter to a tenth of the memory of
    ///     <see cref="LeafPushed" /> on routing tables, for somewhat slower lookups.
    /// </summary>
    Compressed
}

/// <summary>Options for <see cref="BitTrie{TKey,TPrefix,TValue}.Freeze" />.</summary>
public sealed record FrozenBitTrieOptions
{
    /// <summary>The layout of the lookup tables. The default is <see cref="FrozenLayout.LeafPushed" />.</summary>
    public FrozenLayout Layout { get; init; } = FrozenLayout.LeafPushed;

    /// <summary>
    ///     The number of key bits the root table decides, from 1 to 24, and at most the key width. The default is 16 for
    ///     <see cref="FrozenLayout.LeafPushed" /> and 18 for <see cref="FrozenLayout.Compressed" />.
    /// </summary>
    public int? RootBits { get; init; }

    /// <summary>
    ///     The number of key bits each child table decides in the <see cref="FrozenLayout.LeafPushed" /> layout, from 1 to
    ///     16. The default is 8.
    /// </summary>
    public int TableBits { get; init; } = 8;

    /// <summary>
    ///     Keeps a dense copy of the values, so that <see cref="FrozenBitTrie{TKey,TPrefix,TValue}.TryGetMatchValue" />
    ///     reads a smaller array. The default is true; turn it off for large value types when only full matches are used.
    /// </summary>
    public bool ValueOnlyLookups { get; init; } = true;
}

/// <summary>
///     A read-only set of prefixes with faster lookups than <see cref="BitTrie{TKey,TPrefix,TValue}" />, built by
///     <see cref="BitTrie{TKey,TPrefix,TValue}.Freeze" />.
/// </summary>
/// <remarks>
///     <para>
///         Lookups read only the tables and an array of entries in address order; there are no trie nodes. Each entry
///         also records its length and the nearest shorter stored prefix that contains it, which answers the queries
///         about prefixes and <see cref="GetMatches(in TKey)" />.
///     </para>
///     <para>A frozen trie never changes, so any number of threads may read it.</para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TPrefix">The type of the prefix object stored with each prefix.</typeparam>
/// <typeparam name="TValue">The type of the value stored with each prefix.</typeparam>
public sealed class FrozenBitTrie<TKey, TPrefix, TValue> : IReadOnlyBitTrie<TKey, TPrefix, TValue>
    where TKey : struct, IBitKey<TKey>
{
    private struct Entry
    {
        public TPrefix Prefix;
        public TValue Value;
    }

    private struct CompressedNode
    {
        public ulong Children;  // bit s set: slot s leads to a child node
        public ulong Runs;      // bit s set: slot s is an answer that differs from the answer in the previous answer slot
        public int FirstChild;  // index of the child for the lowest child slot; the other children follow it
        public int FirstAnswer; // index in _answers of the first run's answer; the other runs' answers follow it
    }

    // A table entry is an entry index + 1, or 0 for no match, unless TableFlag is set: then the rest is the start of a
    // child table, or the index of a compressed node.
    private const uint TableFlag = 0x8000_0000;
    private const int Stride = 6; // bits per compressed node

    private readonly Entry[] _entries;
    private readonly TValue[]? _values;
    private readonly ushort[] _lengths;
    private readonly int[] _parents; // the nearest shorter stored prefix containing each entry, or -1

    private readonly uint[] _root;
    private readonly int _rootBits;
    private readonly bool _compressed;
    private readonly uint[] _tables = Array.Empty<uint>();
    private readonly int _tableBits;
    private readonly CompressedNode[] _nodes = Array.Empty<CompressedNode>();
    private readonly int[] _answers = Array.Empty<int>(); // entry index, or -1 for no match

    /// <summary>Builds the tables from prefixes in address order, a shorter prefix before a longer one at the same address.</summary>
    internal FrozenBitTrie(IEnumerable<(TKey Bits, int Length, TPrefix Prefix, TValue Value)> items, int count, FrozenBitTrieOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Layout))
            throw new ArgumentException($"Unknown layout {options.Layout}.", nameof(options));
        _compressed = options.Layout == FrozenLayout.Compressed;
        var rootBits = options.RootBits ?? (_compressed ? 18 : 16);
        if (rootBits is < 1 or > 24)
            throw new ArgumentOutOfRangeException(nameof(options), rootBits, "RootBits ranges from 1 to 24.");
        if (options.TableBits is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(options), options.TableBits, "TableBits ranges from 1 to 16.");
        _rootBits = Math.Min(rootBits, TKey.Width);
        _tableBits = options.TableBits;
        Options = options;

        var bits = new TKey[count];
        _entries = new Entry[count];
        _lengths = new ushort[count];
        _parents = new int[count];
        _values = options.ValueOnlyLookups ? new TValue[count] : null;

        // the open chain of prefixes that contain the current one, shortest first
        var chain = new List<int>();
        var i = 0;
        foreach (var (key, length, prefix, value) in items)
        {
            if (i == count)
                throw new InvalidOperationException("The trie changed while it was being frozen.");
            bits[i] = key;
            _lengths[i] = (ushort)length;
            _entries[i] = new Entry { Prefix = prefix, Value = value };
            if (_values != null) _values[i] = value;

            while (chain.Count > 0 && !TKey.MatchesUnderMask(key, bits[chain[^1]], TKey.MaskFor(_lengths[chain[^1]])))
                chain.RemoveAt(chain.Count - 1);
            _parents[i] = chain.Count > 0 ? chain[^1] : -1;
            chain.Add(i);
            i++;
        }

        if (i != count)
            throw new InvalidOperationException("The trie changed while it was being frozen.");

        var builder = new Builder(bits, _lengths, _tableBits);
        _root = new uint[1 << _rootBits];
        builder.FillAnswers(0, count, 0, _rootBits, _root);
        var runs = new Runs(0, count, 0, _rootBits, bits, _lengths);
        while (runs.Next(out var slot, out var lo, out var hi))
        {
            var best = (int)_root[slot] - 1;
            if (_compressed)
            {
                var node = builder.Nodes.Count;
                builder.Nodes.Add(default);
                builder.BuildNode(node, lo, hi, best, _rootBits);
                _root[slot] = TableFlag | (uint)node;
            }
            else
            {
                _root[slot] = TableFlag | (uint)builder.BuildTable(lo, hi, best, _rootBits);
            }
        }

        if (_compressed)
        {
            _nodes = builder.Nodes.ToArray();
            _answers = builder.Answers.ToArray();
        }
        else
        {
            _tables = builder.Tables.ToArray();
        }
    }

    /// <summary>The options the trie was built with.</summary>
    public FrozenBitTrieOptions Options { get; }

    /// <summary>The number of stored prefixes.</summary>
    public int Count => _entries.Length;

    /// <summary>The memory held by the tables and entries, in bytes, computed from the array sizes.</summary>
    public long Bytes =>
        ArrayBytes(_root.Length, sizeof(uint)) + ArrayBytes(_tables.Length, sizeof(uint)) + ArrayBytes(_nodes.Length, Unsafe.SizeOf<CompressedNode>())
        + ArrayBytes(_answers.Length, sizeof(int)) + ArrayBytes(_entries.Length, Unsafe.SizeOf<Entry>())
        + ArrayBytes(_values?.Length ?? 0, Unsafe.SizeOf<TValue>()) + ArrayBytes(_lengths.Length, sizeof(ushort)) + ArrayBytes(_parents.Length, sizeof(int));

    private static long ArrayBytes(int length, int elementSize) => length == 0 ? 0 : 24 + (long)length * elementSize;

    // ---- lookups ---------------------------------------------------------------------------------------------------

    // the entry index of the longest match for a full-width key, or -1
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Find(in TKey key)
    {
        var e = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_root), TKey.GetTopBits(key, _rootBits));
        if ((e & TableFlag) == 0)
            return (int)e - 1;
        return _compressed ? FindCompressed(in key, e) : FindLeafPushed(in key, e);
    }

    private int FindLeafPushed(in TKey key, uint e)
    {
        ref var tables = ref MemoryMarshal.GetArrayDataReference(_tables);
        var offset = _rootBits;
        var tableBits = _tableBits;
        do
        {
            e = Unsafe.Add(ref tables, (int)(e & ~TableFlag) + TKey.GetBits(key, offset, tableBits));
            offset += tableBits;
        } while ((e & TableFlag) != 0);

        return (int)e - 1;
    }

    private int FindCompressed(in TKey key, uint e)
    {
        ref var nodes = ref MemoryMarshal.GetArrayDataReference(_nodes);
        var at = (int)(e & ~TableFlag);
        var offset = _rootBits;
        while (true)
        {
            ref var n = ref Unsafe.Add(ref nodes, at);
            var slot = TKey.GetBits(key, offset, Stride);
            var upToSlot = ulong.MaxValue >> (63 - slot); // bits 0 to slot
            if ((n.Children & (1UL << slot)) == 0)
                return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_answers), n.FirstAnswer + BitOperations.PopCount(n.Runs & upToSlot) - 1);
            at = n.FirstChild + BitOperations.PopCount(n.Children & upToSlot) - 1;
            offset += Stride;
        }
    }

    // the longest stored prefix that contains the prefix (bits, length), or -1: every stored prefix that contains it also
    // contains its first address, so it lies on the chain from that address's longest match
    private int FindForPrefix(in TKey bits, int length)
    {
        var i = Find(TKey.Truncate(bits, length));
        while (i >= 0 && _lengths[i] > length)
            i = _parents[i];
        return i;
    }

    private int FindExact(in TKey bits, int length)
    {
        var i = FindForPrefix(in bits, length);
        return i >= 0 && _lengths[i] == length ? i : -1;
    }

    /// <inheritdoc />
    public bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value)
    {
        var i = Find(in key);
        if (i < 0)
        {
            prefix = default!;
            value = default!;
            return false;
        }

        ref var entry = ref _entries[i];
        prefix = entry.Prefix;
        value = entry.Value;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetMatchValue(in TKey key, out TValue value)
    {
        var i = Find(in key);
        if (i < 0)
        {
            value = default!;
            return false;
        }

        value = _values != null ? _values[i] : _entries[i].Value;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetMatch(in TKey bits, int length, out TPrefix prefix, out TValue value)
    {
        CheckLength(length);
        var i = FindForPrefix(in bits, length);
        if (i < 0)
        {
            prefix = default!;
            value = default!;
            return false;
        }

        ref var entry = ref _entries[i];
        prefix = entry.Prefix;
        value = entry.Value;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetValue(in TKey bits, int length, out TValue value)
    {
        CheckLength(length);
        var i = FindExact(in bits, length);
        if (i < 0)
        {
            value = default!;
            return false;
        }

        value = _entries[i].Value;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetValue(in TKey bits, int length, out TPrefix prefix, out TValue value)
    {
        CheckLength(length);
        var i = FindExact(in bits, length);
        if (i < 0)
        {
            prefix = default!;
            value = default!;
            return false;
        }

        ref var entry = ref _entries[i];
        prefix = entry.Prefix;
        value = entry.Value;
        return true;
    }

    /// <inheritdoc />
    public bool ContainsPrefix(in TKey bits, int length)
    {
        CheckLength(length);
        return FindExact(in bits, length) >= 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<PrefixEntry<TPrefix, TValue>> GetMatches(in TKey key)
    {
        var results = new List<PrefixEntry<TPrefix, TValue>>();
        for (var i = Find(in key); i >= 0; i = _parents[i])
            results.Add(new PrefixEntry<TPrefix, TValue>(_entries[i].Prefix, _entries[i].Value));
        return results;
    }

    /// <inheritdoc />
    public int GetMatches(in TKey key, Span<PrefixEntry<TPrefix, TValue>> destination)
    {
        var count = 0;
        for (var i = Find(in key); i >= 0; i = _parents[i])
        {
            if (count == destination.Length)
                throw new ArgumentException("The destination is too small for the matches.", nameof(destination));
            destination[count++] = new PrefixEntry<TPrefix, TValue>(_entries[i].Prefix, _entries[i].Value);
        }

        return count;
    }

    /// <summary>Returns the stored prefixes in address order, a shorter prefix before a longer one at the same address.</summary>
    public IEnumerator<PrefixEntry<TPrefix, TValue>> GetEnumerator()
    {
        foreach (var entry in _entries)
            yield return new PrefixEntry<TPrefix, TValue>(entry.Prefix, entry.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static void CheckLength(int length)
    {
        if ((uint)length > (uint)TKey.Width)
            throw new ArgumentOutOfRangeException(nameof(length), length, $"Prefix lengths range from 0 to {TKey.Width}.");
    }

    // ---- building --------------------------------------------------------------------------------------------------

    // The runs of entries in [lo, hi) longer than decided + bits, grouped by the slot their next `bits` bits select. The
    // entries come in address order, so the entries below one slot are contiguous: a shorter prefix that starts in a
    // slot's block covers the whole block, and so comes before the longer ones.
    private struct Runs(int lo, int hi, int decided, int bits, TKey[] keys, ushort[] lengths)
    {
        private int _i = lo;

        public bool Next(out int slot, out int runLo, out int runHi)
        {
            var end = decided + bits;
            while (_i < hi && lengths[_i] <= end) _i++;
            if (_i == hi)
            {
                slot = runLo = runHi = 0;
                return false;
            }

            slot = TKey.GetBits(keys[_i], decided, bits);
            runLo = _i;
            for (_i++; _i < hi && lengths[_i] > end && TKey.GetBits(keys[_i], decided, bits) == slot; _i++) { }
            runHi = _i;
            return true;
        }
    }

    private sealed class Builder(TKey[] keys, ushort[] lengths, int tableBits)
    {
        public readonly List<uint> Tables = new();
        public readonly List<CompressedNode> Nodes = new();
        public readonly List<int> Answers = new();

        // Writes the best match for each of the 2^bits slots of the block of keys that share the first `decided` bits:
        // entry index + 1, or 0, starting from what the slots already hold. Only the entries in [lo, hi) that end within
        // the slots' bits count; an entry of length L covers 2^(decided + bits - L) slots. When decided + bits passes the
        // key width, lookups read zeros for the missing bits, which this agrees with. A later entry that overlaps an
        // earlier one lies inside it, because containing prefixes come first, so it wins.
        public void FillAnswers(int lo, int hi, int decided, int bits, Span<uint> slots)
        {
            var end = decided + bits;
            for (var i = lo; i < hi; i++)
            {
                int length = lengths[i];
                if (length > end) continue;
                var first = length == decided ? 0 : TKey.GetBits(keys[i], decided, length - decided) << (end - length);
                slots.Slice(first, 1 << (end - length)).Fill((uint)(i + 1));
            }
        }

        // a leaf-pushed table for the entries [lo, hi) inside the block of keys that share the first `decided` bits;
        // returns its start
        public int BuildTable(int lo, int hi, int best, int decided)
        {
            var start = Tables.Count;
            if (start > int.MaxValue - (1 << tableBits))
                throw new InvalidOperationException("The leaf-pushed tables would exceed 2^31 entries; use a wider TableBits or the compressed layout.");
            CollectionsMarshal.SetCount(Tables, start + (1 << tableBits));
            var slots = CollectionsMarshal.AsSpan(Tables).Slice(start, 1 << tableBits);
            slots.Fill((uint)(best + 1));
            FillAnswers(lo, hi, decided, tableBits, slots);

            var runs = new Runs(lo, hi, decided, tableBits, keys, lengths);
            while (runs.Next(out var slot, out var runLo, out var runHi))
            {
                var child = BuildTable(runLo, runHi, (int)Tables[start + slot] - 1, decided + tableBits);
                Tables[start + slot] = TableFlag | (uint)child;
            }

            return start;
        }

        // fills Nodes[at] for the entries [lo, hi) inside the block of keys that share the first `decided` bits
        public void BuildNode(int at, int lo, int hi, int best, int decided)
        {
            const int slotCount = 1 << Stride;
            Span<uint> slots = stackalloc uint[slotCount];
            Span<int> runLo = stackalloc int[slotCount];
            Span<int> runHi = stackalloc int[slotCount];
            slots.Fill((uint)(best + 1));
            FillAnswers(lo, hi, decided, Stride, slots);

            ulong children = 0;
            var runs = new Runs(lo, hi, decided, Stride, keys, lengths);
            while (runs.Next(out var slot, out var l, out var h))
            {
                children |= 1UL << slot;
                runLo[slot] = l;
                runHi[slot] = h;
            }

            // one answer per run of equal answers, skipping the child slots
            ulong runBits = 0;
            var firstAnswer = Answers.Count;
            var last = uint.MaxValue;
            for (var s = 0; s < slotCount; s++)
            {
                if ((children & (1UL << s)) != 0 || slots[s] == last) continue;
                runBits |= 1UL << s;
                Answers.Add((int)slots[s] - 1);
                last = slots[s];
            }

            // children of one node are contiguous, so reserve their places before building any of them
            var firstChild = Nodes.Count;
            CollectionsMarshal.SetCount(Nodes, firstChild + BitOperations.PopCount(children));
            Nodes[at] = new CompressedNode { Children = children, Runs = runBits, FirstChild = firstChild, FirstAnswer = firstAnswer };

            var child = firstChild;
            for (var s = 0; s < slotCount; s++)
                if ((children & (1UL << s)) != 0)
                    BuildNode(child++, runLo[s], runHi[s], (int)slots[s] - 1, decided + Stride);
        }
    }
}
