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

using System.Buffers.Binary;
using System.Text;

namespace PerformanceTest;

/// <summary>
///     Writes a MaxMind DB file (format 2.0) for benchmarks: an IPv6 tree with 32-bit records, IPv4 networks under
///     <c>::/96</c> as MaxMind keeps them, and no aliases. Each network's record is a map holding an
///     <c>autonomous_system_number</c>, as in GeoLite2-ASN, and networks with the same number share one record.
/// </summary>
/// <remarks>
///     A MaxMind tree holds data only at its leaves, so a network inside another splits the outer one's leaf: the networks
///     go in shortest first, and a longer one turns the leaf it lands in into a node whose two children both keep the
///     outer record. Real MaxMind files are built the same way, so the file holds more networks than went in.
/// </remarks>
public static class MaxMindWriter
{
    private const int Empty = -1; // a record with no data; data records are encoded as -2 - record index

    /// <summary>Writes the networks, each as IPv6 bits and a length, to a file.</summary>
    public static void Write(string path, IEnumerable<(UInt128 Bits, int Length, uint Asn)> networks, string databaseType)
    {
        var left = new List<int> { Empty };
        var right = new List<int> { Empty };
        var records = new Dictionary<uint, int>(); // number to record index
        foreach (var (bits, length, asn) in networks.OrderBy(n => n.Length))
        {
            if (!records.TryGetValue(asn, out var record))
                records.Add(asn, record = records.Count);
            Insert(left, right, bits, length, -2 - record);
        }

        // data section: one map per record, in record order
        var data = new List<byte>();
        var offsets = new int[records.Count];
        foreach (var (asn, record) in records.OrderBy(r => r.Value))
        {
            offsets[record] = data.Count;
            Control(data, 7, 1);
            String(data, "autonomous_system_number");
            UInt(data, 6, asn);
        }

        var nodeCount = left.Count;
        using var file = new BinaryWriter(File.Create(path));
        Span<byte> node = stackalloc byte[8];
        for (var i = 0; i < nodeCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(node, Record(left[i]));
            BinaryPrimitives.WriteUInt32BigEndian(node[4..], Record(right[i]));
            file.Write(node);
        }

        file.Write(new byte[16]); // the data section separator
        file.Write(data.ToArray());

        var metadata = new List<byte>();
        Control(metadata, 7, 9);
        String(metadata, "binary_format_major_version"); UInt(metadata, 5, 2);
        String(metadata, "binary_format_minor_version"); UInt(metadata, 5, 0);
        String(metadata, "build_epoch"); Extended(metadata, 9, 4); metadata.AddRange(Big((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        String(metadata, "database_type"); String(metadata, databaseType);
        String(metadata, "description"); Control(metadata, 7, 1); String(metadata, "en"); String(metadata, "Benchmark data made from the CAIDA RouteViews tables");
        String(metadata, "ip_version"); UInt(metadata, 5, 6);
        String(metadata, "languages"); Extended(metadata, 11, 1); String(metadata, "en");
        String(metadata, "node_count"); UInt(metadata, 6, (uint)nodeCount);
        String(metadata, "record_size"); UInt(metadata, 5, 32);
        file.Write(new byte[] { 0xAB, 0xCD, 0xEF });
        file.Write("MaxMind.com"u8);
        file.Write(metadata.ToArray());

        // a node index, the node count for no data, or a pointer into the data section, which counts from 16 bytes past
        // the end of the tree
        uint Record(int value) => value >= 0 ? (uint)value : value == Empty ? (uint)nodeCount : (uint)(nodeCount + 16 + offsets[-2 - value]);
    }

    private static void Insert(List<int> left, List<int> right, UInt128 bits, int length, int record)
    {
        var node = 0;
        for (var depth = 0; depth < length; depth++)
        {
            var one = ((bits >> (127 - depth)) & 1) != 0;
            var child = one ? right[node] : left[node];
            if (depth == length - 1)
            {
                // the networks come shortest first, so nothing longer lies below this point yet
                if (one) right[node] = record; else left[node] = record;
                return;
            }

            if (child < 0)
            {
                // empty, or the leaf of a shorter network, which both halves of the new node keep
                left.Add(child);
                right.Add(child);
                var created = left.Count - 1;
                if (one) right[node] = created; else left[node] = created;
                child = created;
            }

            node = child;
        }
    }

    // a control byte for a type below 8 and a size up to 28, which is all this writer needs
    private static void Control(List<byte> output, int type, int size) => output.Add((byte)((type << 5) | size));

    private static void Extended(List<byte> output, int type, int size)
    {
        output.Add((byte)size);
        output.Add((byte)(type - 7));
    }

    private static void String(List<byte> output, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length < 29)
            Control(output, 2, bytes.Length);
        else
        {
            Control(output, 2, 29);
            output.Add((byte)(bytes.Length - 29));
        }

        output.AddRange(bytes);
    }

    // an unsigned integer in as few bytes as it needs, as the format asks
    private static void UInt(List<byte> output, int type, uint value)
    {
        var bytes = Big(value).SkipWhile(b => b == 0).ToArray();
        Control(output, type, bytes.Length);
        output.AddRange(bytes);
    }

    private static byte[] Big(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
