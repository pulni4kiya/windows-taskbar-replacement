using System;
using System.Threading;

namespace TaskbarNicifier.App;

internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Local\\TaskbarNicifier.App.SingleInstance";

    private readonly Mutex _mutex;
    public bool IsFirstInstance { get; }

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
        IsFirstInstance = createdNew;
    }

    public void Dispose()
    {
        if (!IsFirstInstance)
            return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // ignore
        }

        _mutex.Dispose();
    }
}
