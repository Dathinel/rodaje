using System.Collections.Concurrent;
using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

public sealed class ExportEncoderAvailabilityTests
{
    private sealed class FakeProbe(Func<EncoderSpec, ExportProbeResult> behavior) : IExportEncoderProbe
    {
        private readonly ConcurrentQueue<string> _ids = new();
        private int _running;
        private int _maxRunning;
        private int _resets;

        public int Calls => _ids.Count;

        public int MaxRunning => Volatile.Read(ref _maxRunning);

        public int Resets => Volatile.Read(ref _resets);

        public int CallsFor(string id) => _ids.Count(i => i == id);

        public ExportProbeResult Probe(EncoderSpec encoder)
        {
            _ids.Enqueue(encoder.Id);
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

        public void Reset() => Interlocked.Increment(ref _resets);
    }

    /// <summary>Reloj que solo avanza cuando la prueba lo dice.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static IEnumerable<EncoderSpec> Few(params string[] ids) => ids.Select(id => ExportEncoderCatalog.Find(id)!);

    [Fact]
    public async Task Every_catalog_encoder_is_probed_once_and_the_report_keeps_catalog_order()
    {
        var probe = new FakeProbe(e => e.Id == "libsvtav1" ? ExportProbeResult.Ok : ExportProbeResult.Fail("no"));
        var availability = new ExportEncoderAvailability(probe);

        ExportEncoderReport report = await availability.GetAsync();

        Assert.Equal(ExportEncoderCatalog.All.Count, probe.Calls);
        Assert.Equal(ExportEncoderCatalog.All.Select(e => e.Id), report.All.Select(s => s.Encoder.Id));
        Assert.Equal(["libsvtav1"], report.Available.Select(e => e.Id));
        Assert.Equal("no", report.All[0].Reason);
        Assert.Null(report.All.Single(s => s.Works).Reason);
        Assert.True(report.HasFailures);
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

        Assert.Equal(ExportEncoderCatalog.All.Count, probe.Calls);
        Assert.All(reports, r => Assert.Same(reports[0], r));
        Assert.Same(reports[0], again);
    }

    [Fact]
    public async Task Invalidate_makes_the_next_request_probe_again_and_resets_the_probe()
    {
        var probe = new FakeProbe(_ => ExportProbeResult.Ok);
        var availability = new ExportEncoderAvailability(probe);
        ExportEncoderReport first = await availability.GetAsync();

        availability.Invalidate();
        ExportEncoderReport second = await availability.GetAsync();

        Assert.NotSame(first, second);
        Assert.Equal(ExportEncoderCatalog.All.Count * 2, probe.Calls);
        Assert.Equal(1, probe.Resets);
    }

    [Fact]
    public async Task Invalidate_during_a_run_starts_the_next_one_only_after_it_ends()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var probe = new FakeProbe(_ =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(10));
            return ExportProbeResult.Ok;
        });
        var availability = new ExportEncoderAvailability(probe, catalog: Few("libsvtav1"));

        Task<ExportEncoderReport> old = availability.GetAsync();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));
        availability.Invalidate();
        Task<ExportEncoderReport> fresh = availability.GetAsync();
        await Task.Delay(100);

        // La prueba vieja sigue sola: la nueva no ha abierto otra sonda a la vez.
        Assert.Equal(1, probe.Calls);
        Assert.False(fresh.IsCompleted);
        release.Set();
        ExportEncoderReport oldReport = await old;
        ExportEncoderReport freshReport = await fresh;

        Assert.NotSame(oldReport, freshReport);
        Assert.Equal(2, probe.Calls);
        Assert.Equal(1, probe.MaxRunning);
        Assert.Same(freshReport, await availability.GetAsync());
    }

    [Fact]
    public async Task Only_failures_are_probed_again_and_only_after_the_interval()
    {
        int nvencCalls = 0;
        var probe = new FakeProbe(e => e.Id switch
        {
            "libsvtav1" => ExportProbeResult.Ok,
            "libkvazaar" => ExportProbeResult.NotCompiled("no está compilado"),
            // La GPU estaba ocupada la primera vez.
            _ => Interlocked.Increment(ref nvencCalls) == 1 ? ExportProbeResult.Fail("ocupada") : ExportProbeResult.Ok,
        });
        var time = new ManualTime();
        var availability = new ExportEncoderAvailability(probe, catalog: Few("h264_nvenc", "libsvtav1", "libkvazaar"), time: time,
            failureRetryInterval: TimeSpan.FromMinutes(2));

        ExportEncoderReport first = await availability.GetAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        ExportEncoderReport early = await availability.GetAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        ExportEncoderReport retried = await availability.GetAsync();
        time.Advance(TimeSpan.FromHours(1));
        ExportEncoderReport later = await availability.GetAsync();

        Assert.True(first.HasFailures);
        Assert.Same(first, early);
        Assert.NotSame(first, retried);
        Assert.Equal(["h264_nvenc", "libsvtav1"], retried.Available.Select(e => e.Id));
        Assert.Equal(ExportProbeStatus.NotCompiled, retried.Find("libkvazaar")!.Result.Status);
        Assert.False(retried.HasFailures);
        Assert.Same(retried, later);
        Assert.Equal(2, probe.CallsFor("h264_nvenc"));
        Assert.Equal(1, probe.CallsFor("libsvtav1"));
        Assert.Equal(1, probe.CallsFor("libkvazaar"));
    }

    [Fact]
    public async Task Hardware_that_is_missing_keeps_failing_and_costs_one_probe_per_interval()
    {
        var probe = new FakeProbe(e => e.Id == "av1_nvenc" ? ExportProbeResult.Fail("sin RTX 40") : ExportProbeResult.Ok);
        var time = new ManualTime();
        var availability = new ExportEncoderAvailability(probe, catalog: Few("h264_nvenc", "av1_nvenc"), time: time);

        await availability.GetAsync();
        time.Advance(TimeSpan.FromMinutes(5));
        ExportEncoderReport report = await availability.GetAsync();

        Assert.Equal(["h264_nvenc"], report.Available.Select(e => e.Id));
        Assert.Equal("sin RTX 40", report.Find("av1_nvenc")!.Reason);
        Assert.Equal(1, probe.CallsFor("h264_nvenc"));
        Assert.Equal(2, probe.CallsFor("av1_nvenc"));
    }

    [Fact]
    public async Task Probes_run_in_parallel_up_to_the_limit()
    {
        using var barrier = new Barrier(3);
        var probe = new FakeProbe(_ =>
        {
            // Solo avanza si tres sondas coinciden; en serie se agotaría el tiempo.
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
            Thread.Sleep(5);
            return ExportProbeResult.Ok;
        });
        var availability = new ExportEncoderAvailability(probe, maxParallelism: 2);

        await availability.GetAsync();

        Assert.InRange(probe.MaxRunning, 1, 2);
    }

    [Fact]
    public async Task A_probe_that_throws_counts_as_a_failure_without_stopping_the_others()
    {
        var probe = new FakeProbe(e => e.Id == "h264_nvenc"
            ? throw new InvalidOperationException("explotó")
            : ExportProbeResult.Ok);
        var availability = new ExportEncoderAvailability(probe);

        ExportEncoderReport report = await availability.GetAsync();

        ExportEncoderStatus nvenc = report.Find("h264_nvenc")!;
        Assert.False(nvenc.Works);
        Assert.Equal(ExportProbeStatus.Failed, nvenc.Result.Status);
        Assert.Contains("explotó", nvenc.Reason, StringComparison.Ordinal);
        Assert.Equal(ExportEncoderCatalog.All.Count - 1, report.Available.Count);
    }

    [Fact]
    public async Task A_failure_without_a_reason_gets_one()
    {
        var probe = new FakeProbe(_ => new ExportProbeResult(ExportProbeStatus.Failed));

        ExportEncoderReport report = await new ExportEncoderAvailability(probe, catalog: Few("libsvtav1")).GetAsync();

        Assert.Equal("Sin motivo.", report.All[0].Reason);
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

        Assert.Equal(ExportEncoderCatalog.All.Count, report.Available.Count);
        Assert.Equal(ExportEncoderCatalog.All.Count, probe.Calls);
    }

    [Fact]
    public async Task The_report_chooses_through_the_chooser()
    {
        var probe = new FakeProbe(e => e.Id is "libopenh264" or "libsvtav1" or "aac" or "libkvazaar"
            ? ExportProbeResult.Ok
            : ExportProbeResult.Fail("no"));
        ExportEncoderReport report = await new ExportEncoderAvailability(probe).GetAsync();

        Assert.Equal("libsvtav1", report.ChooseDefault()!.Encoder.Id);
        Assert.Equal("libsvtav1", report.Choose(ExportCodec.H264)!.Encoder.Id);
        Assert.True(report.Choose(ExportCodec.H264)!.Substituted);
        Assert.Equal("libsvtav1", report.Choose(ExportCodec.Hevc)!.Encoder.Id);
        Assert.Equal("aac", report.ChooseDefaultAudio()!.Encoder.Id);
        Assert.Equal("libkvazaar", report.ChooseById("libkvazaar")!.Encoder.Id);
        Assert.Null(report.ChooseById("h264_nvenc"));
    }

    [Fact]
    public void The_constructor_rejects_bad_arguments()
    {
        var probe = new FakeProbe(_ => ExportProbeResult.Ok);

        Assert.Throws<ArgumentNullException>(() => new ExportEncoderAvailability(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportEncoderAvailability(probe, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportEncoderAvailability(probe, failureRetryInterval: TimeSpan.FromSeconds(-1)));
    }
}
