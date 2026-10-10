namespace Rodaje.TestMedia.Tests;

/// <summary>El candado es de un hilo (como el Mutex que lo respalda), así que cada dueño es un hilo propio.</summary>
public sealed class CrossProcessLockTests
{
    [Fact]
    public void A_second_holder_of_the_same_file_waits_until_the_first_releases()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rodaje candado ñ {Guid.NewGuid():N}.mp4");
        Thread second;
        using (CrossProcessLock.Acquire(path))
        {
            second = Start(path);
            Assert.False(second.Join(TimeSpan.FromMilliseconds(200)));
        }

        Assert.True(second.Join(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Different_files_do_not_block_each_other()
    {
        string root = Path.GetTempPath();
        using CrossProcessLock first = CrossProcessLock.Acquire(Path.Combine(root, $"a-{Guid.NewGuid():N}.mp4"));

        Assert.True(Start(Path.Combine(root, $"b-{Guid.NewGuid():N}.mp4")).Join(TimeSpan.FromSeconds(10)));
    }

    private static Thread Start(string path)
    {
        var thread = new Thread(() => CrossProcessLock.Acquire(path).Dispose()) { IsBackground = true };
        thread.Start();
        return thread;
    }
}
