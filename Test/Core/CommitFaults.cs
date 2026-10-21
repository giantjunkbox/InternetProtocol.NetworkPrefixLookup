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

using System.Reflection;

namespace Test.Core;

/// <summary>Makes a trie's next batch commit throw, as running out of memory would.</summary>
public static class CommitFaults
{
    /// <summary>
    ///     Publishes a copy of the trie's snapshot without shortcut blocks, so that the next commit throws while it rebuilds
    ///     them. Reaches into private members, so it breaks loudly if they are renamed. Returns an action that puts the
    ///     real snapshot back; call it after the failed commit.
    /// </summary>
    /// <param name="trie">A <c>BitTrie</c> of any type arguments, which may be internal to the library.</param>
    public static Action BreakNextCommit(object trie)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = trie.GetType().GetField("_published", flags) ?? throw new MissingFieldException("_published");
        var original = field.GetValue(trie)!;
        var root = original.GetType().GetField("Root")!.GetValue(original);
        field.SetValue(trie, Activator.CreateInstance(original.GetType(), root, false));
        return () => field.SetValue(trie, original);
    }

    /// <summary>Marks a trie's open batch as one in which a write threw, as a write that runs out of memory would.</summary>
    /// <param name="trie">A <c>BitTrie</c> of any type arguments, inside a batch.</param>
    public static void FailBatch(object trie) =>
        (trie.GetType().GetField("_batchFailed", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingFieldException("_batchFailed")).SetValue(trie, true);

    /// <summary>The value of a private field, such as a lookup's tries, declared on the owner's type or a base type.</summary>
    public static object Field(object owner, string name)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) is { } field)
                return field.GetValue(owner)!;
        throw new MissingFieldException(name);
    }
}
