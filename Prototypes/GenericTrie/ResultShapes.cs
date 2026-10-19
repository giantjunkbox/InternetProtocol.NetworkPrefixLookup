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

using System.Runtime.CompilerServices;

namespace GenericTrie;

/// <summary>
///     A stored prefix and its value, as returned by <see cref="ResultShapeTrie{TKey,TPrefix,TValue}.FindEntry" />. The
///     trie's internal node derives from this type, so returning it copies nothing.
/// </summary>
public class PrefixEntry<TPrefix, TValue>
{
    public TPrefix Prefix { get; internal set; } = default!;
    public TValue Value { get; internal set; } = default!;
}

/// <summary>A lookup result returned by value.</summary>
public readonly struct PrefixMatch<TPrefix, TValue>
{
    public readonly TPrefix Prefix;
    public readonly TValue Value;
    public readonly bool Found;

    public PrefixMatch(TPrefix prefix, TValue value)
    {
        Prefix = prefix;
        Value = value;
        Found = true;
    }
}

/// <summary>
///     <see cref="CachedKeyTrie{TKey,TPrefix,TValue}" /> with four ways of returning a lookup result over one shared walk,
///     so that only the result shape differs between them.
/// </summary>
public sealed class ResultShapeTrie<TKey, TPrefix, TValue> where TKey : struct, IBitKey<TKey>
{
    private readonly Node _root = new(default, 0);

    public bool TryAdd(in TKey key, int length, TPrefix prefix, TValue value)
    {
        var bits = TKey.Truncate(key, length);

        var parent = _root;
        while (true)
        {
            var next = parent.Children[TKey.IsSet(bits, parent.Split) ? 1 : 0];
            if (next == null || next.Length > length || !TKey.MatchesUnderMask(bits, next.Bits, next.Mask))
                break;
            parent = next;
        }

        if (parent.Length == length)
        {
            if (parent.HasValue)
                return false;

            parent.Prefix = prefix;
            parent.Value = value;
            parent.HasValue = true;
            return true;
        }

        var slot = TKey.IsSet(bits, parent.Split) ? 1 : 0;
        var existing = parent.Children[slot];
        var node = new Node(bits, length) { Prefix = prefix, Value = value, HasValue = true };

        if (existing == null)
        {
            parent.Children[slot] = node;
            return true;
        }

        if (length < existing.Length && TKey.MatchesUnderMask(existing.Bits, bits, node.Mask))
        {
            node.Children[TKey.IsSet(existing.Bits, node.Split) ? 1 : 0] = existing;
            parent.Children[slot] = node;
            return true;
        }

        var common = TKey.CommonPrefixLength(bits, existing.Bits, Math.Min(length, existing.Length));
        var routing = new Node(TKey.Truncate(bits, common), common);
        routing.Children[TKey.IsSet(bits, routing.Split) ? 1 : 0] = node;
        routing.Children[TKey.IsSet(existing.Bits, routing.Split) ? 1 : 0] = existing;
        parent.Children[slot] = routing;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node? FindNode(in TKey key)
    {
        Node? best = null;
        Node? node = _root;

        do
        {
            if (node.HasValue)
                best = node;

            node = node.Children[TKey.IsSet(key, node.Split) ? 1 : 0];
        } while (node != null && TKey.MatchesUnderMask(key, node.Bits, node.Mask));

        return best;
    }

    /// <summary>Shape A: separate out parameters for the prefix and the value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(in TKey key, out TPrefix prefix, out TValue value)
    {
        var node = FindNode(key);
        if (node != null)
        {
            prefix = node.Prefix;
            value = node.Value;
            return true;
        }

        prefix = default!;
        value = default!;
        return false;
    }

    /// <summary>Shape B: one out parameter holding both.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(in TKey key, out PrefixMatch<TPrefix, TValue> match)
    {
        var node = FindNode(key);
        match = node != null ? new PrefixMatch<TPrefix, TValue>(node.Prefix, node.Value) : default;
        return node != null;
    }

    /// <summary>Shape C: a struct returned by value, with a found flag.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PrefixMatch<TPrefix, TValue> GetMatch(in TKey key)
    {
        var node = FindNode(key);
        return node != null ? new PrefixMatch<TPrefix, TValue>(node.Prefix, node.Value) : default;
    }

    /// <summary>Shape D: the matching node itself, through its read-only base type, or null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PrefixEntry<TPrefix, TValue>? FindEntry(in TKey key) => FindNode(key);

    private sealed class Node : PrefixEntry<TPrefix, TValue>
    {
        public readonly TKey Bits;
        public readonly TKey Mask;
        public readonly TKey Split;
        public readonly int Length;
        public readonly Node?[] Children = new Node?[2];
        public bool HasValue;

        public Node(TKey bits, int length)
        {
            Bits = bits;
            Length = length;
            Mask = TKey.MaskFor(length);
            Split = TKey.SplitFor(length);
        }
    }
}
