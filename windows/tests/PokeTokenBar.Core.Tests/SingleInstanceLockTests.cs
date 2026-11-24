using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// The Windows single-instance lock. Regression: the app judged "another instance runs" by "the mutex already
/// existed", so a handle that any other PokeTokenBar process held (a URL hand-off still alive) made a real launch
/// yield and the app did not start. Each test uses its own mutex name, so none touches a running app.
/// </summary>
public class SingleInstanceLockTests
{
    private static string UniqueName() => @"Local\PokeTokenBarTests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void TheRealNameIsPerSession() =>
        Assert.Equal(@"Local\PokeTokenBar.SingleInstance", SingleInstanceLock.MutexName);

    /// <summary>The trigger of the defect: the mutex exists, nobody owns it.</summary>
    [Fact]
    public void AHandleThatDoesNotOwnTheMutexDoesNotBlockTheLaunch()
    {
        var name = UniqueName();
        using var handOff = new Mutex(initiallyOwned: false, name);
        using var instance = SingleInstanceLock.TryAcquire(name);
        Assert.NotNull(instance);
    }

    [Fact]
    public void ASecondLaunchYieldsWhileTheFirstOwnsIt()
    {
        var name = UniqueName();
        var first = OnOwnThread(() => SingleInstanceLock.TryAcquire(name), out var release);
        try
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstanceLock.TryAcquire(name));
        }
        finally { release(); }
        // Released: the next launch gets it.
        using var next = SingleInstanceLock.TryAcquire(name);
        Assert.NotNull(next);
    }

    /// <summary>An instance that crashed or was killed leaves the mutex abandoned; the next launch takes it.</summary>
    [Fact]
    public void AnAbandonedMutexIsTakenOver()
    {
        var name = UniqueName();
        var owner = new Thread(() => new Mutex(initiallyOwned: true, name));   // exits without releasing
        owner.Start();
        owner.Join();
        using var instance = SingleInstanceLock.TryAcquire(name);
        Assert.NotNull(instance);
    }

    /// <summary>A mutex belongs to a thread, so another thread of this process owning it stands in for another
    /// instance. <paramref name="release"/> disposes the lock on that thread and ends it.</summary>
    private static SingleInstanceLock? OnOwnThread(Func<SingleInstanceLock?> acquire, out Action release)
    {
        SingleInstanceLock? result = null;
        using var acquired = new ManualResetEventSlim();
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            result = acquire();
            acquired.Set();
            done.Wait();
            result?.Dispose();
        });
        thread.Start();
        acquired.Wait();
        release = () => { done.Set(); thread.Join(); done.Dispose(); };
        return result;
    }
}
