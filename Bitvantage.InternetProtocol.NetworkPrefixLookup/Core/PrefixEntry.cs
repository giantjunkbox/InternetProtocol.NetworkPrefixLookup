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

namespace Bitvantage.InternetProtocol.NetworkPrefixLookup;

/// <summary>A stored prefix and its value.</summary>
/// <typeparam name="TPrefix">The type of the prefix object.</typeparam>
/// <typeparam name="TValue">The type of the value.</typeparam>
/// <param name="Prefix">The prefix object that was added.</param>
/// <param name="Value">The value associated with the prefix.</param>
public readonly record struct PrefixEntry<TPrefix, TValue>(TPrefix Prefix, TValue Value);
