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

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Bitvantage.InternetProtocol;

/// <summary>
///     Organize IpNetworks into a tree structure that is wide and shallow which allows for rapid searching by using an
///     IPAddress or Network as the key.
/// </summary>
/// <typeparam name="TValue">
///     The type stored for each prefix. It must derive from <see cref="NetworkPrefixKey" />, so every stored item
///     carries its own <see cref="NetworkPrefixKey.Prefix" />.
/// </typeparam>
/// <remarks>
///     <para>
///         Internally the class keeps two independent path-compressed binary tries (radix trees): one for IPv4 and
///         one for IPv6. Each trie is rooted at the zero-length prefix (<c>0.0.0.0/0</c> or <c>::/0</c>).
///         Every node is either a <em>value node</em>, which holds an item that was added, or a <em>routing node</em>,
///         which exists only to join two subtrees. The README calls routing nodes "container nodes".
///     </para>
///     <para>
///         The shape of the trie depends only on the set of prefixes it contains, never on the order in which they
///         were added or removed. Lookups cost one visited node per level, so performance is proportional to the
///         depth of the path to the most specific match. See <c>Documentation/Architecture.md</c> for diagrams,
///         invariants and performance notes.
///     </para>
///     <para>
///         Thread safety: readers never lock. One writer may run concurrently with any number of readers.
///         Concurrent writers must be serialized, for example by using <see cref="ConcurrenceNetworkPrefixLookup" />.
///     </para>
/// </remarks>
public abstract class NetworkPrefixLookupBase<TValue> where TValue : NetworkPrefixKey, new()
{
    /// <summary>
    ///     Gets the number of prefixes that hold a value.
    /// </summary>
    /// <remarks>
    ///     KNOWN ISSUE: the count is not reliable. <see cref="TryAdd(TValue)" /> does not increment it when the new
    ///     node is attached to an empty child slot, <see cref="TryRemove" /> decrements it even when the removed
    ///     prefix was only a routing node, and <see cref="Clear" /> does not reset it.
    /// </remarks>
    public long Count { get; private set; }

    // TODO: the root depends on if this is IPv4 or IPv6...
    // could keep two separate trees, an IPv4 tree and a IPv6 tree and route the requests to the right place

    /// <summary>
    ///     Root of the IPv4 trie. It is always the prefix <c>0.0.0.0/0</c>; it is a routing node unless that exact
    ///     prefix has been added. Replaced wholesale by <see cref="Clear" />.
    /// </summary>
    private Node RootV4 { get; set; } = new(new NetworkPrefix(IPAddress.Any, 0));

    /// <summary>
    ///     Root of the IPv6 trie. It is always the prefix <c>::/0</c>; it is a routing node unless that exact prefix
    ///     has been added. Replaced wholesale by <see cref="Clear" />.
    /// </summary>
    private Node RootV6 { get; set; } = new(new NetworkPrefix(IPAddress.IPv6Any, 0));

    /// <summary>
    ///     Adds an item to the lookup, keyed by its <see cref="NetworkPrefixKey.Prefix" />.
    /// </summary>
    /// <param name="value">The item to add.</param>
    /// <exception cref="ArgumentException">An item with the same prefix already exists.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void Add(TValue value)
    {
        if (!TryAdd(value))
            throw new ArgumentException("An entry with the same key already exists.");
    }

    /// <summary>
    ///     Removes every prefix from both the IPv4 and IPv6 tries.
    /// </summary>
    /// <remarks>
    ///     The roots are swapped for new, empty roots. Readers that are already walking the old tries finish
    ///     against the old data. <see cref="IPAddress.None" /> and <see cref="IPAddress.IPv6None" /> are normalized
    ///     by <see cref="NetworkPrefix" /> to <c>0.0.0.0/0</c> and <c>::/0</c>, so the new roots match the initial
    ///     ones. KNOWN ISSUE: <see cref="Count" /> is not reset.
    /// </remarks>
    public virtual void Clear()
    {
        RootV4 = new Node(new NetworkPrefix(IPAddress.None, 0));
        RootV6 = new Node(new NetworkPrefix(IPAddress.IPv6None, 0));
    }

    /// <summary>
    ///     Selects the trie root that matches the address family of <paramref name="network" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node GetRoot(NetworkPrefix network)
    {
        if (network.Version == IPVersion.IPv4)
            return RootV4;

        return RootV6;
    }

    /// <summary>
    ///     Selects the trie root that matches the address family of <paramref name="ipAddress" />.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The address is neither IPv4 nor IPv6.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node GetRoot(IPAddress ipAddress)
    {
        return ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => RootV4,
            AddressFamily.InterNetworkV6 => RootV6,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    ///     Gets the item with the longest (most specific) prefix that contains <paramref name="ipAddress" />.
    /// </summary>
    /// <param name="ipAddress">The IPv4 or IPv6 address to search for.</param>
    /// <returns>The most specific matching item.</returns>
    /// <exception cref="KeyNotFoundException">No stored prefix contains the address.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetMatch(IPAddress ipAddress)
    {
        if (TryGetMatch(ipAddress, out var result))
            return result;

        throw new KeyNotFoundException();
    }

    /// <summary>
    ///     Gets the item with the longest stored prefix that is equal to, or contains, <paramref name="address" />.
    /// </summary>
    /// <param name="address">The prefix to search for.</param>
    /// <returns>The most specific matching item.</returns>
    /// <exception cref="KeyNotFoundException">No stored prefix equals or contains the prefix.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetMatch(NetworkPrefix address)
    {
        if (TryGetMatch(address, out var result))
            return result;

        throw new KeyNotFoundException();
    }

    /// <summary>
    ///     Gets every item whose prefix contains <paramref name="ipAddress" />.
    /// </summary>
    /// <param name="ipAddress">The IPv4 or IPv6 address to search for.</param>
    /// <returns>The matching items, ordered from the least specific to the most specific prefix.</returns>
    /// <exception cref="KeyNotFoundException">No stored prefix contains the address.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IList<TValue> GetMatches(IPAddress ipAddress)
    {
        if (TryGetMatches(ipAddress, out var result))
            return result;

        throw new KeyNotFoundException();
    }

    /// <summary>
    ///     Gets every item whose prefix is equal to, or contains, <paramref name="address" />.
    /// </summary>
    /// <param name="address">The prefix to search for.</param>
    /// <returns>The matching items, ordered from the least specific to the most specific prefix.</returns>
    /// <exception cref="KeyNotFoundException">No stored prefix equals or contains the prefix.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IList<TValue> GetMatches(NetworkPrefix address)
    {
        if (TryGetMatches(address, out var result))
            return result;

        throw new KeyNotFoundException();
    }


    /// <summary>
    ///     Removes the item stored under exactly <paramref name="network" />.
    /// </summary>
    /// <param name="network">The exact prefix to remove.</param>
    /// <exception cref="KeyNotFoundException">The prefix is not present in the trie.</exception>
    public virtual void Remove(NetworkPrefix network)
    {
        if (!TryRemove(network))
            throw new KeyNotFoundException();
    }

    /// <summary>
    ///     Writes a Graphviz DOT description of one trie next to <paramref name="filename" /> and renders it to SVG
    ///     with the Graphviz <c>dot</c> tool.
    /// </summary>
    /// <param name="filename">
    ///     Output path. Only its directory and file name without extension are used; the method writes
    ///     <c>&lt;name&gt;.dot</c> and <c>&lt;name&gt;.svg</c> in that directory.
    /// </param>
    /// <param name="version">Which trie to render.</param>
    /// <param name="displayText">Optional text appended to the label of each value node.</param>
    /// <param name="dotPath">
    ///     Path to the <c>dot</c> executable. Defaults to the standard Windows install location
    ///     <c>C:\Program Files\Graphviz\bin\dot.exe</c>, so non-Windows callers must pass it.
    /// </param>
    /// <exception cref="ArgumentException">The directory of <paramref name="filename" /> cannot be determined.</exception>
    /// <exception cref="FileNotFoundException">The <c>dot</c> executable does not exist.</exception>
    /// <exception cref="Exception"><c>dot</c> failed to start or exited with a non-zero code.</exception>
    /// <remarks>This is a diagnostic helper. It is not optimized and should not be used on a hot path.</remarks>
    public void ToDotSvgTree(string filename, IPVersion version,  Func<TValue, string>? displayText = null, string? dotPath = null)
    {
        var fullPath = Path.GetFullPath(filename);
        var workingDirectory = Path.GetDirectoryName(fullPath);

        if (workingDirectory == null)
            throw new ArgumentException($"Absolute path could not be determined: {filename}", nameof(filename));

        var fullDotPath = dotPath ?? @"C:\Program Files\Graphviz\bin\dot.exe";
        if (!File.Exists(fullDotPath))
            throw new FileNotFoundException($"Could not find dot.exe: {fullDotPath}");

        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var dotFilename = Path.Combine(workingDirectory, $"{fileName}.dot");
        var svgFilename = Path.Combine(workingDirectory, $"{fileName}.svg");

        File.WriteAllText(dotFilename, ToDotTree(version, displayText));

        var dotProcess = Process.Start(new ProcessStartInfo(dotPath ?? @"C:\Program Files\Graphviz\bin\dot.exe", $"-Tsvg {dotFilename} -o{svgFilename}") { CreateNoWindow = false, WorkingDirectory = workingDirectory });

        if (dotProcess == null)
            throw new Exception("dot failed to start");

        dotProcess.WaitForExit();

        if (dotProcess.ExitCode != 0)
            throw new Exception($"dot failed with exit code {dotProcess.ExitCode}");
    }


    /// <summary>
    ///     A node paired with the relation that led to it. <see cref="ToDotTree" /> uses the parent to highlight the
    ///     bit that decided which child slot the node occupies.
    /// </summary>
    /// <param name="Node">The trie node.</param>
    /// <param name="Parent">The parent relation, or <see langword="null" /> for the root.</param>
    private record NodeRelation(Node Node, NodeRelation? Parent)
    {
    }

    /// <summary>
    ///     Builds a Graphviz DOT description of one trie.
    /// </summary>
    /// <param name="version">Which trie to describe.</param>
    /// <param name="displayText">Optional text appended to the label of each value node.</param>
    /// <returns>The DOT source text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version" /> is not IPv4 or IPv6.</exception>
    /// <remarks>
    ///     The root is drawn with an orange header. Value nodes have a light blue body and routing nodes a green
    ///     body. Each node shows its address in binary twice: the bits beyond the prefix length are gray, and in the
    ///     first copy the bit that selected the node's child slot in its parent is underlined. An empty arrowhead
    ///     marks the slot 0 child and a filled arrowhead marks the slot 1 child.
    /// </remarks>
    public string ToDotTree(IPVersion version, Func<TValue, string>? displayText = null)
    {
        var root = version switch
        {
            IPVersion.IPv4 => RootV4,
            IPVersion.IPv6 => RootV6,
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };

        // dot -Tsvg test123.dot -otest123.svg

        // depth-first traversal: start with the root node on the stack
        var stack = new Stack<NodeRelation>();
        stack.Push(new NodeRelation(root, null));

        var stringBuilder = new StringBuilder();

        // write dot graph header
        stringBuilder.AppendLine("digraph \"Network Lookup Tree\" {");
        stringBuilder.AppendLine("\tgraph [ overlap=true ranksep=1.3 ]");
        stringBuilder.AppendLine("\tnode [shape=plaintext fontname=\"Consolas\" fontsize=\"8\"]");
        stringBuilder.AppendLine();

        while (stack.TryPop(out var currentNode))
        {
            var isRoot = root == currentNode.Node;

            stringBuilder.AppendLine($"\t\"{currentNode.Node.ValuePair.Prefix}\" [");

            if (isRoot)
                stringBuilder.AppendLine("\t\troot=true");

            // rank by prefix length so that nodes with the same prefix length line up horizontally
            stringBuilder.AppendLine($"\t\trank={currentNode.Node.ValuePair.Prefix.Length + 1}");
            stringBuilder.AppendLine("\t\tlabel=<");
            stringBuilder.AppendLine($"\t\t\t<table border=\"0\" cellborder=\"0\" cellspacing=\"0\" cellpadding=\"0\"{(isRoot ? " bgcolor=\"orange\"" : "")}>");

            var nodeText = string.Empty;
            if (displayText != null && currentNode.Node.HasValue)
                nodeText = $" {displayText.Invoke((TValue)currentNode.Node.ValuePair)}";

            stringBuilder.AppendLine($"\t\t\t\t<tr><td align=\"left\"{(isRoot ? " border=\"1\"" : "")}><b>{currentNode.Node.ValuePair.Prefix.Address}/{currentNode.Node.ValuePair.Prefix.Length}{nodeText}</b></td></tr>");

            // value nodes are light blue, routing nodes are green
            stringBuilder.AppendLine($"\t\t\t\t\t<tr><td><table cellborder=\"0\" cellspacing=\"0\" cellpadding=\"0\" bgcolor=\"{(currentNode.Node.HasValue ? "lightblue" : "yellowgreen")}\" >");

            stringBuilder.Append("\t\t\t\t\t\t<tr><td align=\"left\">");
            WriteAddress(currentNode, true);
            stringBuilder.Append("</td></tr>");

            stringBuilder.AppendLine();

            stringBuilder.Append("\t\t\t\t\t\t<tr><td align=\"left\">");
            WriteAddress(currentNode, false);
            stringBuilder.Append("</td></tr>");

            stringBuilder.AppendLine();
            stringBuilder.AppendLine("\t\t\t\t\t</table></td></tr>");
            stringBuilder.AppendLine("\t\t\t\t</table>>");
            stringBuilder.AppendLine("\t\t\t];");

            stringBuilder.AppendLine();

            for (var i = 0; i <= 1; i++)
                if (currentNode.Node.Children[i] != null)
                {
                    // link this node to its child; slot 0 gets an empty arrowhead, slot 1 a filled one
                    stringBuilder.AppendLine($"\t\"{currentNode.Node.ValuePair.Prefix}\" -> \"{currentNode.Node.Children[i].ValuePair.Prefix}\" [ arrowhead=\"{(i == 0 ? "empty" : "normal")}\" ]");

                    // push this node's children to the stack
                    stack.Push(new NodeRelation(currentNode.Node.Children[i], currentNode));
                }
        }

        // write dot graph footer
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("}");

        return stringBuilder.ToString();

        // Writes the node's address as binary digits, with '.' between IPv4 octets and ':' between IPv6 groups.
        // Bits past the prefix length are gray. When highlightSplitBit is set, the bit at the parent's prefix length
        // (the bit that chose this node's child slot) is underlined and bold.
        void WriteAddress(NodeRelation currentNode, bool highlightSplitBit)
        {
            var bits = currentNode.Node.NetworkBits;

            for (var i = 0; i < currentNode.Node.AddressLength; i++)
            {
                if (i == currentNode.Node.Prefix)
                    stringBuilder.Append("<font color=\"gray\">");

                if (i % 8 == 0 && i > 0 && version == IPVersion.IPv4)
                    stringBuilder.Append(".");

                if (i % 16 == 0 && i > 0 && version == IPVersion.IPv6)
                    stringBuilder.Append(":");

                if (highlightSplitBit && currentNode.Parent != null && i == currentNode.Parent.Node.Prefix)
                    stringBuilder.Append("<U><B>");

                // address bit i, counted from the most significant bit, lives at UInt128 bit (AddressLength - 1 - i)
                if ((bits & UInt128.One << currentNode.Node.AddressLength - 1 - i) > UInt128.Zero)
                    stringBuilder.Append('1');
                else
                    stringBuilder.Append('0');

                if (highlightSplitBit && currentNode.Parent != null && i == currentNode.Parent.Node.Prefix)
                    stringBuilder.Append("</B></U>");
            }

            if (currentNode.Node.Prefix != currentNode.Node.AddressLength)
                stringBuilder.Append("</font>");
        }
    }

    /// <summary>
    ///     Renders one trie as indented text, one node per line.
    /// </summary>
    /// <param name="version">Which trie to render.</param>
    /// <param name="displayText">Optional text appended to each non-root line.</param>
    /// <returns>The text tree, without a trailing line break.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version" /> is not IPv4 or IPv6.</exception>
    /// <remarks>
    ///     <para>
    ///         Routing nodes are prefixed with <c>@</c>. The number in brackets after each non-root node is the
    ///         child slot it occupies in its parent: 0 for the lower half of the parent's address space and 1 for the
    ///         upper half. Children are listed slot 0 first. The unit tests compare against this exact format, so
    ///         changing it breaks most tests.
    ///     </para>
    ///     <para>
    ///         KNOWN ISSUE: the trailing line break is removed by deleting two characters, which assumes
    ///         <see cref="Environment.NewLine" /> is <c>\r\n</c>. On Linux and macOS the last character of the
    ///         final line is removed instead, which makes the text tree unit tests fail on those platforms.
    ///     </para>
    /// </remarks>
    public string ToTextTree(IPVersion version, Func<TValue, string>? displayText = null)
    {
        // depth-first traversal: start with the root node on the stack
        var stack = new Stack<TreeNode>();

        var root = version switch
        {
            IPVersion.IPv4 => RootV4,
            IPVersion.IPv6 => RootV6,
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };

        var rootNode = new TreeNode(root, 0, -1);
        stack.Push(rootNode);

        // in order to output the tree elements correctly, the number of remaining children for each level must be known
        // each time a node is popped off the stack record the number of child elements
        // each time a child element is printed out, decrement the remaining child count
        // 129 levels covers the deepest possible IPv6 trie (/0 through /128)
        var remainingChildren = new int[129];
        remainingChildren[0] = rootNode.ChildCount;

        var stringBuilder = new StringBuilder();

        while (stack.TryPop(out var treeNode))
        {
            remainingChildren[treeNode.Level] = treeNode.ChildCount;

            // node level 0 is not indented, all other nodes are
            if (treeNode.Level == 0)
            {
                stringBuilder.AppendLine($"{(treeNode.Node.HasValue ? "" : "@")}{treeNode.Node.ValuePair.Prefix}");
            }
            else
            {
                remainingChildren[treeNode.Level - 1]--;

                // output the branches for all parent nodes
                for (var i = 1; i < treeNode.Level; i++)
                    stringBuilder.Append(remainingChildren[i - 1] > 0 ? "│  " : "   ");

                stringBuilder.Append(remainingChildren[treeNode.Level - 1] > 0 ? "├──" : "└──");

                // output this node
                if (displayText != null)
                    stringBuilder.AppendLine($"{(treeNode.Node.HasValue ? "" : "@")}{treeNode.Node.ValuePair.Prefix}[{treeNode.Index}] {displayText.Invoke((TValue)treeNode.Node.ValuePair)}");
                else
                    stringBuilder.AppendLine($"{(treeNode.Node.HasValue ? "" : "@")}{treeNode.Node.ValuePair.Prefix}[{treeNode.Index}]");
            }

            // push this node's children to the stack, slot 1 first so that slot 0 is popped and printed first
            for (var i = 1; i >= 0; i--)
                if (treeNode.Node.Children[i] != null)
                    stack.Push(new TreeNode(treeNode.Node.Children[i], treeNode.Level + 1, i));
        }

        // remove the extra line break at the end
        // KNOWN ISSUE: assumes a two character "\r\n" line break; see the remarks above
        if (stringBuilder.Length > 0)
            stringBuilder.Remove(stringBuilder.Length - 2, 2);

        return stringBuilder.ToString();
    }

    /// <summary>
    ///     Converts an address into the same right-aligned integer layout used by
    ///     <see cref="NetworkPrefix.NetworkBits" />.
    /// </summary>
    /// <param name="address">An IPv4 or IPv6 address.</param>
    /// <returns>
    ///     The address as an integer in network bit order: an IPv4 address occupies the low 32 bits, and an IPv6
    ///     address uses all 128 bits.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The address is neither 4 nor 16 bytes long.</exception>
    /// <remarks>
    ///     PERF: this runs once per <see cref="TryGetMatch(IPAddress, out TValue)" /> and
    ///     <see cref="TryGetMatches(IPAddress, out IList{TValue})" /> call. It allocates the byte array returned by
    ///     <see cref="IPAddress.GetAddressBytes" /> plus one temporary 8-byte array per 64-bit half, which measured
    ///     64 bytes of garbage per IPv4 lookup. The bytes are reversed because <see cref="BitConverter" /> reads
    ///     little-endian on the supported platforms while addresses are big-endian.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private UInt128 ToUInt128(IPAddress address)
    {
        var addressBytes = address.GetAddressBytes();

        if (addressBytes.Length == 4)
            return new UInt128(0, BitConverter.ToUInt64(new[] { addressBytes[3], addressBytes[2], addressBytes[1], addressBytes[0], (byte)0, (byte)0, (byte)0, (byte)0 }));

        if (addressBytes.Length == 16)
            return new UInt128(BitConverter.ToUInt64(new[] { addressBytes[7], addressBytes[6], addressBytes[5], addressBytes[4], addressBytes[3], addressBytes[2], addressBytes[1], addressBytes[0] }), BitConverter.ToUInt64(new[] { addressBytes[15], addressBytes[14], addressBytes[13], addressBytes[12], addressBytes[11], addressBytes[10], addressBytes[9], addressBytes[8] }));

        throw new ArgumentOutOfRangeException(nameof(address), $"IP address must be either 32 bits or 128 bits long. Specified address '{address}' is {addressBytes.Length * 8} bytes long");
    }

    /// <summary>
    ///     Adds an item to the lookup if no item with the same prefix exists.
    /// </summary>
    /// <param name="valuePair">The item to add, keyed by its <see cref="NetworkPrefixKey.Prefix" />.</param>
    /// <returns>
    ///     <see langword="true" /> if the item was added; <see langword="false" /> if a value node with the same
    ///     prefix already exists.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The insert first finds the deepest existing node that contains the new prefix, called the parent below.
    ///         One of four cases then applies:
    ///     </para>
    ///     <list type="number">
    ///         <item>The parent has exactly the new prefix. A routing node is converted into a value node; a value node means the prefix is a duplicate.</item>
    ///         <item>The parent's child slot for the new prefix is empty. The new node becomes a leaf in that slot.</item>
    ///         <item>The new prefix contains the existing child in that slot. The new node is inserted between the parent and that child.</item>
    ///         <item>Otherwise the new prefix and the existing child are siblings. A routing node for the smallest network that contains both is inserted, with both as its children.</item>
    ///     </list>
    ///     <para>
    ///         New nodes are fully built before a single reference write links them into the trie, so concurrent
    ///         readers see either the old shape or the new shape.
    ///     </para>
    ///     <para>
    ///         KNOWN ISSUE: case 2 does not increment <see cref="Count" />.
    ///     </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual bool TryAdd(TValue valuePair)
    {
        // find the deepest node that contains the new prefix; it is the last entry in the history
        var nodeHistory = WalkTree(valuePair.Prefix);
        var parent = nodeHistory[^1].Node;


        // case 1: the new network already exists
        if (valuePair.Prefix == parent.ValuePair.Prefix)
        {
            // and it is a value node, then the requested network already exists
            if (parent.HasValue)
                return false;

            // but if the new network is a routing node
            // convert the routing node to a value node
            // the prefix is unchanged, so the cached bit fields on the node keep the same values
            // NOTE: ValuePair is written before HasValue so that a reader that sees HasValue == true also sees the
            // new value. Nothing enforces this ordering on weakly ordered CPUs; see Documentation/Architecture.md
            parent.ValuePair = valuePair;
            parent.HasValue = true;

            Count++;
            return true;
        }

        var targetSlot = parent.GetSlot(valuePair.Prefix);

        // case 2: if the parents target slot is empty, add the node directly
        if (parent.Children[targetSlot] == null)
        {
            // KNOWN ISSUE: Count is not incremented on this path
            parent.Children[targetSlot] = new Node(valuePair, true);
            return true;
        }

        // the new node needs to be added as a child of an existing node; but the existing node already has a child
        var newNode = new Node(valuePair, true);
        var existingNode = parent.Children[targetSlot];

        // case 3: if the new node has a smaller prefix then the existing node, the new node can be inserted directly above the existing node
        if (newNode.ValuePair.Prefix.Length < existingNode.ValuePair.Prefix.Length && newNode.ValuePair.Prefix.Contains(existingNode.ValuePair.Prefix))
        {
            // link the existing node under the new node before publishing the new node to readers
            newNode.Children[newNode.GetSlot(existingNode.ValuePair.Prefix)] = existingNode;
            parent.Children[targetSlot] = newNode;

            Count++;
            return true;
        }

        // case 4: the new node and the existing node are siblings, so a routing node needs to be inserted directly
        // above the existing node. The routing node will cover both prefixes and contain both the existing node and
        // the new node. (The existing node cannot contain the new node, otherwise WalkTree would have descended into it.)

        // calculate the smallest network that contains both the existing node and the new node
        var routingNetwork = valuePair.Prefix.GetContainingNetwork(existingNode.ValuePair.Prefix);
        var routingNode = new Node(routingNetwork);

        // the routing node prefix is longer than the parent prefix, because both children fall in the same half of the
        // parent's address space; the two children always land in different slots of the routing node
        routingNode.Children[routingNode.GetSlot(newNode.ValuePair.Prefix)] = newNode;
        routingNode.Children[routingNode.GetSlot(existingNode.ValuePair.Prefix)] = existingNode;

        // re-point the parent node from the existing node to the new routing node
        // this single reference write publishes the whole change to readers
        parent.Children[targetSlot] = routingNode;

        Count++;
        return true;
    }

    /// <summary>
    ///     Finds the item with the longest (most specific) prefix that contains <paramref name="ipAddress" />.
    /// </summary>
    /// <param name="ipAddress">The IPv4 or IPv6 address to search for.</param>
    /// <param name="result">The most specific matching item, or <see langword="null" /> if there is none.</param>
    /// <returns><see langword="true" /> if a matching prefix was found.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The address is neither IPv4 nor IPv6.</exception>
    /// <remarks>
    ///     <para>
    ///         This is the primary hot path. Starting at the root, the loop remembers the last value node it passed,
    ///         steps to the child selected by the address bit just past the current node's prefix, and stops when the
    ///         child is missing or its prefix does not contain the address. Because the trie is path compressed, a
    ///         child can skip several bits, so the full masked comparison is required at every step.
    ///     </para>
    ///     <para>
    ///         PERF: each step reads the child array and then the child node, so the cost is roughly two dependent
    ///         memory loads per level. The address conversion allocates; see <see cref="ToUInt128" />.
    ///     </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(IPAddress ipAddress, [NotNullWhen(true)] out TValue? result)
    {
        Node? lastValueNode = null;

        // the root always contains the address (it is /0), so it is visited without a containment check
        var currentNode = GetRoot(ipAddress);
        var addressBits = ToUInt128(ipAddress);

        do
        {
            // remember the deepest value node seen so far; it is the longest match if the walk ends here
            if (currentNode.HasValue)
                lastValueNode = currentNode;

            // step to the child on the side of the address bit that follows this node's prefix
            currentNode = currentNode.Children[currentNode.GetSlot(addressBits)];

            // keep going while the child exists and its prefix contains the address
        } while (currentNode != null && currentNode.NetworkBits == (addressBits & currentNode.NetworkMaskBits));

        if (lastValueNode != null)
        {
            result = (TValue)lastValueNode.ValuePair;
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    ///     Finds the item with the longest stored prefix that is equal to, or contains, <paramref name="address" />.
    /// </summary>
    /// <param name="address">The prefix to search for.</param>
    /// <param name="result">The most specific matching item, or <see langword="null" /> if there is none.</param>
    /// <returns><see langword="true" /> if a matching prefix was found.</returns>
    /// <remarks>
    ///     This is the same walk as <see cref="TryGetMatch(IPAddress, out TValue)" />, with an extra stop when a
    ///     node's prefix is longer than <paramref name="address" />, because a more specific prefix cannot contain
    ///     it. It does not allocate. A /32 or /128 prefix behaves like a single address lookup.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatch(NetworkPrefix address, [NotNullWhen(true)] out TValue? result)
    {
        Node? lastValueNode = null;

        var currentNode = GetRoot(address);
        var addressBits = address.NetworkBits;

        do
        {
            if (currentNode.HasValue)
                lastValueNode = currentNode;

            currentNode = currentNode.Children[currentNode.GetSlot(addressBits)];

            // keep going while the child exists, contains the searched prefix, and is not more specific than it
        } while (currentNode != null && currentNode.NetworkBits == (addressBits & currentNode.NetworkMaskBits) && currentNode.Prefix <= address.Length);

        if (lastValueNode != null)
        {
            result = (TValue)lastValueNode.ValuePair;
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    ///     Finds every item whose prefix contains <paramref name="ipAddress" />.
    /// </summary>
    /// <param name="ipAddress">The IPv4 or IPv6 address to search for.</param>
    /// <param name="result">
    ///     The matching items, ordered from the least specific to the most specific prefix. The list is empty when
    ///     nothing matches.
    /// </param>
    /// <returns><see langword="true" /> if at least one prefix matched.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The address is neither IPv4 nor IPv6.</exception>
    /// <remarks>
    ///     PERF: allocates a new list on every call, and reads the containment fields through
    ///     <see cref="Node.ValuePair" /> rather than the copies cached on the node, which adds two dependent loads per
    ///     level compared with <see cref="TryGetMatch(IPAddress, out TValue)" />.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatches(IPAddress ipAddress, out IList<TValue> result)
    {
        result = new List<TValue>();

        var currentNode = GetRoot(ipAddress);
        var addressBits = ToUInt128(ipAddress);

        do
        {
            // every value node on the path contains the address, so each one is a match
            if (currentNode.HasValue)
                result.Add((TValue)currentNode.ValuePair);

            var childSlot = currentNode.GetSlot(addressBits);
            currentNode = currentNode.Children[childSlot];

            // PERF: uses ValuePair.Prefix.* instead of the cached NetworkBits/NetworkMaskBits fields on the node
        } while (currentNode != null && currentNode.ValuePair.Prefix.NetworkBits == (addressBits & currentNode.ValuePair.Prefix.NetworkMaskBits));


        return result.Count > 0;
    }

    /// <summary>
    ///     Finds every item whose prefix is equal to, or contains, <paramref name="address" />.
    /// </summary>
    /// <param name="address">The prefix to search for.</param>
    /// <param name="result">
    ///     The matching items, ordered from the least specific to the most specific prefix. The list is empty when
    ///     nothing matches.
    /// </param>
    /// <returns><see langword="true" /> if at least one prefix matched.</returns>
    /// <remarks>
    ///     PERF: allocates a new list on every call and, like
    ///     <see cref="TryGetMatches(IPAddress, out IList{TValue})" />, does not use the fields cached on the node.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMatches(NetworkPrefix address, out IList<TValue> result)
    {
        result = new List<TValue>();

        var currentNode = GetRoot(address);
        var addressBits = address.NetworkBits;

        do
        {
            if (currentNode.HasValue)
                result.Add((TValue)currentNode.ValuePair);

            var childSlot = currentNode.GetSlot(addressBits);
            currentNode = currentNode.Children[childSlot];

            // PERF: uses ValuePair.Prefix.* instead of the cached fields on the node
        } while (currentNode != null && currentNode.ValuePair.Prefix.NetworkBits == (addressBits & currentNode.ValuePair.Prefix.NetworkMaskBits) && currentNode.ValuePair.Prefix.Length <= address.Length);

        return result.Count > 0;
    }

    /// <summary>
    ///     Removes the item stored under exactly <paramref name="network" />, if present.
    /// </summary>
    /// <param name="network">The exact prefix to remove.</param>
    /// <returns><see langword="true" /> if a node with that prefix was found.</returns>
    /// <remarks>
    ///     <para>
    ///         The node is demoted to a routing node and its value reference is released. The method then walks back
    ///         up the recorded path and prunes routing nodes that are no longer needed: a routing node with no
    ///         children is unlinked, and a routing node with one child is replaced by that child. The walk stops at
    ///         the first routing node that still has two children. The root is never removed.
    ///     </para>
    ///     <para>
    ///         KNOWN ISSUE: the method does not check <see cref="Node.HasValue" /> before removing. Passing the prefix
    ///         of a routing node, including an empty root, returns <see langword="true" /> and decrements
    ///         <see cref="Count" />, which can make the count negative.
    ///     </para>
    ///     <para>
    ///         KNOWN ISSUE: the demoted node's value is replaced with a plain <see cref="NetworkPrefixKey" />. A
    ///         concurrent reader that already saw <see cref="Node.HasValue" /> as true may then cast that key to
    ///         <typeparamref name="TValue" /> and throw <see cref="InvalidCastException" /> when
    ///         <typeparamref name="TValue" /> is a derived type such as <see cref="NetworkPrefixKeyValuePair{TValue}" />.
    ///     </para>
    /// </remarks>
    public virtual bool TryRemove(NetworkPrefix network)
    {
        // record the path from the root to the deepest node that contains the network
        var nodeHistory = WalkTree(network);

        // check to see if the requested network exists
        var nodeToRemove = nodeHistory[^1];
        if (nodeToRemove.Node.ValuePair.Prefix != network)
            return false;

        // KNOWN ISSUE: there is no HasValue check here, so removing a routing node "succeeds"

        // set the node to remove as routing node
        // clear the value to avoid holding a reference
        nodeToRemove.Node.HasValue = false;
        nodeToRemove.Node.ValuePair = new NetworkPrefixKey(nodeToRemove.Node.ValuePair.Prefix);

        // walk up the tree collapsing dead routing nodes
        // index 0 is the root, which is never pruned
        for (var index = nodeHistory.Count - 1; index >= 1; index--)
        {
            var currentNode = nodeHistory[index];

            // if the node is a value node skip it
            if (currentNode.Node.HasValue)
                continue;

            var childCount = (currentNode.Node.Children[0] == null ? 0 : 1) + (currentNode.Node.Children[1] == null ? 0 : 1);

            // if the node has no children unlink it
            // its parent may now have a single child, so keep walking up
            if (childCount == 0)
            {
                var parent = nodeHistory[index - 1];
                parent.Node.Children[currentNode.ParentSlot] = null;
                continue;
            }

            // if the node has one child, remove the node and splice its child to its parent
            // the child is still within the same half of the parent, so it takes over the same slot
            if (childCount == 1)
            {
                var parent = nodeHistory[index - 1];
                var newChildSlot = currentNode.ParentSlot;
                var childSlot = currentNode.Node.Children[0] != null ? 0 : 1;
                var child = currentNode.Node.Children[childSlot];
                parent.Node.Children[newChildSlot] = child;
                continue;
            }

            // once we hit a routing node that does not require pruning, there is no need to go any further
            Count--;
            return true;
        }

        // once we hit the root, the tree has been pruned
        Count--;
        return true;
    }

    /// <summary>
    ///     Records the path from the root down to the deepest node whose prefix contains <paramref name="network" />
    ///     and is no longer than it.
    /// </summary>
    /// <param name="network">The prefix being inserted or removed.</param>
    /// <returns>
    ///     The visited nodes in order, starting with the root. The last entry is either the node with exactly
    ///     <paramref name="network" /> or the node under which it would be inserted. Each entry also records the
    ///     child slot it occupies in the previous entry, which <see cref="TryRemove" /> needs to unlink or splice
    ///     nodes. The slot recorded for the root is meaningless.
    /// </returns>
    /// <remarks>
    ///     PERF: allocates a list on every insert and remove. Only the write path uses it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private List<NodeHistory> WalkTree(NetworkPrefix network)
    {
        var networkBits = network.NetworkBits;
        var root = GetRoot(network);
        NodeHistory lastNode = new(root, root.GetSlot(networkBits));

        var nodeHistory = new List<NodeHistory> { lastNode };

        // walk the tree until...
        // the next node has a more specific prefix (larger) than the network, or
        // the next node does not contain the network, or
        // the current node does not have a child set in the child slot
        // record each node that is visited in the history list

        do
        {
            var nextNodeSlot = lastNode.Node.GetSlot(networkBits);
            var nextNode = lastNode.Node.Children[nextNodeSlot];

            if (nextNode == null || nextNode.Prefix > network.Length || (networkBits & nextNode.NetworkMaskBits) != nextNode.NetworkBits)
                break;

            lastNode = new NodeHistory(nextNode, nextNodeSlot);
            nodeHistory.Add(lastNode);

            // continue until the current node has a larger mask or the current node has no children
        } while (true);

        return nodeHistory;
    }

    /// <summary>
    ///     Traversal state used by <see cref="ToTextTree" />.
    /// </summary>
    private readonly struct TreeNode
    {
        /// <summary>The trie node being printed.</summary>
        internal readonly Node Node;

        /// <summary>Depth of the node; the root is level 0.</summary>
        internal readonly int Level;

        /// <summary>The child slot the node occupies in its parent, or -1 for the root.</summary>
        internal readonly int Index;

        /// <summary>Number of non-null children, from 0 to 2.</summary>
        internal int ChildCount => (Node.Children[0] == null ? 0 : 1) + (Node.Children[1] == null ? 0 : 1);

        /// <summary>Creates traversal state for one node.</summary>
        internal TreeNode(Node node, int level, int index)
        {
            Node = node;
            Level = level;
            Index = index;
        }
    }

    /// <summary>
    ///     One step of the path recorded by <see cref="WalkTree" />.
    /// </summary>
    [DebuggerDisplay("{Node.ValuePair.Prefix.ToString(),nq}")]
    private struct NodeHistory
    {
        /// <summary>The visited node.</summary>
        public Node Node;

        /// <summary>The slot (0 or 1) that <see cref="Node" /> occupies in the previous node on the path.</summary>
        public readonly int ParentSlot;

        /// <summary>Creates a path entry.</summary>
        public NodeHistory(Node node, int parentSlot)
        {
            Node = node;
            ParentSlot = parentSlot;
        }
    }

    /// <summary>
    ///     A node in the binary trie. A node is a value node when <see cref="HasValue" /> is true and a routing node
    ///     otherwise.
    /// </summary>
    /// <remarks>
    ///     <para>Invariants maintained by the insert and remove code:</para>
    ///     <list type="bullet">
    ///         <item>A child's prefix is strictly longer than, and contained in, its parent's prefix.</item>
    ///         <item>The child in slot <c>b</c> has address bit <c>b</c> at position <see cref="Prefix" /> of its parent, counted from the most significant bit.</item>
    ///         <item>Every routing node except the root has exactly two children, and its prefix is the longest prefix common to both.</item>
    ///         <item>Children of a full-length prefix (/32 or /128) are always null.</item>
    ///     </list>
    ///     <para>
    ///         PERF: the containment fields are copied out of <see cref="ValuePair" /> so that the lookup loop reads
    ///         only this object and its child array, instead of following the key record and its
    ///         <see cref="NetworkPrefix" /> object.
    ///     </para>
    /// </remarks>
    [DebuggerDisplay("{ValuePair.Prefix.ToString(),nq}")]
    private sealed class Node
    {
        /// <summary>Backing field for <see cref="ValuePair" />.</summary>
        private NetworkPrefixKey _valuePair;

        /// <summary>Address width in bits: 32 for IPv4 or 128 for IPv6. Cached from <see cref="ValuePair" />.</summary>
        public ushort AddressLength;

        /// <summary>The prefix's network bits, right-aligned. Cached from <see cref="NetworkPrefix.NetworkBits" />.</summary>
        public UInt128 NetworkBits;

        /// <summary>The prefix's network mask, right-aligned. Cached from <see cref="NetworkPrefix.NetworkMaskBits" />.</summary>
        public UInt128 NetworkMaskBits;

        /// <summary>The prefix length in bits. Cached from <see cref="NetworkPrefix.Length" />.</summary>
        public ushort Prefix;

        /// <summary>
        ///     A single bit set at the first address bit after the prefix, <c>1 &lt;&lt; (AddressLength - Prefix - 1)</c>.
        ///     <see cref="GetSlot(UInt128)" /> tests this bit to choose a child.
        /// </summary>
        /// <remarks>
        ///     For a full-length prefix the shift amount is -1. <see cref="UInt128" /> masks shift amounts to 7 bits,
        ///     so the mask becomes bit 127. That is harmless because full-length nodes never have children.
        /// </remarks>
        public UInt128 SplitMask;

        /// <summary>
        ///     The two child slots. Slot 0 holds the lower half of this node's address space and slot 1 the upper half.
        ///     Either slot may be null.
        /// </summary>
        /// <remarks>PERF: a separate array object, so each step of a lookup costs an extra dependent load and a bounds check.</remarks>
        public Node[] Children { get; } = new Node[2];

        /// <summary>
        ///     True when this node holds an item that was added (a value node); false for a routing node that only
        ///     joins two subtrees.
        /// </summary>
        public bool HasValue { get; internal set; } // TODO: rename this

        /// <summary>
        ///     The stored item, or a plain <see cref="NetworkPrefixKey" /> carrying only the prefix for routing nodes.
        ///     Setting it refreshes all the cached prefix fields on the node.
        /// </summary>
        public NetworkPrefixKey ValuePair
        {
            get => _valuePair;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                _valuePair = value;

                // copy these fields to improve performance
                NetworkBits = _valuePair.Prefix.NetworkBits;
                NetworkMaskBits = _valuePair.Prefix.NetworkMaskBits;
                Prefix = (ushort)_valuePair.Prefix.Length;
                AddressLength = _valuePair.Prefix.AddressLength;

                SplitMask = UInt128.One << AddressLength - Prefix - 1;
            }
        }

        /// <summary>Creates a node for an added item.</summary>
        /// <param name="valuePair">The item, which also supplies the prefix.</param>
        /// <param name="hasValue">Whether the node is a value node. All callers pass true.</param>
        public Node(TValue valuePair, bool hasValue)
        {
            ValuePair = valuePair;
            HasValue = hasValue;
        }

        /// <summary>Creates a routing node for <paramref name="network" />.</summary>
        /// <param name="network">The prefix the routing node covers.</param>
        public Node(NetworkPrefix network)
        {
            ValuePair = new NetworkPrefixKey(network);
            HasValue = false;
        }

        /// <summary>
        ///     Chooses the child slot for an address or prefix below this node.
        /// </summary>
        /// <param name="networkBits">Right-aligned address bits, in the layout of <see cref="NetworkPrefix.NetworkBits" />.</param>
        /// <returns>0 if the bit after this node's prefix is clear, otherwise 1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSlot(UInt128 networkBits)
        {
            // calculate the child slot
            // if the child is in the first half of the parents address space, use slot 0
            // otherwise use slot 1
            // TODO: might be faster to do (max - min) /2. Could have a static lookup table for the number of addresses for a given prefix
            // that so you would do something like networkBits + (sizeLookup[prefixSize] / 2)
            return (networkBits & SplitMask) == UInt128.Zero ? 0 : 1;
        }

        /// <summary>
        ///     Chooses the child slot for <paramref name="network" /> below this node.
        /// </summary>
        /// <param name="network">A prefix that is longer than, and contained in, this node's prefix.</param>
        /// <returns>0 for the lower half of this node's address space, otherwise 1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSlot(NetworkPrefix network)
        {
            return GetSlot(network.NetworkBits);
        }
    }
}