using System.Diagnostics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Offline;

public sealed class OfflineTimelineTests(ITestOutputHelper output)
{
    public static TheoryData<long, long> Rates => new()
    {
        { 24, 1 }, { 25, 1 }, { 30000, 1001 }, { 30, 1 }, { 50, 1 }, { 60000, 1001 }, { 60, 1 },
    };

    [Theory]
    [MemberData(nameof(Rates))]
    public void Every_frame_is_the_composition_the_settled_preview_shows(long num, long den)
    {
        var rate = new Rational(num, den);
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(rate, backend, content);
        // Reloj quieto: con el reloj del sistema y el runner cargado, entre dos refrescos pasaba más que el tope de la
        // composición a medias o el cierre de decodificadores ociosos (1,25 s con muchos abiertos), y la vista previa no
        // llegaba a asentarse en 20 refrescos. Aquí se compara qué compone, no cuánto tarda.
        using TimelineSession preview = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { Content = content, TimeProvider = new ManualTimeProvider() });
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Content = content });

        Assert.Equal(preview.FrameCount, offline.FrameCount);
        int transitions = 0;
        for (long f = 0; f < offline.FrameCount; f++)
        {
            preview.SeekToFrame(f);
            using CompositionFrame expected = Settle(preview);
            using CompositionFrame actual = offline.Render(f, CancellationToken.None);
            Assert.Equal(OfflineProjects.Describe(expected), OfflineProjects.Describe(actual));
            transitions += actual.Layers.Count(l => l.Transition is not null);
        }

        Assert.True(transitions > 0);
        Assert.Equal(0, offline.MissingContent);
        output.WriteLine($"{offline.FrameCount} cuadros a {rate}, {transitions} capas en transición, {offline.DecodersOpen} decodificadores abiertos al final.");
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Frames_stay_exact_hours_into_the_sequence(long num, long den)
    {
        var rate = new Rational(num, den);
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 0, rate);
        b.AddMedia("ntsc", Rational.Ntsc30, 3_600_000, backend: backend);
        b.AddMedia("cine", Rational.Ntsc24, 3_600_000, backend: backend);
        Clip first = b.AddVideo(0, "ntsc", S(1), S(7), Time.FromSeconds(3 * 3600));
        Clip second = b.AddVideo(1, "cine", Time.Zero, S(0.5), Time.FromSeconds((3 * 3600) + 2));
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);

        long last = offline.FrameCount - 1;
        long hour = Time.FromSeconds(3600).ToFrames(rate);
        long[] starts = [0, hour - 25, (2 * hour) + 7, last - 60];
        int checkedFrames = 0;
        foreach (long start in starts)
        {
            for (long f = start; f <= Math.Min(last, start + 60); f++)
            {
                using CompositionFrame frame = offline.Render(f, CancellationToken.None);
                foreach (LayerFrame layer in frame.Layers)
                {
                    (Clip clip, Rational source) = layer.TrackIndex == 0 ? (first, Rational.Ntsc30) : (second, Rational.Ntsc24);
                    Assert.Equal(ExactSourceFrame(clip, source, f, rate), layer.Frame.Index);
                    checkedFrames++;
                }
            }
        }

        Assert.True(checkedFrames > 300);
    }

    [Fact]
    public void A_partial_range_decodes_only_around_its_frames()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 30_000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, S(100), S(600));
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);

        Assert.Empty(offline.Prepare(F(9000, Rational.Fps30), F(9100, Rational.Fps30), CancellationToken.None));
        for (long f = 9000; f < 9100; f++)
        {
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            Assert.Equal(3000 + f, Assert.Single(frame.Layers).Frame.Index);
        }

        long[] requests = [.. backend.Videos.SelectMany(v => v.Requests)];
        Assert.Equal(12_000, requests.Min());
        Assert.True(requests.Max() < 12_100 + offline.Options.LookaheadFrames);
        Assert.Equal(requests.Length, requests.Distinct().Count());
        Assert.Single(backend.Videos);
    }

    [Fact]
    public void Going_back_releases_what_was_read_ahead_and_stays_exact()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 30_000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, S(10), S(60));
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);

        foreach (long f in new long[] { 500, 501, 502, 40, 41, 900 })
        {
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            Assert.Equal(300 + f, Assert.Single(frame.Layers).Frame.Index);
        }

        offline.Dispose();
        Assert.Equal(0, backend.Live.Value);
        Assert.All(backend.Videos, v => Assert.True(v.Disposed));
    }

    [Fact]
    public void Rate_conversion_asks_instants_between_frames_without_seeking_back()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps60, 30_000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, S(10), S(20));
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);

        // Una salida a 60 sobre una secuencia de 30 con un origen de 60: cada instante ve su propio cuadro del origen.
        for (long n = 0; n < 600; n++)
        {
            using CompositionFrame frame = offline.RenderAt(F(n, Rational.Fps60), CancellationToken.None);
            Assert.Equal(600 + n, Assert.Single(frame.Layers).Frame.Index);
        }

        long[] requests = [.. backend.Videos.Single().Requests];
        Assert.Equal(requests.Order(), requests);
    }

    [Fact]
    public void A_reversed_clip_shows_its_exact_frames_backwards()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        OfflineProjects.Reversed(b.AddVideo(0, "m", Time.Zero, S(10), S(2)), S(10), S(12));
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);

        using TimelineSession preview = TimelineSessionTests.Open(b, backend);
        var shown = new List<long>();
        for (long f = 0; f < offline.FrameCount; f++)
        {
            preview.SeekToFrame(f);
            using CompositionFrame expected = Settle(preview);
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            Assert.Equal(OfflineProjects.Describe(expected), OfflineProjects.Describe(frame));
            shown.Add(Assert.Single(frame.Layers).Frame.Index);
        }

        Assert.Equal(Enumerable.Range(300, 60).Reverse().Select(i => (long)i), shown);
    }

    [Fact]
    public void A_reversed_clip_decodes_each_gop_once_from_its_keyframe_within_the_pool()
    {
        const long Surface = 16L * 1024 * 1024;
        var backend = new FakeTimelineBackend { KeyframeInterval = 15, PoolSurfaceBytes = Surface, PoolReferences = 10 };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        OfflineProjects.Reversed(b.AddVideo(0, "m", Time.Zero, S(10), S(10)), S(10), S(20));
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);

        for (long f = 0; f < offline.FrameCount; f++)
        {
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            Assert.Equal(599 - f, Assert.Single(frame.Layers).Frame.Index);
        }

        // Un pool de 64: 10 referencias, 5 del decodificador, 3 de margen, 1 guardado, 8 pedidos y M = 37 (tramos de 18).
        FakeMediaVideo video = Assert.Single(backend.Videos);
        Assert.Equal(54, video.SpareSurfaces);
        Assert.Equal(54, video.ExtraSurfaces);
        output.WriteLine($"300 cuadros hacia atrás con GOP 15: {video.Seeks} seeks, {video.DecodedFrames} cuadros decodificados.");
        Assert.True(video.Seeks <= (300 / 15) + 1, $"{video.Seeks} seeks");
        Assert.True(video.DecodedFrames <= 300 + 15, $"{video.DecodedFrames} decodificados");
        Assert.Equal(0, offline.VramOverruns);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    public void A_reversed_clip_stays_exact_at_any_rate_and_on_another_output_grid(long num, long den)
    {
        var backend = new FakeTimelineBackend { KeyframeInterval = 24 };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip clip = SpeedClips.Reversed(b.AddVideo(0, "m", S(1), S(10), S(4)), new Rational(num, den), S(10), S(10) + Time.FromSeconds(4.0 * num / den));
        using (OfflineTimeline offline = OfflineProjects.Open(b, backend))
        {
            for (long f = 0; f < offline.FrameCount; f++)
            {
                using CompositionFrame frame = offline.Render(f, CancellationToken.None);
                if (frame.TimelineTime >= clip.Start && frame.TimelineTime < clip.End)
                {
                    Assert.Equal(SpeedClips.Expected(clip, Rational.Fps30, frame.TimelineTime), Assert.Single(frame.Layers).Frame.Index);
                }
            }
        }

        using OfflineTimeline other = OfflineProjects.Open(b, backend);
        for (long n = 0; ; n++)
        {
            Time at = F(n, Rational.Fps60);
            if (at >= other.Duration)
            {
                break;
            }

            using CompositionFrame frame = other.RenderAt(at, CancellationToken.None);
            if (at >= clip.Start && at < clip.End)
            {
                Assert.Equal(SpeedClips.Expected(clip, Rational.Fps30, at), Assert.Single(frame.Layers).Frame.Index);
            }
        }

        other.Dispose();
        Assert.Equal(0, backend.Live.Value);
    }

    [Fact]
    public async Task Cancelling_while_a_decoder_is_busy_returns_promptly_and_releases_everything()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(10));
        b.AddVideo(1, "b", Time.Zero, Time.Zero, S(10));
        OfflineTimeline offline = OfflineProjects.Open(b, backend);

        Task<CompositionFrame> first = Task.Run(() => offline.Render(0, CancellationToken.None));
        while (!first.IsCompleted)
        {
            backend.RunDecoders();
            Thread.Sleep(1);
        }

        (await first).Dispose();
        using var cancel = new CancellationTokenSource();
        // Hilo propio y no del pool: con el runner cargado, Task.Run tardaba más de 1 s en empezar y la prueba medía la
        // espera del pool, no la cancelación. El tiempo se cuenta desde que se cancela.
        using var started = new ManualResetEventSlim();
        Task<CompositionFrame> stuck = Task.Factory.StartNew(() =>
        {
            started.Set();
            return offline.Render(200, cancel.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "El hilo del render no arrancó.");
        Thread.Sleep(100);
        Assert.False(stuck.IsCompleted);
        var watch = Stopwatch.StartNew();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Tardó {watch.Elapsed.TotalMilliseconds:0} ms desde cancelar.");

        offline.Dispose();
        Assert.Equal(0, backend.Live.Value);
        Assert.All(backend.Videos, v => Assert.True(v.Disposed));
        Assert.All(backend.Executors, e => Assert.True(e.Disposed));
    }

    [Fact]
    public void Steady_state_allocates_only_the_contract_classes()
    {
        var backend = new FakeTimelineBackend { RealThreads = true };
        var content = new FakeEffectContent();
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 30_000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 30_000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(100));
        Clip top = b.AddVideo(1, "b", Time.Zero, S(5), S(100));
        top.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Saturation, 0.3)));
        top.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.5)));
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Content = content });

        for (long f = 0; f < 200; f++)
        {
            offline.Render(f, CancellationToken.None).Dispose();
        }

        const int Frames = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (long f = 200; f < 200 + Frames; f++)
        {
            offline.Render(f, CancellationToken.None).Dispose();
        }

        double perFrame = (double)(GC.GetAllocatedBytesForCurrentThread() - before) / Frames;

        // Lo mismo que arma el contrato: la composición, su lista y dos capas (una con efectos), sin decodificar nada.
        using CompositionFrame sample = offline.Render(1200, CancellationToken.None);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Frames; i++)
        {
            var layers = new List<LayerFrame>(2);
            foreach (LayerFrame layer in sample.Layers)
            {
                layers.Add(new LayerFrame(layer.TrackIndex, layer.ClipId, layer.Frame, layer.Transform, layer.SampleAspectRatio, layer.Rotation)
                {
                    Effects = layer.Effects,
                    Blend = layer.Blend,
                });
            }

            GC.KeepAlive(new CompositionFrame(sample.TimelineTime, sample.Width, sample.Height, layers));
        }

        double contract = (double)(GC.GetAllocatedBytesForCurrentThread() - before) / Frames;
        output.WriteLine($"{perFrame:0} B por cuadro con 2 capas; las clases del contrato solas, {contract:0} B.");
        Assert.True(perFrame <= contract + 32, $"{perFrame:0} B por cuadro frente a {contract:0} B del contrato.");
    }

    [Fact]
    public void Read_ahead_fits_in_the_vram_budget()
    {
        const long Surface = 64L * 1024 * 1024;
        var backend = new FakeTimelineBackend { PoolSurfaceBytes = Surface, PoolReferences = 10 };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 30_000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 30_000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(30));
        b.AddVideo(1, "b", Time.Zero, Time.Zero, S(30));
        long budget = 48 * Surface;
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { VramBudgetBytes = budget });

        int maxSlots = 0;
        for (long f = 0; f < 300; f++)
        {
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            Assert.Equal(2, frame.Layers.Count);
            maxSlots = Math.Max(maxSlots, offline.Readers.Slots);
        }

        // El primero: 10 referencias + 8 fijas + 8 por delante = 26; al segundo le quedan 22 superficies: 4 por delante.
        Assert.Equal([16, 12], backend.Videos.Select(v => v.ExtraSurfaces).Order().Reverse());
        Assert.True(offline.PeakDecoderVramBytes <= budget, $"{offline.PeakDecoderVramBytes / Surface} superficies");
        Assert.True(maxSlots <= 8 + 4 + 2, $"{maxSlots} cuadros guardados");
        Assert.Equal(0, offline.VramOverruns);
    }

    [Fact]
    public void Missing_content_fails_before_the_first_frame_with_the_list()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(Rational.Fps30, backend, content);
        content.ReadyLuts.Clear();
        content.Transitions.Clear();
        using OfflineTimeline offline = OfflineProjects.Open(b, backend,
            new OfflineTimelineOptions { Content = content, ContentTimeout = TimeSpan.FromMilliseconds(50) });

        ExportException error = Assert.Throws<ExportException>(() => offline.Render(0, CancellationToken.None));

        Assert.Equal(ExportErrorCode.ContentMissing, error.Code);
        Assert.True(error.CanContinueWithout);
        Assert.Contains(OfflineProjects.Lut, error.Message, StringComparison.Ordinal);
        Assert.Contains(OfflineProjects.PresetReference, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, offline.MissingContent);
        Assert.Empty(backend.Videos);
    }

    [Fact]
    public void Continuing_without_the_missing_content_renders_like_the_preview_without_it()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(Rational.Fps30, backend, content);
        content.ReadyLuts.Clear();
        content.Transitions.Clear();
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions
        {
            Content = content,
            ContentTimeout = TimeSpan.FromMilliseconds(20),
            MissingContent = MissingContentPolicy.ContinueWithout,
        });

        IReadOnlyList<MissingContentItem> missing = offline.Prepare(Time.Zero, offline.Duration, CancellationToken.None);

        Assert.Equal([(OfflineProjects.Lut, "ntsc", false), (OfflineProjects.PresetReference, "mueve", true)],
            missing.Select(m => (m.Reference, m.ClipId, m.IsTransition)));
        using CompositionFrame frame = offline.Render(Time.FromSeconds(1).ToFrames(Rational.Fps30), CancellationToken.None);
        LayerFrame graded = frame.Layers.Single(l => l.ClipId == "ntsc");
        Assert.Null(graded.Effects[0].Resource);
    }

    [Fact]
    public void A_lut_that_is_still_loading_is_awaited_before_the_first_frame()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(Rational.Fps30, backend, content);
        content.ReadyLuts.Clear();
        using var loader = new Timer(_ =>
        {
            content.ReadyLuts.Add(OfflineProjects.Lut);
            content.RaiseChanged();
        }, null, 100, Timeout.Infinite);
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Content = content });

        using CompositionFrame frame = offline.Render(Time.FromSeconds(1).ToFrames(Rational.Fps30), CancellationToken.None);

        Assert.Equal(0, offline.MissingContent);
        Assert.Equal(new ResourceKey(OfflineProjects.Lut), frame.Layers.Single(l => l.ClipId == "ntsc").Effects[0].Resource);
    }

    [Fact]
    public async Task An_awaitable_content_is_waited_on_instead_of_polled()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2));
        EffectInstance color = TimelineEffectsTests.Effect(BuiltInEffects.Color);
        color.Options[BuiltInEffects.ColorOptions.Lut] = OfflineProjects.Lut;
        clip.Effects.Add(color);
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Color));
        var content = new AwaitableContent();
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Content = content });

        Task<IReadOnlyList<MissingContentItem>> prepare = Task.Run(() => offline.Prepare(Time.Zero, offline.Duration, CancellationToken.None));
        Thread.Sleep(50);
        Assert.False(prepare.IsCompleted);
        content.Finish(loaded: false);

        Assert.Equal(ExportErrorCode.ContentMissing, (await Assert.ThrowsAsync<ExportException>(() => prepare)).Code);
    }

    [Fact]
    public void A_missing_media_file_fails_before_decoding()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(2));
        b.AddVideo(1, "b", S(5), Time.Zero, S(2));
        string missing = TimelineBuilder.PathOf("b");
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, mediaExists: p => p != missing);

        Assert.Empty(offline.Prepare(Time.Zero, S(4), CancellationToken.None));
        ExportException error = Assert.Throws<ExportException>(() => offline.Prepare(Time.Zero, offline.Duration, CancellationToken.None));

        Assert.Equal(ExportErrorCode.MediaMissing, error.Code);
        Assert.Equal(missing, error.Culprit);
        Assert.False(error.CanContinueWithout);
        Assert.Empty(backend.Videos);
    }

    [Fact]
    public void A_file_that_does_not_open_or_a_frame_that_does_not_decode_fails_with_its_code_and_frame()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(4));
        b.AddVideo(1, "b", S(2), Time.Zero, S(2));
        backend.FailOpen.Add(TimelineBuilder.PathOf("b"));
        backend.FailFrames.Add((TimelineBuilder.PathOf("a"), 30));
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { LookaheadFrames = 2 });

        for (long f = 0; f < 30; f++)
        {
            offline.Render(f, CancellationToken.None).Dispose();
        }

        ExportException decode = Assert.Throws<ExportException>(() => offline.Render(30, CancellationToken.None));
        Assert.Equal((ExportErrorCode.DecodeFailed, 30L, TimelineBuilder.PathOf("a")), (decode.Code, decode.Frame!.Value, decode.Culprit));

        ExportException open = Assert.Throws<ExportException>(() => offline.Render(60, CancellationToken.None));
        Assert.Equal((ExportErrorCode.MediaMissing, 60L, TimelineBuilder.PathOf("b")), (open.Code, open.Frame!.Value, open.Culprit));
        offline.Dispose();
        Assert.Equal(0, backend.Live.Value);
    }

    [Fact]
    public void Invalid_requests_and_options_are_rejected()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => OfflineProjects.Open(b, backend, new OfflineTimelineOptions { LookaheadFrames = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => OfflineProjects.Open(b, backend, new OfflineTimelineOptions { VramBudgetBytes = 0 }));
        OfflineTimeline offline = OfflineProjects.Open(b, backend);
        Assert.Throws<ArgumentOutOfRangeException>(() => offline.Render(30, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => offline.RenderAt(S(1), CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => offline.Prepare(S(1), S(1), CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => offline.Render(0, new CancellationToken(canceled: true)));
        offline.Dispose();
        Assert.Throws<ObjectDisposedException>(() => offline.Render(0, CancellationToken.None));
    }

    /// <summary>Cuadro del origen que se ve en el cuadro <paramref name="frame"/> de la secuencia, con fracciones exactas.</summary>
    private static long ExactSourceFrame(Clip clip, Rational sourceRate, long frame, Rational sequenceRate)
    {
        // t = frame·den/num; origen = SourceIn + (t − Start), en segundos como fracción de ticks.
        System.Numerics.BigInteger tps = Time.TicksPerSecond;
        System.Numerics.BigInteger num = (frame * (System.Numerics.BigInteger)sequenceRate.Den * tps)
            + (((System.Numerics.BigInteger)clip.SourceIn.Ticks - clip.Start.Ticks) * sequenceRate.Num);
        System.Numerics.BigInteger den = sequenceRate.Num * tps;
        return (long)System.Numerics.BigInteger.Divide(num * sourceRate.Num, den * sourceRate.Den);
    }

    private static CompositionFrame Settle(TimelineSession session)
    {
        for (int i = 0; ; i++)
        {
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
            Assert.True(i < 20, "La vista previa no llegó a la composición completa.");
        }
    }

    private sealed class AwaitableContent : IAwaitableEffectContent
    {
        private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _ready;

        public event EventHandler? Changed;

        public bool TryGetLut(ResourceKey key, out LutData? lut)
        {
            lut = null;
            return _ready;
        }

        public TransitionCurves? FindTransition(string reference) => null;

        public Task WhenLutLoadedAsync(ResourceKey key) => _loaded.Task;

        public void Finish(bool loaded)
        {
            _ready = loaded;
            _loaded.SetResult();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
