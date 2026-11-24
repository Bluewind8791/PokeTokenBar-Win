namespace PokeTokenBar.Core;

/// <summary>
/// Windows counterpart of <c>SingleInstance.swift</c>: the first launch in a session keeps running, a later one yields.
/// The lock is ownership of <see cref="MutexName"/>, not its existence. Every PokeTokenBar process opens the mutex,
/// including the short-lived ones that hand a <c>poketokenbar://</c> URL to the running app; judged by "did I create
/// it", a handle such a process still held made a real launch yield, so the app did not start.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    /// <summary><c>Local\</c>: one instance per Windows session, like the agent URL pipe.</summary>
    public const string MutexName = @"Local\PokeTokenBar.SingleInstance";

    private readonly Mutex _mutex;

    private SingleInstanceLock(Mutex mutex) => _mutex = mutex;

    /// <summary>The lock when no other instance owns the mutex; null when one does. A null result holds no handle,
    /// so a yielding process never keeps the mutex alive.</summary>
    public static SingleInstanceLock? TryAcquire(string name = MutexName)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;   // The previous instance ended without releasing it (a crash or a kill); the lock is ours.
        }
        if (owned) return new SingleInstanceLock(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>Call on the thread that acquired the lock (a mutex belongs to a thread).</summary>
    public void Dispose()
    {
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }   // Not the owning thread: the process exit releases it.
        _mutex.Dispose();
    }
}
