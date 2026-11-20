using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Session;

public sealed class ImportConcurrencyTests
{
    [Fact]
    public void RepeatedPath_IsProbedOnce() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("repeated.mp4");
        EditorSession session = CreateSession(prober);

        ImportResult result = await session.ImportMediaAsync(["repeated.mp4", Path.GetFullPath("REPEATED.MP4"), "./repeated.mp4"]);

        Assert.Equal(1, prober.Calls);
        Assert.Single(result.MediaIds);
        Assert.Single(session.Project.Media);
    });

    [Fact]
    public void TwoHundredFiles_RespectConcurrencyLimitAndInputOrder() => UiThread.Run(async () =>
    {
        int limit = Math.Clamp(Environment.ProcessorCount, 2, 8);
        using var prober = new GatedProber(limit);
        EditorSession session = CreateSession(prober);
        string[] paths = Paths();
        Task<ImportResult> importing = session.ImportMediaAsync(paths);
        try
        {
            await prober.Filled.Task;
            // Ventana de observación con todos los permisos ocupados: un lector adicional delata el defecto.
            await Task.WhenAny(prober.Exceeded.Task, Task.Delay(200));
        }
        finally
        {
            prober.Release();
        }

        ImportResult result = await importing;
        Assert.Equal(limit, prober.Peak);
        Assert.Equal(200, prober.Calls);
        Assert.Equal(200, result.MediaIds.Count);
        Assert.Equal(paths, session.Project.Media.Select(media => media.Path));
        Assert.False(session.IsBusy);
    });

    [Fact]
    public void Cancellation_StopsPendingProbesAndLeavesProjectUnchanged() => UiThread.Run(async () =>
    {
        int limit = Math.Clamp(Environment.ProcessorCount, 2, 8);
        using var prober = new GatedProber(limit);
        using var cancellation = new CancellationTokenSource();
        EditorSession session = CreateSession(prober);
        Task<ImportResult> importing = session.ImportMediaAsync(Paths(), cancellation.Token);
        bool busyDuringCancellation;
        bool finishedDuringCancellation;
        try
        {
            await prober.Filled.Task;
            await cancellation.CancelAsync();
            busyDuringCancellation = session.IsBusy;
            finishedDuringCancellation = importing.IsCompleted;
        }
        finally
        {
            prober.Release();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
        Assert.True(busyDuringCancellation);
        Assert.False(finishedDuringCancellation);
        Assert.Equal(limit, prober.Calls);
        Assert.Empty(session.Project.Media);
        Assert.False(session.IsDirty);
        Assert.False(session.IsBusy);

        Assert.Single((await session.ImportMediaAsync(["after-cancellation.mp4"])).MediaIds);
    });

    [Fact]
    public void AlreadyCancelled_DoesNotStartProbing() => UiThread.Run(async () =>
    {
        var prober = new FakeProber();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        EditorSession session = CreateSession(prober);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ImportMediaAsync(Paths(), cancellation.Token));

        Assert.Equal(0, prober.Calls);
        Assert.Empty(session.Project.Media);
        Assert.False(session.IsBusy);
    });

    [Fact]
    public void CancellationDuringLastProbe_DiscardsResults() => UiThread.Run(async () =>
    {
        using var prober = new GatedProber(1);
        using var cancellation = new CancellationTokenSource();
        EditorSession session = CreateSession(prober);
        Task<ImportResult> importing = session.ImportMediaAsync(["last.mp4"], cancellation.Token);
        try
        {
            await prober.Filled.Task;
            await cancellation.CancelAsync();
        }
        finally
        {
            prober.Release();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
        Assert.Equal(1, prober.Calls);
        Assert.Empty(session.Project.Media);
        Assert.False(session.IsBusy);
    });

    private static EditorSession CreateSession(IMediaProber prober) =>
        new(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());

    private static string[] Paths() => [.. Enumerable.Range(0, 200).Select(i => Path.GetFullPath($"clip-{i}.mp4"))];

    private sealed class GatedProber(int limit) : IMediaProber, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Lock _gate = new();
        private int _active;

        public TaskCompletionSource Filled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public int Peak { get; private set; }

        public MediaProbeResult Probe(string path)
        {
            lock (_gate)
            {
                Calls++;
                Peak = Math.Max(Peak, ++_active);
                if (_active == limit)
                {
                    Filled.TrySetResult();
                }

                if (_active > limit)
                {
                    Exceeded.TrySetResult();
                    // Si vuelve el defecto, falla por el pico medido sin agotar el pool con 200 esperas.
                    _release.Set();
                }
            }

            try
            {
                if (!_release.Wait(UiThread.DefaultTimeout))
                {
                    throw new TimeoutException("No se liberó el análisis de prueba.");
                }

                return new MediaProbeResult(path, MediaSamples.Audio(path, 10), null);
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                }
            }
        }

        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
