using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

public class SingleInstanceGuardTests
{
    private readonly string _name = $@"Local\ADHDWindows.Tests.{Guid.NewGuid()}";

    // Мьютекс принадлежит потоку, поэтому вторая копия моделируется другим потоком.
    private bool TryAcquireOnOtherThread()
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            using var guard = new SingleInstanceGuard(_name);
            acquired = guard.TryAcquire();
        });
        thread.Start();
        thread.Join();
        return acquired;
    }

    [Fact]
    public void Second_instance_is_refused_while_the_first_runs()
    {
        using var first = new SingleInstanceGuard(_name);

        Assert.True(first.TryAcquire());
        Assert.False(TryAcquireOnOtherThread());
    }

    [Fact]
    public void Instance_can_start_after_the_first_exits()
    {
        using (var first = new SingleInstanceGuard(_name))
        {
            Assert.True(first.TryAcquire());
        }

        Assert.True(TryAcquireOnOtherThread());
    }

    [Fact]
    public void Instance_can_start_after_a_crash_of_the_first()
    {
        var crashed = new Thread(() =>
        {
            // Поток завершается, не освободив мьютекс, как процесс при сбое.
            new SingleInstanceGuard(_name).TryAcquire();
        });
        crashed.Start();
        crashed.Join();

        Assert.True(TryAcquireOnOtherThread());
    }
}
