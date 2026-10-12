using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Pausas de la vista previa en cortes con velocidad y transiciones (tarea 36, <c>docs/fase-6.md</c>): abrir un
/// decodificador de D3D11VA mientras suena crea el decodificador de video con el candado del contexto inmediato tomado,
/// y el hilo que compone espera ese candado. Estas pruebas exigen que reproduciendo solo se abra un decodificador cuando
/// hace falta de verdad: no si el que ya sirve el medio llega solo al corte, ni si hay uno ocioso del mismo medio.
/// </summary>
public sealed class TimelineCutPauseTests(ITestOutputHelper output)
{
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly TimeSpan Refresh60 = TimeSpan.FromTicks(166_667);

    /// <summary>
    /// La pista principal de la escena de aceptación, dos bloques: 1×, 2×, 0,5× y 1001/1000×, cada uno donde terminó el
    /// material del anterior, con fundido, cortinilla y empuje entre ellos. El decodificador que ya avanza por el medio llega
    /// solo a la entrada del 2× (antes, su pre-roll abría otro a los 9,75 s). La cortinilla hacia el 0,5× sí necesita un
    /// segundo decodificador (el que entra va por delante del que sale) y se abre una sola vez: en los cortes siguientes
    /// queda ocioso y se reutiliza.
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    public void Contiguous_cuts_with_speed_and_transitions_open_at_most_one_decoder_in_the_whole_run(int preRollMs)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 60 };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Rational.Ntsc30, 60_000, backend: backend);
        var clips = new List<Clip>();
        Time source = S(1);
        Time start = Time.Zero;
        for (int block = 0; block < 2; block++)
        {
            Clip plain = b.AddVideo(0, "a", start, source, S(10), $"b{block}-1x");
            Clip fast = Fit(b.AddVideo(0, "a", plain.End, source + S(10), S(1), $"b{block}-2x"), new Rational(2, 1), source + S(10), source + S(30));
            Clip slow = Fit(b.AddVideo(0, "a", fast.End, source + S(30), S(1), $"b{block}-0,5x"), new Rational(1, 2), source + S(30), source + S(35));
            Clip ntsc = Fit(b.AddVideo(0, "a", slow.End, source + S(35), S(1), $"b{block}-1001"), new Rational(1001, 1000), source + S(35), source + S(45.01));
            TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, plain.Id, fast.Id, S(0.5));
            TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Wipe, fast.Id, slow.Id, S(0.5));
            TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Push, slow.Id, ntsc.Id, S(0.5));
            clips.AddRange([plain, fast, slow, ntsc]);
            source += S(45.01);
            start = ntsc.End;
        }

        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend,
            new TimelineSessionOptions { EnableAudio = false, TimeProvider = time, PreRoll = TimeSpan.FromMilliseconds(preRollMs) });
        session.Seek(S(7));
        Settle(session, backend);
        int before = backend.Videos.Count;
        session.ResetStats();

        session.Play();
        var opened = new List<double>();
        int wrong = Play(session, backend, time, clips[^1].Start + S(2), clips, opened);

        TimelineStats stats = session.Stats;
        output.WriteLine($"pre-roll {preRollMs} ms: aperturas al reproducir en [{string.Join(", ", opened.Select(t => $"{t:0.00} s"))}], " +
            $"seeks {backend.Videos.Sum(v => v.Seeks)}, decodificados {backend.Videos.Sum(v => v.DecodedFrames)}, tarde {stats.LateRefreshes}, " +
            $"perdidos {stats.FramesDropped}, cortes {stats.Cuts} (pre-roll {stats.PreRollHits}/{stats.PreRollMisses}), " +
            $"transiciones {stats.TransitionPreRollHits}/{stats.TransitionPreRollMisses}");
        Assert.Equal(0, wrong);
        Assert.Equal(opened.Count, stats.DecodersOpenedWhilePlaying);
        Assert.Equal(before + opened.Count, backend.Videos.Count);
        Assert.True(opened.Count <= 1, $"Se abrieron {opened.Count} decodificadores reproduciendo.");
        Assert.All(opened, t => Assert.InRange(t, 17.0, 20.25));
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.PreRollMisses);
    }

    /// <summary>
    /// Un PiP que vuelve al principio de su material en cada corte (como el izquierdo de la escena: 1× y 2× alternados,
    /// cada clip desde el segundo 1) necesita un segundo decodificador para el pre-roll. Se abre en el primer corte y los
    /// siguientes lo reutilizan aunque haya quedado ocioso más que <c>DecoderIdleTimeout</c>: reabrirlo en cada corte
    /// era una pausa por corte.
    /// </summary>
    [Fact]
    public void A_pip_that_jumps_back_at_every_cut_reuses_its_idle_decoder_instead_of_opening_one_per_cut()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 25 };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("fondo", Fps30, 40_000, backend: backend);
        b.AddMedia("pip", Rational.Fps25, 40_000, backend: backend);
        b.AddVideo(0, "fondo", Time.Zero, Time.Zero, S(60), "fondo");
        var clips = new List<Clip> { b.Sequence.VideoTracks[0].Clips[0] };
        Time cursor = Time.Zero;
        for (int n = 0; n < 5; n++)
        {
            Clip clip = b.AddVideo(1, "pip", cursor, S(1), S(18), $"pip{n}");
            if (n % 2 == 1)
            {
                Fit(clip, new Rational(2, 1), S(1), S(19));
            }

            clips.Add(clip);
            cursor = clip.End;
        }

        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend);
        session.ResetStats();

        session.Play();
        int wrong = Play(session, backend, time, S(58), clips);

        TimelineStats stats = session.Stats;
        int pipDecoders = backend.Videos.Count(v => v.Path.EndsWith("pip.mp4", StringComparison.Ordinal));
        output.WriteLine($"decodificadores del PiP {pipDecoders}, abiertos al reproducir {stats.DecodersOpenedWhilePlaying}, " +
            $"cortes {stats.Cuts} (pre-roll {stats.PreRollHits}/{stats.PreRollMisses}), tarde {stats.LateRefreshes}, perdidos {stats.FramesDropped}");
        Assert.Equal(0, wrong);
        Assert.Equal(4, stats.Cuts);
        Assert.Equal(4, stats.PreRollHits);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(2, pipDecoders);
        Assert.Equal(1, stats.DecodersOpenedWhilePlaying);
    }

    /// <summary>
    /// Un decodificador ocioso se guarda reproduciendo si su medio vuelve en un corte próximo (y el corte lo reutiliza);
    /// en pausa se cierra tras <c>DecoderIdleTimeout</c> como siempre.
    /// </summary>
    [Fact]
    public void Idle_decoders_kept_for_a_coming_cut_close_when_paused()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 40_000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, S(100), S(4), "lejos");
        b.AddVideo(0, "a", S(4), S(1), S(8), "cerca");
        b.AddVideo(0, "a", S(12), S(104), S(20), "otra vez");
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend);
        session.Play();

        // El del primer clip queda ocioso a los 4 s; a los 10 s pasó el plazo, pero el corte de los 12 s lo va a usar.
        Play(session, backend, time, S(10), []);
        Assert.Equal(2, session.Stats.DecodersOpen);
        Play(session, backend, time, S(13), []);
        Assert.Equal((2, 0), (backend.Videos.Count, session.Stats.DecodersClosed));

        session.Pause();
        for (int i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            session.Tick().Dispose();
            backend.RunDecoders();
        }

        Assert.Equal(1, session.Stats.DecodersOpen);
    }

    private static Clip Fit(Clip clip, Rational rate, Time sourceIn, Time sourceOut)
    {
        clip.Speed = new ClipSpeed { Rate = rate };
        ClipTimeMap.Fit(clip.Start, sourceIn, sourceOut, clip.Speed).ApplyTo(clip);
        return clip;
    }

    /// <summary>
    /// Reproduce a 60 Hz hasta <paramref name="until"/>; devuelve los refrescos con un cuadro distinto del debido fuera de
    /// las transiciones y anota en <paramref name="opened"/> dónde se abrió cada decodificador.
    /// </summary>
    private static int Play(TimelineSession session, FakeTimelineBackend backend, ManualTimeProvider time, Time until, IReadOnlyList<Clip> clips,
        List<double>? opened = null)
    {
        int wrong = 0;
        while (session.Position < until)
        {
            time.Advance(Refresh60);
            using (CompositionFrame frame = session.Tick())
            {
                foreach (LayerFrame layer in frame.Layers.Where(l => l.Transition is null))
                {
                    Clip? clip = clips.FirstOrDefault(c => c.Id == layer.ClipId);
                    if (clip is not null && frame.TimelineTime >= clip.Start && frame.TimelineTime < clip.End
                        && layer.Frame.Index != clip.TimeMap.Source(frame.TimelineTime).ToFrames(MediaRate(backend, layer)))
                    {
                        wrong++;
                    }
                }
            }

            int count = backend.Videos.Count;
            backend.RunDecoders();
            if (backend.Videos.Count > count)
            {
                opened?.Add(session.Position.ToSeconds());
            }
        }

        return wrong;
    }

    private static Rational MediaRate(FakeTimelineBackend backend, LayerFrame layer)
    {
        string path = ((FakeFramePayload)layer.Frame.Payload!).Path;
        return backend.Videos.First(v => v.Path == path).Stream.FrameRate;
    }

    private static void Settle(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; i < 20 && session.IsSeekPending; i++)
        {
            backend.RunDecoders();
            session.Tick().Dispose();
        }

        Assert.False(session.IsSeekPending);
    }
}
