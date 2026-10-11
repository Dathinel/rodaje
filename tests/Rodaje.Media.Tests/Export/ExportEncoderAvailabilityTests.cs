using System.Collections.Concurrent;
using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

public sealed class ExportEncoderAvailabilityTests
{
    private sealed class FakeProbe(Func<ExportEncoderInfo, ExportProbeResult> behavior) : IExportEncoderProbe
    {
        private int _calls;
        private int _running;
        private int _maxRunning;
        private readonly ConcurrentBag<string> _ids = [];

        public int Calls => _calls;

        public int MaxRunning => _maxRunning;

        public IReadOnlyCollection<string> Ids => _ids;

        public ExportProbeResult Probe(ExportEncoderInfo encoder)
        {
            Interlocked.Increment(ref _calls);
            _ids.Add(encoder.Id);
            int now = Interlocked.Increment(ref _running);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxRunning)) && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen)
            {
            }

            try
            {
                return behavior(encoder);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    [Fact]
    public async Task Every_catalog_encoder_is_probed_once_and_the_report_keeps_catalog_order()
    {
        var probe = new FakeProbe(e => e.Id == "libsvtav1" ? ExportProbeResult.Ok : ExportProbeResult.Fail("no"));
        var availability = new ExportEncoderAvailability(probe);

        ExportEncoderReport report = await availability.GetAsync();

        Assert.Equal(ExportEncoderCatalog.All.Length, probe.Calls);
        Assert.Equal(ExportEncoderCatalog.All.Select(e => e.Id), report.All.Select(s => s.Encoder.Id));
        Assert.Equal(["libsvtav1"], report.Available.Select(e => e.Id));
        Assert.Equal("no", report.All[0].Reason);
        Assert.Null(report.All.Single(s => s.Works).Reason);
    }

    [Fact]
    public async Task The_result_is_cached_and_concurrent_requests_share_one_run()
    {
        var probe = new FakeProbe(_ =>
        {
            Thread.Sleep(20);
            return ExportProbeResult.Ok;
        });
        var availability = new ExportEncoderAvailability(probe);

        ExportEncoderReport[] reports = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => availability.GetAsync()));
        ExportEncoderReport again = await availability.GetAsync();

        Assert.Equal(ExportEncoderCatalog.All.Length, probe.Calls);
        Assert.All(reports, r => Assert.Same(reports[0], r));
        Assert.Same(reports[0], again);
    }

    [Fact]
    public async Task Invalidate_makes_the_next_request_probe_again()
    {
        var probe = new FakeProbe(_ => ExportProbeResult.Ok);
        var availability = new ExportEncoderAvailability(probe);
        await availability.GetAsync();

        availability.Invalidate();
        await availability.GetAsync();

        Assert.Equal(ExportEncoderCatalog.All.Length * 2, probe.Calls);
    }

    [Fact]
    public async Task Probes_run_in_parallel_up_to_the_limit()
    {
        using var barrier = new Barrier(3);
        var probe = new FakeProbe(_ =>
        {
            // Solo avanza si tres sondas coinciden; con una ejecución en serie se agotaría el tiempo.
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return ExportProbeResult.Ok;
        });
        var availability = new ExportEncoderAvailability(probe, maxParallelism: 3, catalog: ExportEncoderCatalog.All.Take(3));

        await availability.GetAsync();

        Assert.Equal(3, probe.MaxRunning);
    }

    [Fact]
    public async Task The_parallelism_limit_is_respected()
    {
        var probe = new FakeProbe(_ =>
        {
            Thread.Sleep(15);
            return ExportProbeResult.Ok;
        });
        var availability = new ExportEncoderAvailability(probe, maxParallelism: 2);

        await availability.GetAsync();

        Assert.InRange(probe.MaxRunning, 1, 2);
    }

    [Fact]
    public async Task A_probe_that_throws_counts_as_unavailable_without_stopping_the_others()
    {
        var probe = new FakeProbe(e => e.Id == "h264_nvenc"
            ? throw new InvalidOperationException("explotó")
            : ExportProbeResult.Ok);
        var availability = new ExportEncoderAvailability(probe);

        ExportEncoderReport report = await availability.GetAsync();

        ExportEncoderStatus nvenc = report.All.Single(s => s.Encoder.Id == "h264_nvenc");
        Assert.False(nvenc.Works);
        Assert.Contains("explotó", nvenc.Reason, StringComparison.Ordinal);
        Assert.Equal(ExportEncoderCatalog.All.Length - 1, report.Available.Length);
    }

    [Fact]
    public async Task Cancelling_one_waiter_does_not_poison_the_shared_run()
    {
        using var release = new ManualResetEventSlim();
        var probe = new FakeProbe(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return ExportProbeResult.Ok;
        });
        var availability = new ExportEncoderAvailability(probe);
        using var cts = new CancellationTokenSource();

        Task<ExportEncoderReport> cancelled = availability.GetAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Task<ExportEncoderReport> other = availability.GetAsync();
        release.Set();
        ExportEncoderReport report = await other;

        Assert.Equal(ExportEncoderCatalog.All.Length, report.Available.Length);
        Assert.Equal(ExportEncoderCatalog.All.Length, probe.Calls);
    }

    [Fact]
    public async Task The_report_chooses_through_the_chooser()
    {
        var probe = new FakeProbe(e => e.Id is "libopenh264" or "libsvtav1" ? ExportProbeResult.Ok : ExportProbeResult.Fail("no"));
        ExportEncoderReport report = await new ExportEncoderAvailability(probe).GetAsync();

        Assert.Equal("libsvtav1", report.ChooseDefault()!.Encoder.Id);
        Assert.Equal("libsvtav1", report.Choose(ExportCodec.H264)!.Encoder.Id);
        Assert.True(report.Choose(ExportCodec.H264)!.Substituted);
    }

    [Fact]
    public void The_constructor_rejects_bad_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ExportEncoderAvailability(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportEncoderAvailability(new FakeProbe(_ => ExportProbeResult.Ok), 0));
    }
}
