using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>El carril generado: títulos sin decodificador, con su entrada y salida pegadas a los bordes del clip.</summary>
public sealed class TimelineTitleTests(ITestOutputHelper output)
{
    private const double Tolerance = 1e-9;

    /// <summary>Entrada de 0,4 s (opacidad 0 → 1 y desde 40 px más abajo, con salida suave) y salida de 0,3 s (opacidad 1 → 0).</summary>
    internal static TextStyle Animated() => new()
    {
        Size = 96,
        In = new StyleAnimation
        {
            DurationSeconds = 0.4,
            Curves = new Dictionary<string, List<StyleKey>>
            {
                [T.Opacity] = [new StyleKey(0, 0, Interpolation.EaseOut), new StyleKey(0.4, 1)],
                [T.Y] = [new StyleKey(0, 40, Interpolation.EaseOut), new StyleKey(0.4, 0)],
            },
        },
        Out = new StyleAnimation
        {
            DurationSeconds = 0.3,
            Curves = new Dictionary<string, List<StyleKey>> { [T.Opacity] = [new StyleKey(0, 1), new StyleKey(0.3, 0)] },
        },
    };

    [Fact]
    public void Titles_go_to_the_generated_lane_without_decoders_or_audio()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 1);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4), "video");
        b.AddAudio(0, "m", Time.Zero, Time.Zero, S(4), "audio");
        b.AddTitle(1, S(1), S(2), "Hola\nmundo", id: "titulo");
        b.AddTitle(0, S(4), S(2), "Después", id: "despues");
        Assert.Empty(ModelValidator.Validate(b.Project));

        TimelinePlan plan = b.Plan();
        Assert.Empty(plan.Layers[1].Clips);
        Assert.Equal("titulo", Assert.Single(plan.Layers[1].Generated).ClipId);
        Assert.Equal("video", Assert.Single(plan.Layers[0].Clips).ClipId);
        Assert.Equal("despues", Assert.Single(plan.Layers[0].Generated).ClipId);
        Assert.Equal("audio", Assert.Single(Assert.Single(plan.Audio.Tracks).Clips).ClipId);

        using TimelineSession session = TimelineSessionTests.Open(b, backend);
        session.Seek(S(1.5));
        Settle(session, backend);
        using (CompositionFrame frame = session.Tick())
        {
            Assert.Equal(2, frame.Layers.Count);
            LayerFrame title = frame.Layers[1];
            Assert.Equal("titulo", title.ClipId);
            Assert.Equal(1, title.TrackIndex);
            Assert.Equal("Hola\nmundo", title.Generated!.Text);
            Assert.Null(title.Frame.Payload);
            Assert.Null(frame.Layers[0].Generated);
        }

        session.Seek(S(5));
        Settle(session, backend);
        using (CompositionFrame after = session.Tick())
        {
            Assert.Equal("despues", Assert.Single(after.Layers).ClipId);
        }

        TimelineStats stats = session.Stats;
        Assert.Equal(0, stats.FailedMedia);
        Assert.Equal(0, stats.DecodeErrors);
        Assert.True(stats.DecodersOpen <= 1);
    }

    [Fact]
    public void A_sequence_of_only_titles_plays_without_opening_a_decoder()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 1);
        b.AddTitle(0, Time.Zero, S(2), "Solo");
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        for (int i = 0; i < 5; i++)
        {
            backend.RunDecoders();
            using CompositionFrame frame = session.Tick();
            Assert.NotNull(Assert.Single(frame.Layers).Generated);
        }

        Assert.False(session.IsSeekPending);
        Assert.Equal(0, session.Stats.DecodersOpen);
        Assert.Equal(0, session.Stats.Seeks);
        Assert.Single(session.GeneratedContents());
    }

    [Fact]
    public void Entrance_and_exit_are_anchored_to_the_clip_edges_and_add_to_the_clip_position()
    {
        var b = new TimelineBuilder(1, 0);
        Clip clip = b.AddTitle(0, S(10), S(3), "Título", Animated());
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Y, 380)));
        GeneratedClipPlan title = Single(b);

        LayerTransform start = title.TransformAt(S(10));
        Assert.Equal(420, start.Y, Tolerance);
        Assert.Equal(0, start.Opacity, Tolerance);
        Assert.Equal(1 - Math.Pow(0.5, 3), title.TransformAt(S(10.2)).Opacity, 6);
        Assert.Equal(new LayerTransform(0, 380, 1, 0, 1), title.TransformAt(S(10.4)));
        Assert.Equal(new LayerTransform(0, 380, 1, 0, 1), title.TransformAt(S(12.7) - Time.FromTicks(1)));
        Assert.Equal(0.5, title.TransformAt(S(12.85)).Opacity, 6);
        Assert.True(title.TransformAt(S(13) - Time.FromTicks(1)).Opacity < 1e-6);
    }

    [Fact]
    public void Trimming_the_start_repeats_the_entrance_and_extending_moves_the_exit()
    {
        var b = new TimelineBuilder(1, 0);
        Clip clip = b.AddTitle(0, S(10), S(3), "Título", Animated(), id: "t");
        Edits.Trim(b.Sequence.Id, "t", TrimEdge.In, S(1)).Apply(b.Project);
        Assert.Equal(S(11), clip.Start);
        Assert.Equal(0, Single(b).TransformAt(S(11)).Opacity, Tolerance);
        Assert.Equal(1, Single(b).TransformAt(S(11.4)).Opacity, Tolerance);

        Edits.Trim(b.Sequence.Id, "t", TrimEdge.Out, S(2)).Apply(b.Project);
        GeneratedClipPlan longer = Single(b);
        Assert.Equal(S(15), longer.End);
        Assert.Equal(1, longer.TransformAt(S(12.9)).Opacity, Tolerance);
        Assert.Equal(0.5, longer.TransformAt(S(14.85)).Opacity, 6);
    }

    [Fact]
    public void A_clip_shorter_than_both_animations_shrinks_them_to_fill_it()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddTitle(0, S(1), S(0.35), "Corto", Animated());
        GeneratedClipPlan title = Single(b);

        TitleAnimation animation = title.Animation!;
        Assert.Equal(S(0.35), animation.InDuration + animation.OutDuration);
        Assert.Equal(0.2, animation.InDuration.ToSeconds(), 9);

        // A mitad de la entrada encogida, la curva va por su mitad: la forma se conserva.
        Assert.Equal(1 - Math.Pow(0.5, 3), title.TransformAt(S(1.1)).Opacity, 6);
        Assert.Equal(0.5, title.TransformAt(S(1.275)).Opacity, 6);
    }

    /// <summary>
    /// Dividir no cambia ningún cuadro: cada trozo sigue la animación del título entero, también con el corte en plena
    /// entrada o salida, con dos cortes y con un clip tan corto que las animaciones se encogen.
    /// </summary>
    [Theory]
    [InlineData(3, 0.2, null)]
    [InlineData(3, 2.85, null)]
    [InlineData(3, 1.5, null)]
    [InlineData(3, 0.1, 2.8)]
    [InlineData(3, 0.3, 0.35)]
    [InlineData(0.5, 0.1, null)]
    [InlineData(0.5, 0.1, 0.4)]
    public void Splitting_a_title_keeps_every_frame_as_it_was(double seconds, double cut, double? secondCut)
    {
        var b = new TimelineBuilder(1, 0);
        Clip clip = b.AddTitle(0, S(10), S(seconds), "Título", Animated(), id: "t");
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Rotation, 30), (T.Scale, 1.5), (T.Y, 100)));
        GeneratedClipPlan whole = Single(b);
        Time step = S(1.0 / 240);
        var expected = new List<(Time At, LayerTransform Transform)>();
        for (Time at = whole.Start; at < whole.End; at += step)
        {
            expected.Add((at, whole.TransformAt(at)));
        }

        SequenceEditCommand split = Edits.Split(b.Sequence.Id, "t", S(10 + cut));
        split.Apply(b.Project);
        string right = Assert.Single(split.CreatedIds);
        if (secondCut is { } again)
        {
            Edits.Split(b.Sequence.Id, again > cut ? right : "t", S(10 + again)).Apply(b.Project);
        }

        IReadOnlyList<GeneratedClipPlan> pieces = b.Plan().Layers[0].Generated;
        Assert.Equal(secondCut is null ? 2 : 3, pieces.Count);
        double worst = 0;
        foreach ((Time at, LayerTransform want) in expected)
        {
            LayerTransform got = Assert.Single(pieces, p => p.Covers(at)).TransformAt(at);
            worst = Math.Max(worst, Math.Abs(got.Opacity - want.Opacity));
            Assert.Equal(want.Opacity, got.Opacity, 9);
            Assert.Equal(want.X, got.X, 6);
            Assert.Equal(want.Y, got.Y, 6);
            Assert.Equal(want.Scale, got.Scale, 9);
            Assert.Equal(want.RotationDegrees, got.RotationDegrees, 9);
        }

        output.WriteLine($"{expected.Count} instantes, mayor diferencia de opacidad {worst:E2}");
    }

    /// <summary>
    /// «Convertir en keyframes» deja cada cuadro como estaba aunque el clip esté escalado o girado: el delta de la
    /// animación gira y se escala con el clip igual que en el motor. También en un trozo dividido en plena entrada.
    /// </summary>
    [Theory]
    [InlineData(1, 0, null)]
    [InlineData(2, 0, null)]
    [InlineData(1, 90, null)]
    [InlineData(2, 90, null)]
    [InlineData(0.5, -30, null)]
    [InlineData(2, 90, 0.2)]
    public void Converting_the_animation_to_keyframes_keeps_every_frame(double scale, double rotation, double? cut)
    {
        var b = new TimelineBuilder(1, 0);
        Clip clip = b.AddTitle(0, S(10), S(3), "Título", Animated(), id: "t");
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Rotation, rotation), (T.Scale, scale), (T.X, 100), (T.Y, 50)));
        string target = "t";
        if (cut is { } at)
        {
            SequenceEditCommand split = Edits.Split(b.Sequence.Id, "t", S(10 + at));
            split.Apply(b.Project);
            target = split.CreatedIds[0];
        }

        GeneratedClipPlan before = Assert.Single(b.Plan().Layers[0].Generated, p => p.ClipId == target);
        var expected = new List<(Time At, LayerTransform Transform)>();
        for (Time t = before.Start; t < before.End; t += S(1.0 / 240))
        {
            expected.Add((t, before.TransformAt(t)));
        }

        Edits.ConvertTitleAnimation(b.Sequence.Id, target).Apply(b.Project);
        GeneratedClipPlan after = Assert.Single(b.Plan().Layers[0].Generated, p => p.ClipId == target);
        Assert.Null(after.Animation);
        foreach ((Time t, LayerTransform want) in expected)
        {
            LayerTransform got = after.TransformAt(t);
            Assert.Equal(want.X, got.X, 6);
            Assert.Equal(want.Y, got.Y, 6);
            Assert.Equal(want.Scale, got.Scale, 9);
            Assert.Equal(want.RotationDegrees, got.RotationDegrees, 9);
            Assert.Equal(want.Opacity, got.Opacity, 9);
        }
    }

    [Fact]
    public void The_animation_delta_turns_and_scales_with_the_clip()
    {
        var b = new TimelineBuilder(1, 0);
        Clip clip = b.AddTitle(0, Time.Zero, S(3), "Gira", Animated());
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Rotation, 90), (T.Scale, 2), (T.X, 100)));

        LayerTransform start = Single(b).TransformAt(Time.Zero);
        // 40 px hacia abajo en la capa: girados 90° en sentido horario son 40 px a la izquierda, y al doble de escala, 80.
        Assert.Equal(100 - 80, start.X, 9);
        Assert.Equal(0, start.Y, 9);
        Assert.Equal(2, start.Scale, Tolerance);
        Assert.Equal(90, start.RotationDegrees, Tolerance);
    }

    [Fact]
    public void Karaoke_highlights_the_word_that_sounds_and_prepares_every_variant()
    {
        var b = new TimelineBuilder(1, 0);
        var style = new TextStyle { Size = 96, Highlight = new StyleHighlight { Color = "#FFD400" } };
        Clip clip = b.AddTitle(0, S(10), S(3), "Hola a\ntodos", style);
        clip.SourceIn = S(1);
        clip.SourceOut = S(4);
        clip.Generator!.Words =
        [
            new GeneratorWord { Start = S(1.2), End = S(1.6), Text = "Hola" },
            new GeneratorWord { Start = S(1.6), End = S(1.8), Text = "a" },
            new GeneratorWord { Start = S(2), End = S(2.5), Text = "todos" },
        ];

        GeneratedClipPlan title = Single(b);
        Assert.Equal(4, title.Contents.Count);
        Assert.Equal(-1, title.ContentAt(S(10.1)).HighlightStart);
        Assert.Equal((0, 4), (title.ContentAt(S(10.2)).HighlightStart, title.ContentAt(S(10.2)).HighlightLength));
        Assert.Equal(5, title.ContentAt(S(10.7)).HighlightStart);
        Assert.Equal((7, 5), (title.ContentAt(S(12.9)).HighlightStart, title.ContentAt(S(12.9)).HighlightLength));
        Assert.Same(title.ContentAt(S(11.5)), title.Layer(0, S(11.5), new EffectListRing(), null).Generated);

        // Sin el estilo «palabra resaltada» no hay variantes.
        clip.Generator.Style!.Highlight = null;
        Assert.Single(Single(b).Contents);
    }

    [Fact]
    public void Style_units_scale_to_the_sequence_and_the_raster_scale_covers_the_largest_size()
    {
        var b = new TimelineBuilder(1, 0);
        b.Sequence.Width = 1080;
        b.Sequence.Height = 1920;
        TextStyle style = Animated();
        style.In!.Curves[T.Scale] = [new StyleKey(0, 1.5), new StyleKey(0.4, 1)];
        Clip clip = b.AddTitle(0, Time.Zero, S(3), "Vertical", style);
        clip.Generator!.Box = new TextFrame { WidthFraction = 0.8 };
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Scale, 2)));

        GeneratedClipPlan title = Single(b);
        double unit = 1080.0 / 1920;
        Assert.Equal(unit, title.Content.UnitScale, Tolerance);
        Assert.Equal(40 * unit, title.TransformAt(Time.Zero).Y / 2, 9);
        Assert.Equal(0.8 * 1080, title.Content.BoxWidth, Tolerance);
        Assert.Equal(3, title.Content.RasterScale, Tolerance);

        clip.Generator.Box = new TextFrame { Width = 1600 };
        Assert.Equal(1600 * unit, Single(b).Content.BoxWidth, Tolerance);
    }

    [Fact]
    public void Disabled_edges_unknown_kinds_and_disabled_clips_draw_nothing_extra()
    {
        var b = new TimelineBuilder(1, 0);
        Clip still = b.AddTitle(0, Time.Zero, S(3), "Quieto", Animated());
        still.Generator!.Edges = new GeneratorEdges { In = false, Out = false };
        Clip unknown = b.AddTitle(0, S(3), S(1), "Forma");
        unknown.Generator!.Kind = "shape";
        Clip off = b.AddTitle(0, S(4), S(1), "Apagado");
        off.Enabled = false;

        GeneratedClipPlan title = Single(b);
        Assert.Null(title.Animation);
        Assert.Equal(LayerTransform.Identity, title.TransformAt(Time.Zero));
    }

    [Fact]
    public void A_transition_against_a_title_is_a_plain_cut()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2), "video");
        b.AddTitle(0, S(2), S(2), "Título", id: "titulo");
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, "video", "titulo", S(0.5));

        TimelinePlan plan = b.Plan();
        Assert.Null(plan.Layers[0].Companion);
        Assert.Null(plan.Layers[0].Clips[0].Outgoing);
        Assert.Equal(S(2), plan.Layers[0].Clips[0].End);
    }

    [Fact]
    public void The_descriptor_is_built_once_per_clip_and_matches_across_reloads()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddTitle(0, Time.Zero, S(3), "Igual", Animated());
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        GeneratedContent first;
        long index;
        using (CompositionFrame a = session.Tick())
        using (CompositionFrame c = session.Tick())
        {
            first = a.Layers[0].Generated!;
            index = a.Layers[0].Frame.Index;
            Assert.Same(first, c.Layers[0].Generated);
            Assert.Equal(a.Layers[0].Frame.Index, c.Layers[0].Frame.Index);
        }

        session.Reload(b.Project);
        using (CompositionFrame reloaded = session.Tick())
        {
            Assert.Equal(first, reloaded.Layers[0].Generated);
            Assert.Equal(first.Key, reloaded.Layers[0].Generated!.Key);
        }

        b.Sequence.VideoTracks[0].Clips[0].Generator!.Text = "Distinto";
        session.Reload(b.Project);
        using CompositionFrame edited = session.Tick();
        Assert.NotEqual(first, edited.Layers[0].Generated);
        Assert.NotEqual(index, edited.Layers[0].Frame.Index);
    }

    [Fact]
    public void After_a_seek_the_composition_waits_for_the_visible_title_and_playing_counts_it_late()
    {
        var backend = new FakeTimelineBackend();
        var titles = new FakeTitles();
        var b = new TimelineBuilder(1, 0);
        b.AddTitle(0, S(1), S(2), "Uno", id: "uno");
        b.AddTitle(0, S(4), S(2), "Dos", id: "dos");
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { Titles = titles });

        session.Seek(S(1.5));
        using (CompositionFrame waiting = session.Tick())
        {
            Assert.True(session.IsSeekPending);
            Assert.Empty(waiting.Layers);
        }

        Assert.Contains("Uno", titles.Requested);
        Assert.DoesNotContain("Dos", titles.Requested);
        titles.Ready.Add("Uno");
        using (CompositionFrame ready = session.Tick())
        {
            Assert.False(session.IsSeekPending);
            Assert.Equal("uno", Assert.Single(ready.Layers).ClipId);
        }

        // A 2 s del segundo título ya se pide; si al llegar no está, el cuadro sale y se cuenta tarde.
        session.Seek(S(2.5));
        session.Tick().Dispose();
        Assert.Contains("Dos", titles.Requested);
        session.Seek(S(3.9));
        session.Tick().Dispose();
        Assert.False(session.IsSeekPending);
        session.Play();
        for (int i = 0; i < 20; i++)
        {
            backend.Clock.Advance(S(1 / 30.0));
            session.Tick().Dispose();
        }

        Assert.True(session.Stats.LateTitles > 0);
        output.WriteLine($"Refrescos con el título tarde: {session.Stats.LateTitles}");
    }

    [Fact]
    public void A_title_tick_reserves_no_more_than_a_video_layer_tick()
    {
        long video = TickBytes(b =>
        {
            b.Builder.AddMedia("m", Rational.Fps30, 3000, backend: b.Backend);
            Clip clip = b.Builder.AddVideo(0, "m", Time.Zero, Time.Zero, S(10));
            clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Y, 100)));
            clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Blur, (BuiltInEffects.BlurParameters.Radius, 4)));
        });
        long title = TickBytes(b =>
        {
            Clip clip = b.Builder.AddTitle(0, Time.Zero, S(10), "Título con animación", Animated());
            clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Y, 100)));
            clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Blur, (BuiltInEffects.BlurParameters.Radius, 4)));
        });

        output.WriteLine($"Por Tick: capa de video {video} B, título {title} B");
        Assert.True(title <= video, $"Un título reserva {title} B por Tick y una capa de video {video} B.");
    }

    [Fact]
    public void Export_renders_the_same_title_layers_as_the_settled_preview()
    {
        var backend = new FakeTimelineBackend();
        var titles = new FakeTitles { AlwaysReady = true };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, S(1), S(3), "video");
        b.AddTitle(0, S(3), S(1), "Final", Animated(), id: "final");
        Clip over = b.AddTitle(1, S(0.5), S(2), "Encima", Animated(), id: "encima");
        over.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Y, 380), (T.Rotation, 10)));
        EffectInstance blend = TimelineEffectsTests.Effect(BuiltInEffects.Blend);
        blend.Options[BuiltInEffects.BlendOptions.Mode] = "screen";
        over.Effects.Add(blend);
        using TimelineSession preview = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { Titles = titles });
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Titles = titles });

        int titleLayers = 0;
        for (long f = 0; f < offline.FrameCount; f++)
        {
            preview.SeekToFrame(f);
            using CompositionFrame expected = Settle(preview, backend);
            using CompositionFrame actual = offline.Render(f, CancellationToken.None);
            Assert.Equal(OfflineProjects.Describe(expected), OfflineProjects.Describe(actual));
            titleLayers += actual.Layers.Count(l => l.Generated is not null);
        }

        Assert.Equal(30 + 60, titleLayers);
        Assert.Equal(2, offline.GeneratedContents(Time.Zero, offline.Duration).Count);
        Assert.Contains("Final", titles.Requested);
    }

    private static GeneratedClipPlan Single(TimelineBuilder b) => Assert.Single(b.Plan().Layers[0].Generated);

    private static CompositionFrame Settle(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; ; i++)
        {
            backend.RunDecoders();
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
            Assert.True(i < 20, "La vista previa no llegó a la composición completa.");
        }
    }

    private static long TickBytes(Action<(TimelineBuilder Builder, FakeTimelineBackend Backend)> build)
    {
        var backend = new FakeTimelineBackend();
        var builder = new TimelineBuilder(1, 0);
        build((builder, backend));
        using TimelineSession session = TimelineSessionTests.Open(builder, backend,
            new TimelineSessionOptions { Titles = new FakeTitles { AlwaysReady = true }, EnableAudio = false });
        session.Seek(S(0.5));
        Settle(session, backend).Dispose();
        for (int i = 0; i < 50; i++)
        {
            session.Tick().Dispose();
        }

        const int Ticks = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Ticks; i++)
        {
            session.Tick().Dispose();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Ticks;
    }


    /// <summary>Rasterizador falso: listo lo que diga <see cref="Ready"/> (o todo), y anota lo que se pidió.</summary>
    internal sealed class FakeTitles : IGeneratedContentCache
    {
        public HashSet<string> Ready { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Requested { get; } = new(StringComparer.Ordinal);

        public bool AlwaysReady { get; init; }

        public bool Prepare(GeneratedContent content)
        {
            lock (Requested)
            {
                Requested.Add(content.Text);
                return AlwaysReady || Ready.Contains(content.Text);
            }
        }
    }
}
