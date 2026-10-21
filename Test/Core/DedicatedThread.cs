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

using System.Collections.Concurrent;

namespace Test.Core;

/// <summary>Runs work one item at a time on a thread of its own, so that a test can keep a batch on the thread that started it.</summary>
public sealed class DedicatedThread : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public DedicatedThread()
    {
        _thread = new Thread(() =>
        {
            foreach (var item in _work.GetConsumingEnumerable())
                item();
        }) { IsBackground = true };
        _thread.Start();
    }

    /// <summary>Runs a function on the thread and returns its result, or rethrows its exception.</summary>
    public T Run<T>(Func<T> function)
    {
        var result = new TaskCompletionSource<T>();
        _work.Add(() =>
        {
            try
            {
                result.SetResult(function());
            }
            catch (Exception e)
            {
                result.SetException(e);
            }
        });
        return result.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs an action on the thread, or rethrows its exception.</summary>
    public void Run(Action action) => Run(() =>
    {
        action();
        return 0;
    });

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join();
    }
}
