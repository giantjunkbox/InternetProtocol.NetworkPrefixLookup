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

using Bitvantage.InternetProtocol;

namespace Test.Core;

/// <summary>
///     A 16-bit key with only the required members, relying on the default <see cref="IBitKey{TSelf}.GetTopBits" />. Only
///     this test project can define key types, through InternalsVisibleTo; no other assembly can implement the interface.
/// </summary>
public readonly record struct Bits16(ushort Value) : IBitKey<Bits16>
{
    static int IBitKey<Bits16>.Width => 16;

    private static ushort Mask(int length) => (ushort)(0xFFFF0000u >> length);

    static Bits16 IBitKey<Bits16>.Truncate(in Bits16 key, int length) => new((ushort)(key.Value & Mask(length)));

    static Bits16 IBitKey<Bits16>.MaskFor(int length) => new(Mask(length));

    static bool IBitKey<Bits16>.MatchesUnderMask(in Bits16 key, in Bits16 bits, in Bits16 mask) => (key.Value & mask.Value) == bits.Value;

    static int IBitKey<Bits16>.GetBits(in Bits16 key, int offset, int count) => (int)(((uint)key.Value << (16 + offset)) >> (32 - count));

    static Bits16 IBitKey<Bits16>.SetBits(in Bits16 key, int offset, int count, int value)
    {
        var mask = (0xFFFFu >> (16 - count)) << (16 - offset - count);
        return new((ushort)((key.Value & ~mask) | (((uint)value << (16 - offset - count)) & mask)));
    }
}

/// <summary>An 8-bit key, the narrowest the trie takes, and the only width without shortcut blocks.</summary>
public readonly record struct Bits8(byte Value) : IBitKey<Bits8>
{
    static int IBitKey<Bits8>.Width => 8;

    private static byte Mask(int length) => (byte)(0xFF00 >> length);

    static Bits8 IBitKey<Bits8>.Truncate(in Bits8 key, int length) => new((byte)(key.Value & Mask(length)));

    static Bits8 IBitKey<Bits8>.MaskFor(int length) => new(Mask(length));

    static bool IBitKey<Bits8>.MatchesUnderMask(in Bits8 key, in Bits8 bits, in Bits8 mask) => (key.Value & mask.Value) == bits.Value;

    static int IBitKey<Bits8>.GetBits(in Bits8 key, int offset, int count) => (int)(((uint)key.Value << (24 + offset)) >> (32 - count));

    static Bits8 IBitKey<Bits8>.SetBits(in Bits8 key, int offset, int count, int value)
    {
        var mask = (0xFFu >> (8 - count)) << (8 - offset - count);
        return new((byte)((key.Value & ~mask) | (((uint)value << (8 - offset - count)) & mask)));
    }
}

/// <summary>A key wider than the trie supports, to check that it is refused. Only its width is ever read.</summary>
public readonly struct Bits136 : IBitKey<Bits136>
{
    static int IBitKey<Bits136>.Width => 136;
    static Bits136 IBitKey<Bits136>.Truncate(in Bits136 key, int length) => throw new NotSupportedException();
    static Bits136 IBitKey<Bits136>.MaskFor(int length) => throw new NotSupportedException();
    static bool IBitKey<Bits136>.MatchesUnderMask(in Bits136 key, in Bits136 bits, in Bits136 mask) => throw new NotSupportedException();
    static int IBitKey<Bits136>.GetBits(in Bits136 key, int offset, int count) => throw new NotSupportedException();
    static Bits136 IBitKey<Bits136>.SetBits(in Bits136 key, int offset, int count, int value) => throw new NotSupportedException();
}

/// <summary>A 32-bit key that throws while <see cref="Fail" /> is set, to make a write fail part way.</summary>
public readonly record struct FaultyBits32(uint Value) : IBitKey<FaultyBits32>
{
    [ThreadStatic] public static bool Fail;

    static int IBitKey<FaultyBits32>.Width => 32;

    private static uint Mask(int length) => (uint)((ulong)uint.MaxValue << (32 - length));

    static FaultyBits32 IBitKey<FaultyBits32>.Truncate(in FaultyBits32 key, int length) =>
        Fail ? throw new InjectedFault() : new(key.Value & Mask(length));

    static FaultyBits32 IBitKey<FaultyBits32>.MaskFor(int length) => new(Mask(length));

    static bool IBitKey<FaultyBits32>.MatchesUnderMask(in FaultyBits32 key, in FaultyBits32 bits, in FaultyBits32 mask) => (key.Value & mask.Value) == bits.Value;

    static int IBitKey<FaultyBits32>.GetBits(in FaultyBits32 key, int offset, int count) => (int)((key.Value << offset) >> (32 - count));

    static FaultyBits32 IBitKey<FaultyBits32>.SetBits(in FaultyBits32 key, int offset, int count, int value)
    {
        var mask = (uint.MaxValue >> (32 - count)) << (32 - offset - count);
        return new((key.Value & ~mask) | (((uint)value << (32 - offset - count)) & mask));
    }
}

/// <summary>The exception <see cref="FaultyBits32" /> throws.</summary>
public sealed class InjectedFault : Exception;

/// <summary>Random keys and prefixes of any width, and an independent reference to check lookups against.</summary>
public static class TestKeys
{
    public static TKey RandomKey<TKey>(Random random) where TKey : struct, IBitKey<TKey>
    {
        var key = default(TKey);
        for (var offset = 0; offset < TKey.Width; offset += 8)
            key = TKey.SetBits(key, offset, 8, random.Next(256));
        return key;
    }

    /// <summary>
    ///     Random distinct prefixes. Half are spread over the whole key space, with lengths weighted toward long ones as
    ///     the v2 tests did; half are clustered below a few short prefixes, so that nodes several levels deep fill up.
    /// </summary>
    public static List<(TKey Bits, int Length)> RandomPrefixes<TKey>(int count, int seed) where TKey : struct, IBitKey<TKey>
    {
        var random = new Random(seed);
        var seen = new HashSet<(TKey, int)>();
        var result = new List<(TKey Bits, int Length)>();
        var clusters = Enumerable.Range(0, 4).Select(_ => TKey.Truncate(RandomKey<TKey>(random), Math.Min(12, TKey.Width / 2))).ToArray();
        while (result.Count < count)
        {
            int length;
            TKey bits;
            if (random.Next(2) == 0)
            {
                length = (int)Math.Cbrt(random.Next(0, (TKey.Width + 1) * (TKey.Width + 1) * (TKey.Width + 1)));
                bits = RandomKey<TKey>(random);
            }
            else
            {
                var cluster = clusters[random.Next(clusters.Length)];
                var start = Math.Min(12, TKey.Width / 2);
                length = start + random.Next(TKey.Width - start + 1);
                bits = RandomKey<TKey>(random);
                // keep the cluster's first bits
                for (var offset = 0; offset < start; offset += 4)
                    bits = TKey.SetBits(bits, offset, Math.Min(4, start - offset), TKey.GetBits(cluster, offset, Math.Min(4, start - offset)));
            }

            length = Math.Min(length, TKey.Width);
            bits = TKey.Truncate(bits, length);
            if (seen.Add((bits, length)))
                result.Add((bits, length));
        }

        return result;
    }

    /// <summary>Keys that land inside, next to and outside the prefixes, plus random ones.</summary>
    public static List<TKey> ProbeKeys<TKey>(IEnumerable<(TKey Bits, int Length)> prefixes, int seed) where TKey : struct, IBitKey<TKey>
    {
        var random = new Random(seed);
        var keys = new List<TKey>();
        foreach (var (bits, length) in prefixes)
        {
            keys.Add(bits);
            var inside = RandomKey<TKey>(random);
            // the prefix's bits followed by random bits
            var merged = bits;
            for (var offset = length; offset < TKey.Width; offset++)
                merged = TKey.SetBits(merged, offset, 1, TKey.GetBits(inside, offset, 1));
            keys.Add(merged);
            if (length > 0)
                keys.Add(TKey.SetBits(bits, length - 1, 1, 1 - TKey.GetBits(bits, length - 1, 1))); // the sibling
        }

        for (var i = 0; i < 2000; i++)
            keys.Add(RandomKey<TKey>(random));
        return keys;
    }
}

/// <summary>A deliberately simple longest-prefix match: one dictionary probe per length, longest first.</summary>
public sealed class ReferenceTrie<TKey> where TKey : struct, IBitKey<TKey>
{
    private readonly Dictionary<(TKey Bits, int Length), int> _values = new();
    private List<(TKey Bits, int Length, int Value)>? _inOrder; // built on demand, dropped by every change
    private HashSet<(TKey Bits, int Length)>? _covering;        // likewise

    public int Count => _values.Count;

    public bool Add(TKey bits, int length, int value)
    {
        _inOrder = null;
        _covering = null;
        return _values.TryAdd((TKey.Truncate(bits, length), length), value);
    }

    public bool Remove(TKey bits, int length)
    {
        _inOrder = null;
        _covering = null;
        return _values.Remove((TKey.Truncate(bits, length), length));
    }

    public bool TryGetValue(TKey bits, int length, out int value) => _values.TryGetValue((TKey.Truncate(bits, length), length), out value);

    /// <summary>Every stored prefix that contains the prefix (bits, length), longest first, as (length, value).</summary>
    public List<(int Length, int Value)> Matches(TKey bits, int length)
    {
        var result = new List<(int, int)>();
        for (var l = length; l >= 0; l--)
            if (_values.TryGetValue((TKey.Truncate(bits, l), l), out var value))
                result.Add((l, value));
        return result;
    }

    /// <summary>Every stored prefix inside the prefix (bits, length), in address order, as values: a filter over all of them.</summary>
    public List<int> Subnets(TKey bits, int length)
    {
        var truncated = TKey.Truncate(bits, length);
        var mask = TKey.MaskFor(length);
        return InOrder().Where(p => p.Length >= length && TKey.MatchesUnderMask(p.Bits, truncated, mask)).Select(p => p.Value).ToList();
    }

    /// <summary>
    ///     True when a stored prefix contains the prefix (bits, length) or lies inside it. Something lies inside it exactly
    ///     when it is one of the prefixes that contain a stored prefix, which are collected once.
    /// </summary>
    public bool Overlaps(TKey bits, int length)
    {
        if (_covering == null)
        {
            _covering = new HashSet<(TKey, int)>();
            foreach (var (stored, storedLength) in _values.Keys)
                for (var l = 0; l <= storedLength; l++)
                    _covering.Add((TKey.Truncate(stored, l), l));
        }

        return Matches(bits, length).Count > 0 || _covering.Contains((TKey.Truncate(bits, length), length));
    }

    /// <summary>The stored prefixes in address order, a shorter prefix before a longer one at the same address.</summary>
    public List<(TKey Bits, int Length, int Value)> InOrder()
    {
        if (_inOrder != null)
            return _inOrder;
        var list = _inOrder = _values.Select(p => (p.Key.Bits, p.Key.Length, p.Value)).ToList();
        list.Sort((a, b) =>
        {
            for (var offset = 0; offset < TKey.Width; offset += 8)
            {
                var c = TKey.GetBits(a.Bits, offset, 8).CompareTo(TKey.GetBits(b.Bits, offset, 8));
                if (c != 0) return c;
            }

            return a.Length.CompareTo(b.Length);
        });
        return list;
    }
}
