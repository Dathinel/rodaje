using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.TestMedia;

namespace Rodaje.Export.Tests.Running;

/// <summary>F5 del repaso del 19-nov: una excepción en el hilo de la exportación tumbaba la app entera.</summary>
public sealed class ExportJobListenerTests
{
    [ProxyFact]
    public async Task A_listener_that_throws_does_not_end_the_export_thread_and_the_job_still_finishes()
    {
        var options = new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable! };
        var request = new ExportRequest(ExportPlanTests.Project(Time.FromSeconds(1)), "sec", Path.Combine(Path.GetTempPath(), "rodaje-no-se-escribe.mp4"), new ExportSettings());
        using var job = new ExportJob(request, [], options);
        job.Changed += (_, _) => throw new InvalidOperationException("La interfaz falló al enterarse.");

        job.Launch();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(ExportJobState.Failed, job.Progress.State);
        Assert.NotNull(job.Failure);
    }
}
