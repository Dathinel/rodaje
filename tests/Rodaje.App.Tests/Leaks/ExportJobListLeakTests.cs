using System.Runtime.CompilerServices;
using Rodaje.App.Tests.Export;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Export;
using Rodaje.Media.Export;
using Rodaje.Media.Video;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// Cada exportación lleva una copia del proyecto. Antes de este arreglo, las terminadas la conservaban (en la fila y en su
/// trabajo) hasta pulsar «Limpiar terminadas»: la lista de Trabajos crecía con un proyecto entero por exportación.
/// </summary>
public sealed class ExportJobListLeakTests
{
    [Fact]
    public void A_completed_export_releases_its_project_and_keeps_the_summary() => UiThread.Run(async () =>
    {
        var runner = new ManualRunner();
        using var list = new ExportJobList(runner, SynchronizationContext.Current);
        (ExportEntry entry, WeakReference project) = EnqueueWithProject(list, "uno.mp4");

        await FinishAsync(list, runner, ExportJobState.Completed);
        Collect();

        Assert.False(project.IsAlive);
        Assert.Contains(entry, list.Entries);
        Assert.True(entry.IsReleased);
        Assert.Equal(ExportJobState.Completed, entry.Progress.State);
        Assert.Equal(1, entry.Progress.Fraction);
        Assert.NotNull(entry.Thumbnail);
        Assert.Equal("uno.mp4", entry.Title);
        Assert.Equal(ExportTestRange, entry.Request.Range);
        Assert.Same(ExportPresets.YouTube1080p.Settings, entry.Request.Settings);
        Assert.Equal("p", entry.Request.Project.Id);
        Assert.Empty(entry.Request.Project.Sequences);
        Assert.Equal(1, runner.Disposed);
    });

    [Fact]
    public void A_hundred_completed_exports_keep_no_project_alive() => UiThread.Run(async () =>
    {
        var runner = new ManualRunner();
        using var list = new ExportJobList(runner, SynchronizationContext.Current);
        var projects = new List<WeakReference>();
        for (int i = 0; i < 100; i++)
        {
            projects.Add(EnqueueWithProject(list, $"{i}.mp4").Project);
            await FinishAsync(list, runner, ExportJobState.Completed);
        }

        Collect();

        Assert.Equal(100, list.Entries.Count);
        Assert.Equal(0, projects.Count(p => p.IsAlive));
    });

    [Theory]
    [InlineData(ExportJobState.Failed)]
    [InlineData(ExportJobState.Canceled)]
    public void Failed_and_canceled_keep_the_project_to_retry_it(ExportJobState state) => UiThread.Run(async () =>
    {
        var runner = new ManualRunner();
        using var list = new ExportJobList(runner, SynchronizationContext.Current);
        (ExportEntry entry, WeakReference project) = EnqueueWithProject(list, "repite.mp4");

        await FinishAsync(list, runner, state);
        Collect();

        Assert.True(project.IsAlive);
        Assert.False(entry.IsReleased);

        // Con otro proyecto abierto, se repite la copia de entonces.
        Project other = ExportTestData.Project();
        other.Id = "otro";
        ExportEntry retried = list.Retry(entry, current: other);
        Assert.Same(project.Target, retried.Request.Project);
    });

    [Fact]
    public void A_released_export_is_retried_only_with_its_project_open() => UiThread.Run(async () =>
    {
        var runner = new ManualRunner();
        using var list = new ExportJobList(runner, SynchronizationContext.Current);
        (ExportEntry entry, _) = EnqueueWithProject(list, "otra-vez.mp4");
        await FinishAsync(list, runner, ExportJobState.Completed);

        Assert.Throws<InvalidOperationException>(() => list.Retry(entry));
        Assert.Contains(entry, list.Entries);

        Project open = ExportTestData.Project();
        ExportEntry retried = list.Retry(entry, current: open);
        Assert.Equal(open.Id, retried.Request.Project.Id);
        Assert.NotEmpty(retried.Request.Project.Sequences);
        Assert.DoesNotContain(entry, list.Entries);
    });

    private static readonly ExportRange ExportTestRange = new(Rodaje.Core.Timing.Time.FromSeconds(1), Rodaje.Core.Timing.Time.FromSeconds(3));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ExportEntry Entry, WeakReference Project) EnqueueWithProject(ExportJobList list, string name)
    {
        Project project = ExportTestData.Project();
        var request = new ExportRequest(project, "sec", name, ExportPresets.YouTube1080p.Settings, ExportTestRange);
        return (list.Enqueue(request), new WeakReference(project));
    }

    // Termina el último trabajo y espera a que la lista lo anote en el hilo de la interfaz.
    private static async Task FinishAsync(ExportJobList list, ManualRunner runner, ExportJobState state)
    {
        var finished = new TaskCompletionSource();
        EventHandler<ExportEntry> handler = (_, _) => finished.TrySetResult();
        list.Finished += handler;
        runner.Take().Complete(state);
        await finished.Task.WaitAsync(UiThread.DefaultTimeout);
        list.Finished -= handler;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Trabajos que terminan cuando la prueba lo dice; no guarda lo pedido (la simulación sí, en <c>Started</c>).</summary>
    private sealed class ManualRunner : IExportRunner
    {
        private ManualJob? _last;

        public int Disposed { get; private set; }

        public bool IsSimulation => true;

        /// <summary>El último trabajo que empezó; el corredor deja de guardarlo, como el de verdad.</summary>
        public ManualJob Take()
        {
            ManualJob job = _last ?? throw new InvalidOperationException("No empezó ninguno.");
            _last = null;
            return job;
        }

        public Task<IReadOnlyList<EncoderSpec>> GetEncodersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EncoderSpec>>([]);

        public IExportJob Start(ExportRequest request, bool continueWithoutMissing = false)
        {
            var job = new ManualJob(request, () => Disposed++);
            _last = job;
            return job;
        }
    }

    private sealed class ManualJob(ExportRequest request, Action disposed) : IExportJob
    {
        private readonly TaskCompletionSource _done = new();
        private ExportProgress _progress = new(ExportJobState.Encoding, 0, 0, 0, TimeSpan.Zero, null, 0);

        public event EventHandler? Changed;

        public ExportRequest Request { get; } = request;

        public ExportProgress Progress => _progress;

        public ExportException? Failure { get; private set; }

        public BgraImage? Thumbnail { get; } = new(2, 2, new byte[16]);

        public Task Completion => _done.Task;

        public void Complete(ExportJobState state)
        {
            Failure = state == ExportJobState.Failed ? new ExportException(ExportErrorCode.DecodeFailed, "falló") : null;
            _progress = new ExportProgress(state, 10, 10, 0, TimeSpan.Zero, null, 0);
            Changed?.Invoke(this, EventArgs.Empty);
            _done.SetResult();
        }

        public void Cancel()
        {
        }

        public void Dispose() => disposed();
    }
}
