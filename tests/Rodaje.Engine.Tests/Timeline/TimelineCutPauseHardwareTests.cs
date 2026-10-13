using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using DeviceClockBackend = Rodaje.Engine.Tests.Timeline.TimelineHardwareTests.DeviceClockBackend;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Pausas de la vista previa en los cortes (tarea 36) con FFmpeg, D3D11VA y el compositor reales: una pista principal
/// como la de la escena de aceptación (1×, 2×, 0,5× y 1001/1000× seguidos en el material, con fundido, cortinilla y
/// empuje) y un PiP que vuelve al principio de su material con un fundido. Se mide cada refresco (<c>Tick</c> más la
/// composición, que toma el candado del contexto inmediato) reproduciendo a 60 Hz.
/// </summary>
/// <remarks>
/// El corte del PiP necesita un segundo decodificador: abrirlo es la pausa que queda (FFmpeg crea el decodificador de
/// D3D11VA con el candado del contexto tomado). Se informa como <c>MEDIDA</c> y no se exige; todo lo demás, sí.
/// </remarks>
[Collection(GpuHardwareGroup.Name)]
public sealed class TimelineCutPauseHardwareTests(ITestOutputHelper output)
{
    private static readonly Rational SequenceRate = Rational.Fps30;
    private static readonly Time PipCut = Time.FromSeconds(13);

    [HardwareFact(VideoCodec.H264)]
    [Trait("Categoria", "TiempoReal")]
    public void Cuts_with_speed_and_transitions_never_hold_a_refresh_past_50_ms()
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var sequence = new Sequence { Id = "sec", Name = "Pausas", FrameRate = SequenceRate, Width = 1920, Height = 1080 };
        var project = new Project { Id = "p", Name = "Pausas en los cortes", Sequences = [sequence] };
        MediaRef main = AddMedia(project, generator, TestMediaPresets.H264Ntsc30BFrames, "principal");
        MediaRef pip = AddMedia(project, generator, TestMediaPresets.H264Ntsc24Aac, "pip");
        var v0 = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Principal" };
        var v1 = new Track { Id = "v1", Kind = TrackKind.Video, Name = "PiP" };
        sequence.VideoTracks.AddRange([v0, v1]);

        Clip plain = Add(v0, main, "1x", Time.Zero, S(1), S(4), null);
        Clip fast = Add(v0, main, "2x", plain.End, S(4), S(12), new Rational(2, 1));
        Clip slow = Add(v0, main, "0,5x", fast.End, S(12), S(13.5), new Rational(1, 2));
        Clip ntsc = Add(v0, main, "1001", slow.End, S(13.5), S(19.5), new Rational(1001, 1000));
        Transition(v0, BuiltInTransitions.Crossfade, plain, fast);
        Transition(v0, BuiltInTransitions.Wipe, fast, slow);
        Transition(v0, BuiltInTransitions.Push, slow, ntsc);
        Clip first = Add(v1, pip, "pip0", Time.Zero, S(2), S(2) + PipCut, null);
        Clip second = Add(v1, pip, "pip1", first.End, S(2), S(2) + (ntsc.End - first.End), null);
        foreach (Clip clip in new[] { first, second })
        {
            clip.Effects.Add(new EffectInstance
            {
                Id = clip.Id + "-pip",
                Type = BuiltInEffects.Transform,
                Parameters = new()
                {
                    [BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = 0.5 },
                    [BuiltInEffects.TransformParameters.X] = new Parameter { Value = 480 },
                },
            });
        }

        Transition(v1, BuiltInTransitions.Crossfade, first, second);
        Assert.Empty(ModelValidator.Validate(project));

        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using TimelineSession session = new(project, "sec", gpu, new TimelineSessionOptions { UseProxies = ProxyUsage.Never }, backend);
        using var compositor = new Compositor(gpu, 1920, 1080);
        Func<LayerFrame, SourceGeometry> geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);
        var settle = Stopwatch.StartNew();
        while (true)
        {
            using CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                // Con los shaders de efectos y transiciones ya creados: compilarlos no es lo que se mide.
                compositor.RenderStrict(frame, geometry);
                break;
            }

            Assert.True(settle.Elapsed < TimeSpan.FromSeconds(10), "Las capas no llegaron a su cuadro en 10 s.");
            Thread.Sleep(1);
        }

        session.ResetStats();
        session.Play();
        Time origin = backend.Clock.Position;
        long lastRefresh = ((ntsc.End - S(0.5)).Ticks / Time.FromFrames(1, Rational.Fps60).Ticks) - 2;
        var pacer = new WallClockPacer(60);
        var stalls = new List<(double At, double Ms)>();
        double worst = 0;
        for (long slot = pacer.Next(); slot <= lastRefresh; slot = pacer.Next())
        {
            backend.Clock.Advance(origin + Time.FromFrames(slot, Rational.Fps60) - backend.Clock.Position);
            long start = Stopwatch.GetTimestamp();
            using (CompositionFrame frame = session.Tick())
            {
                compositor.Render(frame, geometry);
            }

            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            worst = Math.Max(worst, ms);
            if (ms > 50)
            {
                stalls.Add((session.Position.ToSeconds(), ms));
            }
        }

        TimelineStats stats = session.Stats;
        // El segundo decodificador del PiP se abre con el pre-roll de su fundido, entre 2 s antes del corte y el corte.
        bool PipOpen((double At, double Ms) s) => s.At >= (PipCut - S(2.5)).ToSeconds() && s.At <= (PipCut + S(0.5)).ToSeconds();
        string Describe(IEnumerable<(double At, double Ms)> list) =>
            string.Join(", ", list.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Ms:0} ms en {s.At:0.00} s")));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"MEDIDA refresco_max_ms = {worst:0.0}; aperturas reproduciendo {stats.DecodersOpenedWhilePlaying}; tarde {stats.LateRefreshes}, " +
            $"perdidos {stats.FramesDropped}, saltados por el bucle {pacer.Skipped}, cortes {stats.Cuts} (pre-roll {stats.PreRollHits}/{stats.PreRollMisses}), " +
            $"transiciones {stats.TransitionPreRollHits}/{stats.TransitionPreRollMisses}, seeks de keyframe {stats.DecoderSeeks}."));
        output.WriteLine($"MEDIDA pausa_apertura_pip = [{Describe(stalls.Where(PipOpen))}]");
        output.WriteLine($"Otras pausas de más de 50 ms: [{Describe(stalls.Where(s => !PipOpen(s)))}]");
        Assert.Equal(1, stats.DecodersOpenedWhilePlaying);
        Assert.True(!stalls.Any(s => !PipOpen(s)), $"Refrescos de más de 50 ms fuera de la apertura del PiP: {Describe(stalls.Where(s => !PipOpen(s)))}");
        Assert.Equal(0, stats.DecodeErrors);
    }

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static MediaRef AddMedia(Project project, TestMediaGenerator generator, MediaSpec spec, string id)
    {
        GeneratedMedia media = generator.GetOrCreate(spec);
        var reference = new MediaRef { Id = id, Path = media.Path };
        MediaProbe.Probe(media.Path).ApplyTo(reference);
        project.Media.Add(reference);
        return reference;
    }

    private static Clip Add(Track track, MediaRef media, string id, Time start, Time sourceIn, Time sourceOut, Rational? rate)
    {
        var clip = new Clip { Id = id, MediaId = media.Id, Start = start, SourceIn = sourceIn, SourceOut = sourceOut };
        if (rate is { } r)
        {
            clip.Speed = new ClipSpeed { Rate = r };
            ClipTimeMap.Fit(start, sourceIn, sourceOut, clip.Speed).ApplyTo(clip);
        }

        track.Clips.Add(clip);
        track.SortClips();
        return clip;
    }

    private static void Transition(Track track, string type, Clip from, Clip to) =>
        track.Transitions.Add(new TransitionInstance { Id = $"t-{from.Id}", Type = type, From = from.Id, To = to.Id, Duration = Time.FromSeconds(0.5) });
}
