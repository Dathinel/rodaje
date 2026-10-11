using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Export;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Offline;

/// <summary>
/// El motor fuera de línea con FFmpeg, D3D11VA y el compositor de verdad: igual a la vista previa píxel a píxel, cada
/// capa con el número de cuadro que dibuja el medio, y cuántos cuadros por segundo da sin codificar.
/// </summary>
public sealed class OfflineHardwareTests(ITestOutputHelper output)
{
    private static readonly MediaSpec[] Layers =
    [
        new("capas-640-24", 640, 360, Rational.Fps24, TimeSpan.FromSeconds(14)) { GopLength = 48, BFrames = 2 },
        new("capas-640-25", 640, 360, Rational.Fps25, TimeSpan.FromSeconds(14)) { GopLength = 50, BFrames = 2 },
        new("capas-640-2997", 640, 360, Rational.Ntsc30, TimeSpan.FromSeconds(14)) { GopLength = 60, BFrames = 2 },
    ];

    [HardwareFact(VideoCodec.H264, Audio = AudioCodec.Aac)]
    public void Every_frame_matches_the_settled_preview_pixel_for_pixel_on_another_device()
    {
        Project project = SpeedProject();
        using GpuDevice previewGpu = GpuDevice.Create();
        using GpuDevice exportGpu = GpuDevice.Create();
        using var backend = new TimelineHardwareTests.DeviceClockBackend();
        using TimelineSession preview = new(project, "sec", previewGpu, new TimelineSessionOptions(), backend);
        using OfflineTimeline offline = OfflineTimeline.Open(project, "sec", exportGpu);
        using var previewCompositor = new Compositor(previewGpu, offline.Width, offline.Height);
        using var exportCompositor = new Compositor(exportGpu, offline.Width, offline.Height);

        int worst = 0;
        long differing = 0;
        int transitions = 0;
        for (long f = 0; f < offline.FrameCount; f++)
        {
            preview.SeekToFrame(f);
            using (CompositionFrame shown = Settle(preview))
            {
                previewCompositor.RenderStrict(shown);
            }

            using (CompositionFrame exported = offline.Render(f, CancellationToken.None))
            {
                exportCompositor.RenderStrict(exported);
                transitions += exported.Layers.Count(l => l.Transition is not null);
            }

            byte[] a = previewCompositor.ReadbackRgba();
            byte[] b = exportCompositor.ReadbackRgba();
            for (int i = 0; i < a.Length; i++)
            {
                int d = Math.Abs(a[i] - b[i]);
                worst = Math.Max(worst, d);
                differing += d > 0 ? 1 : 0;
            }
        }

        output.WriteLine($"{offline.FrameCount} cuadros 1080p, {transitions} capas en transición; diferencia máxima {worst}, {differing} bytes distintos.");
        Assert.True(worst <= 1, $"Diferencia máxima {worst}.");
        Assert.True(transitions > 30);
    }

    [HardwareFact(VideoCodec.H264, Audio = AudioCodec.Aac)]
    public void Every_layer_shows_the_frame_its_code_says_with_speed_and_transitions()
    {
        Project project = SpeedProject();
        using GpuDevice gpu = GpuDevice.Create();
        using OfflineTimeline offline = OfflineTimeline.Open(project, "sec", gpu);
        TimelinePlan plan = TimelinePlan.Build(project, "sec", Environment.CurrentDirectory);

        int layers = 0;
        var failures = new List<string>();
        for (long f = 0; f < offline.FrameCount; f++)
        {
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            foreach (LayerFrame layer in frame.Layers.SelectMany(l => l.Transition is { } t ? new[] { l, t.Incoming } : [l]))
            {
                Assert.IsType<D3D11TexturePayload>(layer.Frame.Payload);
                LumaImage luma = FrameReadback.ReadLuma(layer.Frame);
                int? code = FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
                long expected = Expected(plan, layer, frame.TimelineTime);
                if (code != layer.Frame.Index || layer.Frame.Index != expected)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"cuadro {f}, {layer.ClipId}: índice {layer.Frame.Index}, código {code}, esperado {expected}"));
                }

                layers++;
            }
        }

        output.WriteLine($"{offline.FrameCount} cuadros, {layers} capas leídas por su código, {failures.Count} fallos; " +
            $"pico de VRAM de los decodificadores {offline.PeakDecoderVramBytes / (1024 * 1024)} MB.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    [HardwareFact(VideoCodec.H264, VideoCodec.Hevc, Audio = AudioCodec.Aac)]
    public void Disposing_mid_export_closes_the_decoders_and_returns_the_video_memory()
    {
        Project project = SpeedProject();
        using GpuDevice gpu = GpuDevice.Create();
        long? before = gpu.QueryLocalVideoMemoryUsage();
        var offline = OfflineTimeline.Open(project, "sec", gpu);
        for (long f = 0; f < 60; f++)
        {
            offline.Render(f, CancellationToken.None).Dispose();
        }

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => offline.Render(60, cancel.Token));
        var watch = Stopwatch.StartNew();
        offline.Dispose();
        watch.Stop();
        long? after = gpu.QueryLocalVideoMemoryUsage();

        output.WriteLine($"Cerrar: {watch.Elapsed.TotalMilliseconds:0} ms; VRAM del proceso {before / (1024 * 1024)} → {after / (1024 * 1024)} MB.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(0, offline.DecodersOpen);
        // Otras clases de pruebas usan la GPU en paralelo: la VRAM del proceso solo se exige en la pasada larga.
        if (Skip.LongRuns && before is { } b && after is { } a)
        {
            Assert.InRange(a - b, -50L * 1024 * 1024, 50L * 1024 * 1024);
        }
    }

    /// <summary>
    /// 1080p30 con dos pistas (H.264 1080p a 29,97 de fondo y HEVC 1080p a 25 en PiP con color y transformación),
    /// compuestas y convertidas a NV12 con el anillo de Staging, sin codificar.
    /// </summary>
    [HardwareFact(VideoCodec.H264, VideoCodec.Hevc, Audio = AudioCodec.Aac)]
    [Trait("Categoria", "TiempoReal")]
    public void Unencoded_1080p30_with_two_tracks_and_an_effect_runs_well_above_real_time()
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var sequence = new Sequence { Id = "sec", Name = "Rendimiento", FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
        var project = new Project { Id = "p", Name = "Rendimiento", Sequences = [sequence] };
        MediaSpec[] specs = [TestMediaPresets.H264Ntsc30BFrames, TestMediaPresets.Hevc25Mkv];
        for (int m = 0; m < specs.Length; m++)
        {
            var reference = new MediaRef { Id = $"m{m}", Path = generator.GetOrCreate(specs[m]).Path };
            MediaProbe.Probe(reference.Path).ApplyTo(reference);
            project.Media.Add(reference);
            var track = new Track { Id = $"v{m}", Kind = TrackKind.Video, Name = $"Video {m + 1}" };
            track.Clips.Add(new Clip { Id = $"c{m}", MediaId = reference.Id, Start = Time.Zero, SourceIn = S(1), SourceOut = S(19) });
            sequence.VideoTracks.Add(track);
        }

        Clip top = sequence.VideoTracks[1].Clips[0];
        top.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.5),
            (BuiltInEffects.TransformParameters.X, 400)));
        top.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Saturation, 0.4),
            (BuiltInEffects.ColorParameters.Contrast, 0.2)));
        Assert.Empty(ModelValidator.Validate(project));

        using GpuDevice gpu = GpuDevice.Create();
        using OfflineTimeline offline = OfflineTimeline.Open(project, "sec", gpu);
        using var compositor = new Compositor(gpu, offline.Width, offline.Height);
        var format = new ExportVideoFormat(1920, 1080, offline.FrameRate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        using var converter = new ExportFrameConverter(gpu, offline.Width, offline.Height, format);
        long read = 0;
        void Count(in ExportVideoFrame frame) => read++;

        offline.Prepare(Time.Zero, offline.Duration, CancellationToken.None);
        var engine = new Stopwatch();
        var watch = Stopwatch.StartNew();
        for (long f = 0; f < offline.FrameCount; f++)
        {
            engine.Start();
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            engine.Stop();
            compositor.RenderStrict(frame);
            while (converter.Pending == converter.Capacity)
            {
                converter.TryRead(Count);
            }

            converter.Submit(compositor.Texture, f);
        }

        while (converter.Pending > 0)
        {
            converter.TryRead(Count);
        }

        watch.Stop();
        double fps = offline.FrameCount / watch.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{offline.FrameCount} cuadros 1080p30 de 2 pistas con efecto: {fps:0} fps sin codificar ({watch.Elapsed.TotalMilliseconds / offline.FrameCount:0.00} ms por cuadro, " +
            $"{engine.Elapsed.TotalMilliseconds / offline.FrameCount:0.00} esperando al motor); espera de la bajada {converter.ReadWait.TotalMilliseconds:0} ms; " +
            $"{offline.DecodersOpen} decodificadores, pico de VRAM {offline.PeakDecoderVramBytes / (1024 * 1024)} MB."));
        Assert.Equal(offline.FrameCount, read);
        Assert.True(fps >= (Skip.LongRuns ? 150 : 30), $"{fps:0} fps");
    }

    /// <summary>
    /// Seis segundos a 1080p30 con los tres medios de 640×360: 2× y 0,5× con fundido, 1001/1000× con color y keyframes y
    /// «Bala» con viñeta y empuje, PiP en «trama» y congelado con barrido.
    /// </summary>
    private static Project SpeedProject()
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var b = new TimelineBuilder(3, 0);
        b.Project.Media.Clear();
        for (int m = 0; m < Layers.Length; m++)
        {
            var reference = new MediaRef { Id = $"m{m}", Path = generator.GetOrCreate(Layers[m]).Path };
            MediaProbe.Probe(reference.Path).ApplyTo(reference);
            b.Project.Media.Add(reference);
        }

        Clip fast = SpeedClips.WithRate(b.AddVideo(0, "m0", Time.Zero, S(1), S(2), "rapido"), new Rational(2, 1), S(1), S(7));
        Clip slow = SpeedClips.WithRate(b.AddVideo(0, "m1", fast.End, S(2), S(1), "lento"), new Rational(1, 2), S(2), S(3.5));
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, "rapido", "lento", S(0.6));

        Clip ntsc = SpeedClips.WithRate(b.AddVideo(1, "m2", S(0.5), S(1), S(2), "ntsc"), new Rational(1001, 1000), S(1), S(3.5));
        EffectInstance color = TimelineEffectsTests.Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Saturation, 0.5));
        color.Parameters[BuiltInEffects.ColorParameters.Exposure] = new Parameter
        {
            Keyframes = [new Keyframe(S(1), -0.5), new Keyframe(S(3), 0.8, Interpolation.EaseInOut)],
        };
        ntsc.Effects.Add(color);
        ntsc.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.6)));
        Clip bullet = SpeedClips.WithCurve(b.AddVideo(1, "m0", ntsc.End, S(5), S(2.5), "bala"), SpeedPreset.Bullet, S(2.2), S(5), S(8.5));
        bullet.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Vignette, (BuiltInEffects.VignetteParameters.Amount, 0.7)));
        bullet.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.6)));
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Push, "ntsc", "bala", S(0.5), track: 1)
            .Options[BuiltInTransitions.PushOptions.Direction] = "left";

        Clip pip = b.AddVideo(2, "m1", S(1), S(6), S(2.5), "pip");
        pip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.35),
            (BuiltInEffects.TransformParameters.X, -500), (BuiltInEffects.TransformParameters.Rotation, 8)));
        EffectInstance blend = TimelineEffectsTests.Effect(BuiltInEffects.Blend);
        blend.Options[BuiltInEffects.BlendOptions.Mode] = "screen";
        pip.Effects.Add(blend);
        SpeedClips.Frozen(b.AddVideo(2, "m2", pip.End, S(9), S(1), "congelado"), S(9), S(2));
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Wipe, "pip", "congelado", S(0.4), track: 2);
        foreach (Clip clip in b.Sequence.VideoTracks.SelectMany(t => t.Clips))
        {
            clip.Effects.ForEach(e => e.Id = $"{clip.Id}-{e.Type}");
        }

        var issues = ModelValidator.Validate(b.Project);
        Assert.True(issues.Count == 0, string.Join("; ", issues));
        return b.Project;
    }

    /// <summary>Cuadro que muestra la capa según el plan: el del clip del carril principal o el del compañero.</summary>
    private static long Expected(TimelinePlan plan, LayerFrame layer, Time time)
    {
        VideoLayerPlan lane = plan.Layers.Single(l => l.TrackIndex == layer.TrackIndex);
        VideoClipPlan clip = lane.ClipAt(time) is { } main && main.ClipId == layer.ClipId ? main : lane.Companion!.ClipAt(time)!;
        return clip.FrameAt(time);
    }

    private static CompositionFrame Settle(TimelineSession session)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "La vista previa no llegó a su cuadro en 10 s.");
            Thread.Sleep(1);
        }
    }
}
