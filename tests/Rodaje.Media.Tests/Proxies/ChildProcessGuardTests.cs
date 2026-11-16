using System.Diagnostics;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Tests.Proxies;

/// <summary>
/// El registro de hijos que usa <see cref="ChildProcessGuard"/> en Linux y macOS. La lógica no depende del sistema, así que
/// corre también en Windows, con procesos de verdad.
/// </summary>
public sealed class ChildProcessGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Rodaje-hijos-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (Process process in _started)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Abrupt_death_of_the_editor_leaves_no_child_alive_after_the_sweep()
    {
        Process editor = StartSleeper();
        Process child = StartSleeper();
        string session = ChildProcessGuard.SessionFile(_directory, editor.Id, editor.StartTime);
        ChildProcessGuard.Register(session, child.Id, child.StartTime);

        Assert.Equal(0, ChildProcessGuard.SweepOrphans(_directory));
        Assert.False(child.HasExited);

        editor.Kill();
        Assert.True(editor.WaitForExit(10_000));
        Assert.Equal(1, ChildProcessGuard.SweepOrphans(_directory));

        Assert.True(child.WaitForExit(10_000), "El hijo de un editor muerto siguió vivo tras el barrido.");
        Assert.False(File.Exists(session));
    }

    [Fact]
    public void A_recycled_pid_is_not_killed()
    {
        Process stranger = StartSleeper();
        string session = ChildProcessGuard.SessionFile(_directory, 999_999_999, DateTime.UtcNow.AddDays(-1));
        ChildProcessGuard.Register(session, stranger.Id, stranger.StartTime.AddMinutes(-5));

        Assert.Equal(0, ChildProcessGuard.SweepOrphans(_directory));

        Assert.False(stranger.WaitForExit(500));
        Assert.False(File.Exists(session));
    }

    [Fact]
    public void Garbage_in_the_registry_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "no-es-un-pid.txt"), "x y\n12\n\n");

        Assert.Equal(0, ChildProcessGuard.SweepOrphans(_directory));
        Assert.Equal(0, ChildProcessGuard.SweepOrphans(Path.Combine(_directory, "no-existe")));
    }

    private Process StartSleeper()
    {
        ProcessStartInfo info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 600 127.0.0.1")
            : new ProcessStartInfo("sleep", "600");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        Process process = Process.Start(info)!;
        _started.Add(process);
        return process;
    }
}
